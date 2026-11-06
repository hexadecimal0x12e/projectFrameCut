using System.Text;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

public sealed record EffectGroupLibraryItem(string Hash, byte[] Content, EffectGroupTemplate? Template, string? Error)
{
    public string Name => Template?.Groups.FirstOrDefault(g => g.Id == Template.RootGroupId)?.Name ?? Hash[..Math.Min(8, Hash.Length)];
}

public sealed class EffectGroupValidationException(EffectGroupTemplate template, IReadOnlyList<EffectBindingHelper.BindingDiagnostic> diagnostics)
    : InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)))
{
    public IReadOnlyList<EffectBindingHelper.BindingDiagnostic> Diagnostics { get; } = diagnostics;

    public string NodeName(EffectBindingHelper.BindingDiagnostic diagnostic)
    {
        var p = template.Providers.FirstOrDefault(p => p.Id == diagnostic.ProviderId);
        var name = p is null ? Localized.Effect_BindingsGraph : string.IsNullOrWhiteSpace(p.Name) ? p.TypeName : p.Name;
        return diagnostic.FieldId is { } field ? $"{name} · {field}" : name;
    }

    public string Describe(EffectBindingHelper.BindingDiagnostic diagnostic)
    {
        var message = diagnostic.Message;
        foreach (var p in template.Providers)
            message = message.Replace(p.Id.ToString(), string.IsNullOrWhiteSpace(p.Name) ? p.TypeName : p.Name, StringComparison.OrdinalIgnoreCase);
        return message;
    }
}

public static class EffectGroupStore
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static event Action? Changed;
    public static string DirectoryPath => Path.Combine(MauiProgram.DataPath, "EffectGroups");
    public static string Hash(byte[] content) => EffectGroupHelper.ContentHash(content);

    public static EffectGroupTemplate Parse(byte[] content)
    {
        var t = EffectGroupMermaid.Parse(new UTF8Encoding(false, true).GetString(content));
        var factories = EffectHelper.EffectsProviderEnum;
        if (t.Providers.All(p => factories.ContainsKey(p.TypeName))) Restore(t);
        else
        {
            // Available providers must still satisfy restrictions even if another plugin is missing.
            foreach (var p in t.Providers.Where(p => factories.ContainsKey(p.TypeName)))
            {
                var provider = factories[p.TypeName]();
                if (provider is ClipArgumentProvider or RenderOutputProvider or IClipValueEndpointProvider
                    || provider.TypeOfEffect == EffectType.Transform)
                    throw new EffectGroupValidationException(t, [new(p.Id, "ForbiddenGroupNode", Localized.EffectGroup_ForbiddenNode)]);
            }
        }
        return t;
    }

    public static Dictionary<Guid, IEffectProvider> Restore(EffectGroupTemplate t)
    {
        EffectGroupHelper.ValidateTemplate(t);
        var providers = EffectBindingHelper.MigrateToEffectProviders(EffectGroupHelper.Copy(t.Providers), null, out var diagnostics);
        if (providers.Count != t.Providers.Length || diagnostics.Count != 0)
        {
            var problems = diagnostics.ToList();
            problems.AddRange(t.Providers.Where(p => !providers.ContainsKey(p.Id) && !problems.Any(d => d.ProviderId == p.Id))
                .Select(p => new EffectBindingHelper.BindingDiagnostic(p.Id, "UnavailableProvider", $"Cannot load provider '{p.TypeName}' from '{p.FromPlugin}'.")));
            throw new EffectGroupValidationException(t, problems);
        }
        try { EffectGroupMermaid.ValidatePorts(t, providers); }
        catch (EffectGroupMermaid.ValidationException ex) { throw new EffectGroupValidationException(t, ex.Diagnostics); }
        EffectGroupHelper.Validate(t.Groups, providers);
        var errors = EffectBindingHelper.ValidateBindings(providers, forward: true);
        if (errors.Count != 0) throw new EffectGroupValidationException(t, errors);
        return providers;
    }

    public static Dictionary<Guid, IEffectProvider> InspectForEditing(EffectGroupTemplate t,
        out IReadOnlyList<EffectBindingHelper.BindingDiagnostic> diagnostics)
    {
        var problems = new List<EffectBindingHelper.BindingDiagnostic>();
        try { EffectGroupHelper.ValidateTemplate(t); }
        catch (InvalidOperationException ex) { problems.Add(new(null, "InvalidGroupTemplate", ex.Message)); }
        // Keep invalid bindings intact so the import editor can display and repair them.
        var providers = EffectBindingHelper.MigrateToEffectProviders(EffectGroupHelper.Copy(t.Providers), null,
            out var restoration, normalizeBindings: false);
        problems.AddRange(restoration);
        foreach (var p in providers.Values)
            if (!EffectGroupHelper.CanGroup(p, providers))
                problems.Add(new(p.Id, "ForbiddenGroupNode", Localized.EffectGroup_ForbiddenNode));
        try { EffectGroupMermaid.ValidatePorts(t, providers); }
        catch (EffectGroupMermaid.ValidationException ex) { problems.AddRange(ex.Diagnostics); }
        if (providers.Count == t.Providers.Length)
        {
            try { EffectGroupHelper.Validate(t.Groups, providers); }
            catch (InvalidOperationException ex) { problems.Add(new(null, "InvalidGroup", ex.Message)); }
        }
        problems.AddRange(EffectBindingHelper.ValidateBindings(providers, forward: true));
        diagnostics = problems.Distinct().ToArray();
        return providers;
    }

    public static Task<List<EffectGroupLibraryItem>> LoadAsync() => Task.Run(() =>
    {
        var items = new List<EffectGroupLibraryItem>();
        if (!Directory.Exists(DirectoryPath)) return items;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.pjfcEBG"))
        {
            byte[] content = [];
            string hash = Path.GetFileNameWithoutExtension(path);
            try
            {
                content = File.ReadAllBytes(path);
                if (hash != Hash(content)) throw new InvalidOperationException(Localized.EffectGroup_HashMismatch);
                var template = Parse(content);
                string? error = null;
                try { Restore(template); }
                catch (Exception ex) { error = ex.Message; }
                items.Add(new(hash, content, template, error));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Load effect group {path}", typeof(EffectGroupStore));
                items.Add(new(hash, content, null, ex.Message));
            }
        }
        return items.OrderBy(i => i.Name).ThenBy(i => i.Hash).ToList();
    });

    public static async Task<string> SaveAsync(byte[] content, string? replaceHash = null)
    {
        await gate.WaitAsync();
        try
        {
            var hash = await Task.Run(() =>
            {
                Parse(content);
                var h = Hash(content);
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, h + ".pjfcEBG");
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                {
                    var temporary = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
                    try
                    {
                        File.WriteAllBytes(temporary, content);
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
        return Path.Combine(DirectoryPath, hash + ".pjfcEBG");
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
        Encoding.UTF8.GetBytes(EffectGroupMermaid.Serialize(template));

    public static async Task<string?> SaveWithChoiceAsync(byte[] content, Page page)
    {
        var template = await Task.Run(() => Parse(content));
        var hash = Hash(content);
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
        return await SaveAsync(content, replace);
    }

    public static async Task ExportAsync(EffectGroupLibraryItem item)
    {
        var content = item.Template is null ? item.Content : Serialize(item.Template);
        using var stream = new MemoryStream(content, writable: false);
        await FileSystemService.SaveAFile(Hash(content) + ".pjfcEBG", stream);
    }
}
