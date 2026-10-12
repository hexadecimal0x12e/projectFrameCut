using System.Text.Json;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

public sealed record EffectGroupLibraryItem(string Hash, byte[] Json, EffectGroupTemplate? Template, string? Error)
{
    public string Name => Template?.Groups.FirstOrDefault(g => g.Id == Template.RootGroupId)?.Name ?? Hash[..Math.Min(8, Hash.Length)];
}

public static class EffectGroupStore
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static event Action? Changed;
    public static string DirectoryPath => Path.Combine(MauiProgram.DataPath, "EffectGroups");
    public static string Hash(byte[] json) => EffectGroupHelper.JsonHash(json);

    public static EffectGroupTemplate Parse(byte[] json)
    {
        ReadOnlySpan<byte> content = json;
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
        var t = JsonSerializer.Deserialize<EffectGroupTemplate>(content) ?? throw new InvalidOperationException("Empty effect group JSON.");
        EffectGroupHelper.ValidateTemplate(t);
        var factories = EffectHelper.EffectsProviderEnum;
        if (t.Providers.All(p => factories.ContainsKey(p.TypeName))) Restore(t);
        else
        {
            // Available providers must still satisfy restrictions even if another plugin is missing.
            foreach (var p in t.Providers.Where(p => factories.ContainsKey(p.TypeName)))
                if (factories[p.TypeName]() is ClipArgumentProvider or IClipValueEndpointProvider)
                    throw new InvalidOperationException(Localized.EffectGroup_ForbiddenNode);
        }
        return t;
    }

    public static Dictionary<Guid, IEffectProvider> Restore(EffectGroupTemplate t)
    {
        EffectGroupHelper.ValidateTemplate(t);
        var providers = EffectBindingHelper.MigrateToEffectProviders(t.Providers, null, out var diagnostics);
        if (providers.Count != t.Providers.Length || diagnostics.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        EffectGroupHelper.Validate(t.Groups, providers);
        var errors = EffectBindingHelper.ValidateBindings(providers);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(d => d.Message)));
        return providers;
    }

    public static Task<List<EffectGroupLibraryItem>> LoadAsync() => Task.Run(() =>
    {
        var items = new List<EffectGroupLibraryItem>();
        if (!Directory.Exists(DirectoryPath)) return items;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            byte[] json = [];
            string hash = Path.GetFileNameWithoutExtension(path);
            try
            {
                json = File.ReadAllBytes(path);
                if (hash != Hash(json)) throw new InvalidOperationException(Localized.EffectGroup_HashMismatch);
                var template = Parse(json);
                string? error = null;
                try { Restore(template); }
                catch (Exception ex) { error = ex.Message; }
                items.Add(new(hash, json, template, error));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Load effect group {path}", typeof(EffectGroupStore));
                items.Add(new(hash, json, null, ex.Message));
            }
        }
        return items.OrderBy(i => i.Name).ThenBy(i => i.Hash).ToList();
    });

    public static async Task<string> SaveAsync(byte[] json, string? replaceHash = null)
    {
        await gate.WaitAsync();
        try
        {
            var hash = await Task.Run(() =>
            {
                Parse(json);
                var h = Hash(json);
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, h + ".json");
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(json))
                {
                    var temporary = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
                    try
                    {
                        File.WriteAllBytes(temporary, json);
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                if (replaceHash is not null && replaceHash != h) File.Delete(ItemPath(replaceHash));
                return h;
            });
            Logger.Log($"Saved effect group {hash}.", "info");
            Changed?.Invoke();
            return hash;
        }
        finally { gate.Release(); }
    }

    private static string ItemPath(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidOperationException("Invalid effect group hash.");
        return Path.Combine(DirectoryPath, hash + ".json");
    }

    public static async Task DeleteAsync(string hash)
    {
        await gate.WaitAsync();
        try
        {
            await Task.Run(() => File.Delete(ItemPath(hash)));
            Logger.Log($"Deleted effect group {hash}.", "info");
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public static byte[] Serialize(EffectGroupTemplate template) =>
        JsonSerializer.SerializeToUtf8Bytes(template, new JsonSerializerOptions { WriteIndented = true });

    public static async Task<string?> SaveWithChoiceAsync(byte[] json, Page page)
    {
        var template = await Task.Run(() => Parse(json));
        var hash = Hash(json);
        var name = template.Groups.Single(g => g.Id == template.RootGroupId).Name;
        var items = await LoadAsync();
        if (items.Any(i => i.Hash == hash))
        {
            await page.DisplayAlertAsync(Localized._Info, Localized.EffectGroup_AlreadyExists, Localized._OK);
            return null;
        }
        string? replace = null;
        var matching = items.Where(i => i.Template is not null && i.Name == name).ToArray();
        if (matching.Length > 0)
        {
            var choice = await page.DisplayActionSheetAsync(Localized.EffectGroup_NameConflict, Localized._Cancel, null,
                Localized.EffectGroup_KeepBoth, Localized.EffectGroup_Replace);
            if (choice == Localized.EffectGroup_Replace)
            {
                var options = matching.Select(i => $"{i.Name} · {i.Hash}").ToArray();
                var selected = await page.DisplayActionSheetAsync(Localized.EffectGroup_Replace, Localized._Cancel, null, options);
                int index = Array.IndexOf(options, selected);
                if (index < 0) return null;
                replace = matching[index].Hash;
            }
            else if (choice != Localized.EffectGroup_KeepBoth) return null;
        }
        return await SaveAsync(json, replace);
    }

    public static async Task ExportAsync(EffectGroupLibraryItem item)
    {
        using var stream = new MemoryStream(item.Json, writable: false);
        await FileSystemService.SaveAFile(item.Hash + ".json", stream);
    }
}
