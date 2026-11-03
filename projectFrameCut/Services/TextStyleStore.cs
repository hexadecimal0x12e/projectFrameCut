#if !HEADLESS
using System.Text.Json;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Text;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

public sealed class TextClipPreset
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string SampleText { get; set; } = "";
    public string? LegacyKey { get; set; }
    public TextClipStyleState Style { get; set; } = new();
    public ClipDraftDTO Clip { get; set; } = new() { ClipType = ClipMode.TextClip, Opacity = 1 };
}

public sealed record TextStyleLibraryItem(TextClipPreset Preset, string? Error);

public static class TextStyleStore
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public static event Action? Changed;
    private static string DirectoryPath => Path.Combine(MauiProgram.BasicDataPath, "TextStyles");
    public static byte[] Serialize(TextClipPreset preset) => JsonSerializer.SerializeToUtf8Bytes(preset, DraftImportAndExportHelper.DraftJSONOption);
    public static TextClipPreset Copy(TextClipPreset preset) => Parse(Serialize(preset));

    public static TextClipPreset Parse(byte[] json)
    {
        ReadOnlySpan<byte> content = json;
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
        var preset = JsonSerializer.Deserialize<TextClipPreset>(content, DraftImportAndExportHelper.DraftJSONOption)
            ?? throw new InvalidOperationException("Empty text style file.");
        Validate(preset);
        return preset;
    }

    private static void Validate(TextClipPreset preset)
    {
        if (preset.Version != 1 || preset.Id == Guid.Empty || string.IsNullOrWhiteSpace(preset.Name)
            || preset.Style is null || preset.Style.Version != 1 || preset.Style.Fields is null || preset.Style.Bindings is null
            || preset.Style.MetaData is null || string.IsNullOrWhiteSpace(preset.Style.FromPlugin) || string.IsNullOrWhiteSpace(preset.Style.TypeName)
            || preset.Clip is null || preset.Clip.ClipType != ClipMode.TextClip)
            throw new InvalidOperationException("Invalid text style file or version.");
        var providers = preset.Clip.EffectProviders ?? [];
        var ids = providers.Select(p => p.Id).ToHashSet();
        if (ids.Count != providers.Length) throw new InvalidOperationException("Duplicate text style provider ID.");
        foreach (var binding in providers.SelectMany(p => p.AnchorsBindingState
            .Where(b => b.Key != EffectProviderAnchorExtensions.OutputFieldKey).Select(b => b.Value)).Concat(preset.Style.Bindings.Values))
        {
            if (ValueProviderFrameContext.IsBuiltInSource(binding)) continue;
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var id, out _)
                || id != IEffectProvider.NoConnectionGUID && id != IEffectProvider.InputAnchorGUID
                    && id != IEffectProvider.OutputAnchorGUID && !ids.Contains(id))
                throw new InvalidOperationException(Localized.TextStyles_ExternalReference);
        }
        foreach (var p in providers)
        {
            var source = p.MetaData?.GetValueOrDefault("SourceClipId")?.ToString();
            if (Guid.TryParse(source, out var clipId) && clipId != Guid.Empty && clipId != preset.Clip.Id)
                throw new InvalidOperationException(Localized.TextStyles_ExternalReference);
            if (Guid.TryParse(p.MetaData?.GetValueOrDefault("SourceEndpointId")?.ToString(), out var endpoint)
                && endpoint != Guid.Empty && !ids.Contains(endpoint))
                throw new InvalidOperationException(Localized.TextStyles_ExternalReference);
        }
        var groups = EffectGroupHelper.Read(preset.Clip.MetaData ?? new());
        foreach (var group in groups)
            if (EffectGroupHelper.Members(group, groups).Any(id => !ids.Contains(id)))
                throw new InvalidOperationException(Localized.TextStyles_ExternalReference);
    }

    public static ITextClipStyleProvider Restore(TextClipPreset preset, string? text = null)
    {
        var provider = TextClipStyles.Create(preset.Style.FromPlugin, preset.Style.TypeName)
            ?? throw new InvalidOperationException($"{Localized.TextStyles_MissingPlugin}: {preset.Style.FromPlugin} / {preset.Style.TypeName}");
        provider.ImportStyle(preset.Style);
        provider.BasicText = text ?? preset.SampleText;
        return provider;
    }

    public static string? GetError(TextClipPreset preset)
    {
        try
        {
            Restore(preset);
            var providers = EffectBindingHelper.MigrateToEffectProviders(preset.Clip.EffectProviders, null, out var diagnostics);
            if (diagnostics.Count != 0 || providers.Count != (preset.Clip.EffectProviders?.Length ?? 0))
                throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
            foreach (var effect in preset.Clip.Effects ?? [])
                if (!projectFrameCut.Render.Plugin.PluginManager.LoadedPlugins.ContainsKey(effect.FromPlugin))
                    throw new InvalidOperationException($"{Localized.TextStyles_MissingPlugin}: {effect.FromPlugin}");
            CheckFiles(JsonSerializer.SerializeToElement(preset.Style));
            CheckFiles(JsonSerializer.SerializeToElement(preset.Clip));
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void CheckFiles(JsonElement value, string key = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject()) CheckFiles(p.Value, p.Name);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckFiles(item, key);
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            if (key == "TextEntriesJson")
            {
                using var document = JsonDocument.Parse(text);
                CheckFiles(document.RootElement);
            }
            else if ((key.Contains("Path", StringComparison.OrdinalIgnoreCase) || key is "FontFamily" or "FontName" or "FileName")
                && Path.IsPathRooted(text) && !File.Exists(text) && !Directory.Exists(text))
                throw new FileNotFoundException($"{Localized.TextStyles_MissingFile}: {text}");
        }
    }

    public static async Task<List<TextStyleLibraryItem>> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                MigrateLegacy();
                var items = new List<TextStyleLibraryItem>();
                if (!Directory.Exists(DirectoryPath)) return items;
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
                {
                    try
                    {
                        var preset = Parse(File.ReadAllBytes(path));
                        if (Path.GetFileNameWithoutExtension(path) != preset.Id.ToString()) throw new InvalidOperationException("Text style ID mismatch.");
                        items.Add(new(preset, GetError(preset)));
                    }
                    catch (Exception ex) { Logger.Log(ex, $"Load text style {path}", typeof(TextStyleStore)); }
                }
                return items.OrderBy(p => p.Preset.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            });
        }
        finally { gate.Release(); }
    }

    public static async Task SaveAsync(TextClipPreset preset)
    {
        Validate(preset);
        await gate.WaitAsync();
        try { await Task.Run(() => Write(preset)); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    private static void Write(TextClipPreset preset)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temp = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
        try
        {
            File.WriteAllBytes(temp, Serialize(preset));
            File.Move(temp, Path.Combine(DirectoryPath, preset.Id + ".json"), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task DeleteAsync(Guid id)
    {
        await gate.WaitAsync();
        try { await Task.Run(() => File.Delete(Path.Combine(DirectoryPath, id + ".json"))); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }

    public static async Task ImportAsync(Page page)
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.TextStyles_Import });
        if (file is null) return;
        using var stream = await file.OpenReadAsync();
        using var content = new MemoryStream();
        await stream.CopyToAsync(content);
        var preset = Parse(content.ToArray());
        preset.Id = Guid.NewGuid();
        preset.LegacyKey = null;
        StripText(preset);
        var names = (await LoadAsync()).Select(i => i.Preset.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        string name = preset.Name;
        for (int i = 2; names.Contains(preset.Name); i++) preset.Name = $"{name} ({i})";
        await SaveAsync(preset);
        if (GetError(preset) is { } error) await page.DisplayAlertAsync(Localized._Warn, error, Localized._OK);
        else
        {
            var missing = Restore(preset).BuildEntries().Select(e => e.FontName).Distinct()
                .Where(font => !TextServices.LoadedFonts.ContainsKey(font)
                    && !projectFrameCut.Render.ClipsAndTracks.Text.TextClipFontRegistry.TryGetFont(font, out _)).ToArray();
            if (missing.Length > 0) await page.DisplayAlertAsync(Localized._Warn,
                Localized.TextStyles_MissingFont + Environment.NewLine + string.Join(Environment.NewLine, missing), Localized._OK);
        }
    }

    public static async Task ExportAsync(TextClipPreset preset)
    {
        using var stream = new MemoryStream(Serialize(preset));
        string name = string.Concat(preset.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        await FileSystemService.SaveAFile(name + ".pjfctextstyle", stream);
    }

    public static TextClipPreset Capture(DraftPage page, ClipElementUI clip, ITextClipStyleProvider style)
    {
        ClipInfoBuilder.SaveTextStyleClipArguments(clip);
        style = TextClipStyles.Restore(clip.ExtraData) ?? style;
        var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip);
        var preset = FromProvider(style, clip.DisplayName);
        preset.Clip = dto;
        dto.StartFrame = dto.LayerIndex = dto.SubLayerIndex = 0;
        dto.BindedSoundTrack = "";
        dto.FilePath = null;
        dto.MetaData = dto.MetaData?.Where(p => p.Key == EffectGroupHelper.MetadataKey).ToDictionary() ?? new();
        StripText(preset);
        Validate(preset);
        return Copy(preset);
    }

    public static TextClipPreset FromProvider(ITextClipStyleProvider provider, string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? provider.TypeName : name, SampleText = provider.BasicText, Style = provider.ExportStyle(),
    };

    public static void StripText(TextClipPreset preset)
    {
        preset.Style.Fields.Remove("Text");
        preset.Style.Bindings.Remove("Text");
        preset.Style.MetaData.Remove("Text");
        foreach (var p in preset.Clip.EffectProviders ?? [])
        {
            if (p.TypeName != ClipArgumentProvider.ProviderTypeName) continue;
            p.StaticFields?.Remove(TextClipStyles.ArgumentPrefix + "Text");
            p.AnchorsBindingState.Remove(TextClipStyles.ArgumentPrefix + "Text");
        }
    }

    public static void SetStyle(TextClipPreset preset, ITextClipStyleProvider provider)
    {
        preset.Style = provider.ExportStyle();
        foreach (var p in preset.Clip.EffectProviders ?? [])
        {
            if (p.TypeName != ClipArgumentProvider.ProviderTypeName || p.StaticFields is null) continue;
            foreach (var key in p.StaticFields.Keys.Where(k => k.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)).ToArray())
                if (preset.Style.Fields.TryGetValue(key[TextClipStyles.ArgumentPrefix.Length..], out var value)) p.StaticFields[key] = value;
        }
    }

    public static TextClipPreset Instantiate(TextClipPreset source)
    {
        var preset = Copy(source);
        var providers = preset.Clip.EffectProviders ?? [];
        var groups = EffectGroupHelper.Read(preset.Clip.MetaData ?? new());
        var ids = providers.Select(p => p.Id).Concat(groups.Select(g => g.Id)).Append(preset.Clip.Id).Where(id => id != Guid.Empty)
            .Distinct().ToDictionary(id => id, _ => Guid.NewGuid());
        string Remap(string source)
        {
            if (source == IEffectProvider.NoConnectionGUID.ToString() || source == IEffectProvider.InputAnchorGUID.ToString()
                || source == IEffectProvider.OutputAnchorGUID.ToString()) return source;
            if (EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var output) && ids.TryGetValue(id, out var mapped))
                return output is null ? mapped.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(mapped, output);
            return source;
        }
        foreach (var p in providers)
        {
            p.Id = ids[p.Id];
            foreach (var key in p.AnchorsBindingState.Keys.Where(k => k != EffectProviderAnchorExtensions.OutputFieldKey).ToArray())
                p.AnchorsBindingState[key] = Remap(p.AnchorsBindingState[key]);
            foreach (var key in new[] { "SourceClipId", "SourceEndpointId" })
                if (p.MetaData?.TryGetValue(key, out var value) == true) p.MetaData[key] = Remap(value.ToString() ?? "");
        }
        foreach (var key in preset.Style.Bindings.Keys.ToArray()) preset.Style.Bindings[key] = Remap(preset.Style.Bindings[key]);
        foreach (var effect in preset.Clip.Effects ?? [])
        {
            effect.Id = Guid.NewGuid().ToString();
            effect.BindedEffectGroupID = Remap(effect.BindedEffectGroupID);
        }
        foreach (var g in groups)
        {
            g.Id = ids[g.Id];
            g.ProviderIds = g.ProviderIds.Select(id => ids[id]).ToList();
            g.ChildGroupIds = g.ChildGroupIds.Select(id => ids[id]).ToList();
            foreach (var p in g.Ports) p.ProviderId = ids[p.ProviderId];
        }
        preset.Clip.Id = ids.GetValueOrDefault(preset.Clip.Id, Guid.NewGuid());
        preset.Clip.MetaData ??= new();
        EffectGroupHelper.Write(preset.Clip.MetaData, groups);
        return preset;
    }

    private static void MigrateLegacy()
    {
        string path = Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json");
        if (!File.Exists(path)) return;
        string marker = Path.Combine(DirectoryPath, "legacy-imported.txt");
        try
        {
            var imported = File.Exists(marker) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(marker)) ?? new() : new HashSet<string>();
            var templates = JsonSerializer.Deserialize<Dictionary<string, TextClipEntry>>(File.ReadAllText(path)) ?? new();
            foreach (var (key, entry) in templates)
            {
                if (imported.Contains(key)) continue;
                var provider = TextStyleServices.FromLegacyEntry(entry);
                var preset = FromProvider(provider, key);
                if (string.IsNullOrWhiteSpace(preset.SampleText)) preset.SampleText = key;
                // Stable IDs make interrupted migrations safe to repeat.
                preset.Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("TextTemplate:" + key)).AsSpan(0, 16));
                preset.LegacyKey = key;
                StripText(preset);
                if (!File.Exists(Path.Combine(DirectoryPath, preset.Id + ".json"))) Write(preset);
                imported.Add(key);
            }
            Directory.CreateDirectory(DirectoryPath);
            string temp = Path.Combine(DirectoryPath, Guid.NewGuid() + ".tmp");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(imported));
                File.Move(temp, marker, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception ex) { Logger.Log(ex, "Migrate text templates", typeof(TextStyleStore)); }
    }
}
#endif
