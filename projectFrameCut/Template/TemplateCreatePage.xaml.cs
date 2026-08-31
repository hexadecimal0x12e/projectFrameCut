using projectFrameCut.Asset;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Template;

public partial class TemplateCreatePage : ContentView
{
    private readonly ObservableCollection<TemplateVariableItem> _allVariables = [];
    private readonly ObservableCollection<TemplateVariableItem> _filteredVariables = [];
    private ITemplateStructure? _template;
    private JsonObject _projectNode = new();
    private JsonObject _draftNode = new();
    private readonly Dictionary<string, TemplateVariableDefinition> _variableDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private bool _isBusy;
    private bool _isTemplateInputMode;
    private TaskCompletionSource<Dictionary<string, string?>?>? _templateInputCompletion;
    private TaskCompletionSource<AssetItem?>? _assetPickerCompletion;

    public event EventHandler? CloseRequested;

    public TemplateCreatePage()
    {
        InitializeComponent();
        InlineAssetPicker.IsDoubleTapPreviewEnabled = false;
        VariablesCollectionView.ItemsSource = _filteredVariables;
        WireInlineAssetPicker();
        ConfigureForProjectCreationMode();
        RefreshStats();
    }

    public TemplateCreatePage(ITemplateStructure template)
    {
        InitializeComponent();
        InlineAssetPicker.IsDoubleTapPreviewEnabled = false;
        ImportControlGrid.IsVisible = false;
        VariablesCollectionView.ItemsSource = _filteredVariables;
        WireInlineAssetPicker();
        LoadTemplateFromStructureAsync(template, template.TemplateName ?? template.TemplateID.ToString());
    }

    private void WireInlineAssetPicker()
    {
        InlineAssetPicker.SelectedAssetChanged += InlineAssetPicker_SelectedAssetChanged;
        InlineAssetPicker.AssetDoubleTapped += InlineAssetPicker_AssetDoubleTapped;
    }

    public void ConfigureForProjectCreationMode()
    {
        _isTemplateInputMode = false;
        ImportTemplateButton.IsVisible = true;
        ProjectNameEntry.IsVisible = true;
        ProjectNameEntry.Placeholder = Localized.TemplateCreatePage_ProjectNamePlaceholder;
        CreateButton.Text = Localized.TemplateCreatePage_CreateProject;
        CancelButton.Text = Localized._Cancel;
    }

    public async Task<Dictionary<string, string?>?> PromptTemplateValuesAsync(ITemplateStructure template, string? templateLabel = null)
    {
        _isTemplateInputMode = true;
        ImportTemplateButton.IsVisible = false;
        ProjectNameEntry.IsVisible = false;

        await LoadTemplateFromStructureAsync(template, templateLabel ?? "");

        _templateInputCompletion = new TaskCompletionSource<Dictionary<string, string?>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return await _templateInputCompletion.Task;
    }

    private async void ImportTemplate_Clicked(object sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            var fileResult = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select JSON file",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                { DevicePlatform.WinUI, [".json", ".zip"] },
                { DevicePlatform.Android, ["application/json", "text/plain", "application/zip", "application/x-zip-compressed"] },
                { DevicePlatform.iOS, ["public.json", "public.plain-text", "public.zip-archive", "com.pkware.zip-archive"] },
                { DevicePlatform.MacCatalyst, ["public.json", "public.plain-text", "public.zip-archive", "com.pkware.zip-archive"] }
                })
            });

            if (fileResult is null || string.IsNullOrWhiteSpace(fileResult.FullPath))
            {
                return;
            }

            SetBusy(true);
            var loadResult = await LoadTemplateFromFileAsync(fileResult.FullPath);
            if (loadResult.ImportedAssetCount > 0)
            {
                await DisplayAlertAsync(Localized._Info, $"Imported assets: {loadResult.ImportedAssetCount}", Localized._OK);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error, $"{Localized.TemplateCreatePage_InvalidTemplate}{Environment.NewLine}{Environment.NewLine}{Localized._ExceptionTemplate(ex)})", Localized._OK);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<TemplateLoadResult> LoadTemplateFromFileAsync(string path)
    {
        var loadResult = await TemplatePackageIO.LoadTemplateAsync(
            path,
            DraftPage.DraftJSONOption,
            installPackagedAssets: false);

        await LoadTemplateFromStructureAsync(loadResult.Template, Localized.TemplateCreatePage_TemplatePath(path));
        return loadResult;
    }

    public Task LoadTemplateFromStructureAsync(ITemplateStructure template, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(template);

        _template = template;

        // 从两种模板类型中提取 Project / Draft
        var (project, draft) = GetProjectDraft(template);
        _projectNode = project is not null
            ? JsonSerializer.SerializeToNode(project, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject()
            : new JsonObject();
        _draftNode = draft is not null
            ? JsonSerializer.SerializeToNode(draft, DraftPage.DraftJSONOption) as JsonObject ?? new JsonObject()
            : new JsonObject();

        _allVariables.Clear();
        _filteredVariables.Clear();
        _variableDefinitions.Clear();

        var map = new Dictionary<string, VariableAggregate>(StringComparer.OrdinalIgnoreCase);
        CollectPlaceholderFields(_projectNode, "project", "Project", map);
        CollectPlaceholderFields(_draftNode, "draft", "Draft", map);

        if (_template.VariableDefinitions is not null)
        {
            foreach (var kv in _template.VariableDefinitions)
            {
                var key = NormalizePlaceholderKey(kv.Key);
                if (string.IsNullOrWhiteSpace(key) || kv.Value is null)
                {
                    continue;
                }

                _variableDefinitions[key] = kv.Value;
                if (!map.TryGetValue(key, out var aggregate))
                {
                    aggregate = new VariableAggregate
                    {
                        Key = key,
                        Type = kv.Value.Type,
                        DefaultValue = kv.Value.DefaultValue
                    };
                    map[key] = aggregate;
                }
                else
                {
                    aggregate.Type = kv.Value.Type;
                    aggregate.DefaultValue ??= kv.Value.DefaultValue;
                }
            }
        }

        if (_template.Variables is not null)
        {
            foreach (var kv in _template.Variables)
            {
                var key = NormalizePlaceholderKey(kv.Key);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!map.TryGetValue(key, out var aggregate))
                {
                    aggregate = new VariableAggregate
                    {
                        Key = key,
                        DefaultValue = kv.Value,
                        Type = TemplateVariableType.Auto
                    };
                    map[key] = aggregate;
                }
                else if (aggregate.DefaultValue is null)
                {
                    aggregate.DefaultValue = kv.Value;
                }

                if (!_variableDefinitions.ContainsKey(key))
                {
                    _variableDefinitions[key] = new TemplateVariableDefinition
                    {
                        Type = aggregate.Type,
                        DefaultValue = aggregate.DefaultValue
                    };
                }
            }
        }

        foreach (var item in map.Values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            var firstPath = item.Paths.FirstOrDefault() ?? "(Variable)";
            if (!_variableDefinitions.TryGetValue(item.Key, out var def))
            {
                def = new TemplateVariableDefinition
                {
                    Type = item.Type,
                    DefaultValue = item.DefaultValue
                };
                _variableDefinitions[item.Key] = def;
            }

            var variable = new TemplateVariableItem(
            item.Key,
            item.Scope,
            firstPath,
            string.Join("; ", item.Paths.Take(3)),
            def.Type,
            item.Type != TemplateVariableType.File ? item.DefaultValue : "",
            item.Type != TemplateVariableType.File ? item.DefaultValue : "");
            _allVariables.Add(variable);
        }

        ApplyFilter();

        var projectName = project?.ProjectName;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = "Template Project";
        }
        ProjectNameEntry.Text = BuildDefaultProjectName(projectName);

        RefreshStats();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从 <see cref="ITemplateStructure"/> 中提取 Project/Draft 数据。
    /// <see cref="ScriptBasedTemplateStructure"/> 和 <see cref="JSONBasedTemplateStructure"/> 都包含这些属性。
    /// </summary>
    private static (ProjectJSONStructure? Project, DraftStructureJSON? Draft) GetProjectDraft(ITemplateStructure template)
    {
        if (template is JSONBasedTemplateStructure json)
            return (json.Project, json.Draft);
        if (template is ScriptBasedTemplateStructure script)
            return (script.Project, script.Draft);
        return (null, null);
    }

    private static string BuildDefaultProjectName(string baseName)
    {
        var now = DateTime.Now;
        return $"{baseName}-{now:MMdd-HHmm}";
    }

    private static void CollectPlaceholderFields(JsonNode? node, string path, string scope, IDictionary<string, VariableAggregate> variables)
    {
        if (node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value is null)
                {
                    continue;
                }

                CollectPlaceholderFields(kv.Value, $"{path}.{kv.Key}", scope, variables);
            }
            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is null)
                {
                    continue;
                }

                CollectPlaceholderFields(arr[i], $"{path}[{i}]", scope, variables);
            }
            return;
        }

        if (node is not JsonValue value || !TryGetPlaceholderKey(value, out var placeholder))
        {
            return;
        }

        var key = NormalizePlaceholderKey(placeholder);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!variables.TryGetValue(key, out var aggregate))
        {
            aggregate = new VariableAggregate
            {
                Key = key,
                Scope = scope
            };
            variables[key] = aggregate;
        }

        aggregate.Scope = scope;
        aggregate.Paths.Add(path);
    }

    private static string NormalizePlaceholderKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var trimmed = key.Trim();
        if (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal) && trimmed.Length > 4)
        {
            trimmed = trimmed.Substring(2, trimmed.Length - 4);
        }

        return trimmed.Trim();
    }

    private static bool TryGetPlaceholderKey(JsonValue value, out string key)
    {
        key = string.Empty;
        if (!value.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str))
        {
            return false;
        }

        str = str.Trim();
        if (!str.StartsWith("{{", StringComparison.Ordinal) || !str.EndsWith("}}", StringComparison.Ordinal))
        {
            return false;
        }

        key = str.Substring(2, str.Length - 4).Trim();
        return !string.IsNullOrWhiteSpace(key);
    }

    private void FieldSearchBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = (FieldSearchBar.Text ?? string.Empty).Trim();
        IEnumerable<TemplateVariableItem> src = _allVariables;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            src = src.Where(v =>
            v.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || v.PathsDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || v.DefaultDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || v.TypeDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (v.Value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _filteredVariables.Clear();
        foreach (var item in src)
        {
            _filteredVariables.Add(item);
        }
    }

    private void ResetDefaults_Clicked(object sender, EventArgs e)
    {
        foreach (var item in _allVariables)
        {
            if (_variableDefinitions.TryGetValue(item.Key, out var def))
            {
                item.Value = def.DefaultValue ?? string.Empty;
                continue;
            }

            item.Value = item.DefaultValue ?? string.Empty;
        }
    }

    private async void PickFileForVariable_Clicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: TemplateVariableItem item })
        {
            return;
        }

        var file = await FileSystemService.PickFileAsync();
        if (!string.IsNullOrWhiteSpace(file))
        {
            item.Value = file;
        }
    }

    private async void PickAssetForVariable_Clicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: TemplateVariableItem item })
        {
            return;
        }

        var selectedAsset = await PickAssetWithPickerPageAsync();
        if (string.IsNullOrWhiteSpace(selectedAsset?.AssetId))
        {
            return;
        }

        item.Value = "$" + selectedAsset.AssetId;
    }

    private void ClearVariableValue_Clicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: TemplateVariableItem item })
        {
            return;
        }

        item.Value = string.Empty;
    }

    private async void Cancel_Clicked(object sender, EventArgs e)
    {
        if (AssetPickerOverlay.IsVisible)
        {
            CloseInlineAssetPicker(null);
            return;
        }

        if (_isTemplateInputMode)
        {
            _templateInputCompletion?.TrySetResult(null);
        }

        await RequestCloseAsync();
    }

    private async void CreateProject_Clicked(object sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_template is null)
        {
            await DisplayAlertAsync(Localized._Error, "No template.", Localized._OK);
            return;
        }

        try
        {
            SetBusy(true);
            var values = BuildInputValues(_allVariables);
            var defaults = _template.Variables ?? new Dictionary<string, string?>();
            var missingKeys = CollectMissingKeys(_allVariables, values, defaults, _variableDefinitions);
            if (missingKeys.Count > 0)
            {
                await DisplayAlertAsync(Localized._Error, Localized.TemplateCreatePage_MissingKeys(missingKeys.ToArray()), Localized._OK);
                return;
            }

            if (_isTemplateInputMode)
            {
                _templateInputCompletion?.TrySetResult(values);
                await RequestCloseAsync();
                return;
            }
            var projectName = "";
            if (!ImportControlGrid.IsVisible)
            {
                var page = FindHostPage();
                if (page is not null)
                {
                    projectName = await page.DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, _template.TemplateName, 1024, null, _template.TemplateName);
                    if (projectName is null) return;

                }
            }
            if (string.IsNullOrWhiteSpace(projectName))
            {
                await DisplayAlertAsync(Localized._Error, HomePage.GetInvalidFileNameWarn(), Localized._OK);
                return;
            }

            if (Path.GetInvalidPathChars().Any(projectName.Contains) || Path.GetInvalidFileNameChars().Any(projectName.Contains))
            {
                await DisplayAlertAsync(Localized._Error, HomePage.GetInvalidFileNameWarn(), Localized._OK);
                return;
            }

            var draftRoot = Path.Combine(MauiProgram.DataPath, "My Drafts");
            Directory.CreateDirectory(draftRoot);
            var projectDir = Path.Combine(draftRoot, projectName + ".pjfc");
            if (projectDir.Length > HomePage.GetMaxPathLength())
            {
                await DisplayAlertAsync(Localized._Error, HomePage.GetInvalidFileNameWarn(), Localized._OK);
                return;
            }
            if (Directory.Exists(projectDir))
            {
                await DisplayAlertAsync(Localized._Info, Localized.HomePage_CreateAProject_Exists, Localized._OK);
                return;
            }

            var projectClone = JsonNode.Parse(_projectNode.ToJsonString()) as JsonObject ?? new JsonObject();
            var draftClone = JsonNode.Parse(_draftNode.ToJsonString()) as JsonObject ?? new JsonObject();

            ReplacePlaceholders(projectClone, values, defaults, _variableDefinitions);
            ReplacePlaceholders(draftClone, values, defaults, _variableDefinitions);

            var referencedAssetIds = CollectReferencedAssetIds(projectClone, draftClone);
            var projectAssets = BuildProjectAssetList(referencedAssetIds, _template, projectDir, out var resolvedPackagedAssetPaths);
            ReplaceAssetReferencesWithResolvedPaths(projectClone, resolvedPackagedAssetPaths);
            ReplaceAssetReferencesWithResolvedPaths(draftClone, resolvedPackagedAssetPaths);

            var project = projectClone.Deserialize<ProjectJSONStructure>(DraftPage.DraftJSONOption)
            ?? throw new InvalidOperationException("Invalid Project structure.");
            var draft = draftClone.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption)
            ?? throw new InvalidOperationException("Invalid Draft structure.");

            project.ProjectName = projectName;
            project.LastChanged = DateTime.Now;
            project.NormallyExited = true;
            project.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
            project.LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
            project.LastOpenAppName = MauiProgram.AssemblyName;
            project.LastOpenAppIdentifier = MauiProgram.AppIdentifier;
            project.PluginUsed ??= [];
            project.ProjectUniqueId = Guid.CreateVersion7();

            draft.SavedAt = DateTime.Now;

            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "project.pjfc"), JsonSerializer.Serialize(project, DraftPage.DraftJSONOption));
            File.WriteAllText(Path.Combine(projectDir, "timeline.json"), JsonSerializer.Serialize(draft, DraftPage.DraftJSONOption));
            File.WriteAllText(Path.Combine(projectDir, "assets.json"), JsonSerializer.Serialize(projectAssets, DraftPage.DraftJSONOption));

            await RequestCloseAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_DraftLoadFail(ex), Localized._OK);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Page? FindHostPage()
    {
        Element? current = this;
        while (current is not null)
        {
            if (current is Page page)
            {
                return page;
            }

            current = current.Parent;
        }

        return Application.Current?.Windows.FirstOrDefault()?.Page;
    }

    private Task DisplayAlertAsync(string title, string message, string cancel)
    {
        var page = FindHostPage();
        return page is null ? Task.CompletedTask : page.DisplayAlertAsync(title, message, cancel);
    }

    private async Task RequestCloseAsync()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        if (CloseRequested is not null)
        {
            return;
        }

        var page = FindHostPage();
        if (page?.Navigation is not null && page.Navigation.NavigationStack.Count > 1)
        {
            await page.Navigation.PopAsync();
        }
    }

    private static Dictionary<string, string?> BuildInputValues(IEnumerable<TemplateVariableItem> items)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var value = item.Value;
            if (value is not null)
            {
                dict[item.Key] = value;
            }
        }
        return dict;
    }

    private static HashSet<string> CollectReferencedAssetIds(JsonNode? projectNode, JsonNode? draftNode)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectReferencedAssetIds(projectNode, refs);
        CollectReferencedAssetIds(draftNode, refs);
        return refs;
    }

    private static void CollectReferencedAssetIds(JsonNode? node, ISet<string> refs)
    {
        if (node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                CollectReferencedAssetIds(kv.Value, refs);
            }
            return;
        }

        if (node is JsonArray arr)
        {
            foreach (var child in arr)
            {
                CollectReferencedAssetIds(child, refs);
            }
            return;
        }

        if (node is JsonValue value
            && value.TryGetValue<string>(out var str)
            && TryParseAssetReference(str, out var assetId))
        {
            refs.Add(assetId);
        }
    }

    private static bool TryParseAssetReference(string? value, out string assetId)
    {
        assetId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('$') || trimmed.Length <= 1)
        {
            return false;
        }

        assetId = trimmed[1..].Trim();
        return !string.IsNullOrWhiteSpace(assetId);
    }

    private static List<AssetItem> BuildProjectAssetList(
        IEnumerable<string> assetIds,
        ITemplateStructure template,
        string projectDir,
        out Dictionary<string, string> resolvedPackagedAssetPaths)
    {
        resolvedPackagedAssetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<AssetItem>();
        var addedAssetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var templateAssetMap = template.AssetHashTable;
        var projectAssetsDir = Path.Combine(projectDir, "assets");
        Directory.CreateDirectory(projectAssetsDir);

        foreach (var assetId in assetIds)
        {
            if (string.IsNullOrWhiteSpace(assetId) || !addedAssetIds.Add(assetId))
            {
                continue;
            }

            if (!AssetDatabase.Assets.TryGetValue(assetId, out var asset))
            {
                if (templateAssetMap is null
                    || !templateAssetMap.TryGetValue(assetId, out var packagedAssetPath)
                    || string.IsNullOrWhiteSpace(packagedAssetPath)
                    || !File.Exists(packagedAssetPath))
                {
                    continue;
                }

                var extension = Path.GetExtension(packagedAssetPath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".bin";
                }

                var copiedPath = Path.Combine(projectAssetsDir, assetId + extension);
                File.Copy(packagedAssetPath, copiedPath, overwrite: true);

                var packagedAsset = new AssetItem
                {
                    AssetId = assetId,
                    Name = Path.GetFileNameWithoutExtension(packagedAssetPath),
                    Path = copiedPath,
                    AssetType = AssetItem.GetAssetType(copiedPath),
                    CreatedAt = DateTime.Now
                };
                if (string.IsNullOrWhiteSpace(packagedAsset.Name))
                {
                    packagedAsset.Name = $"Asset@{assetId[..Math.Min(assetId.Length, 8)]}";
                }

                assets.Add(packagedAsset);
                resolvedPackagedAssetPaths[assetId] = copiedPath;
                continue;
            }

            var cloned = JsonSerializer.Deserialize<AssetItem>(
                JsonSerializer.Serialize(asset, DraftPage.DraftJSONOption),
                DraftPage.DraftJSONOption);
            if (cloned is null)
            {
                continue;
            }

            assets.Add(cloned);
        }

        return assets;
    }

    private static void ReplaceAssetReferencesWithResolvedPaths(JsonNode? node, IReadOnlyDictionary<string, string> resolvedAssetPaths)
    {
        if (node is null || resolvedAssetPaths.Count == 0)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var current = obj[key];
                if (current is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && TryParseAssetReference(text, out var assetId)
                    && resolvedAssetPaths.TryGetValue(assetId, out var resolvedPath))
                {
                    obj[key] = resolvedPath;
                }
                else
                {
                    ReplaceAssetReferencesWithResolvedPaths(current, resolvedAssetPaths);
                }
            }

            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var current = arr[i];
                if (current is JsonValue value
                    && value.TryGetValue<string>(out var text)
                    && TryParseAssetReference(text, out var assetId)
                    && resolvedAssetPaths.TryGetValue(assetId, out var resolvedPath))
                {
                    arr[i] = resolvedPath;
                }
                else
                {
                    ReplaceAssetReferencesWithResolvedPaths(current, resolvedAssetPaths);
                }
            }
        }
    }

    private static List<string> CollectMissingKeys(
    IEnumerable<TemplateVariableItem> items,
    IReadOnlyDictionary<string, string?> values,
    IReadOnlyDictionary<string, string?> defaults,
    IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            if (TryResolveVariable(item.Key, values, defaults, definitions, out var resolved, out _) && !string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            list.Add(item.Key);
        }

        return list;
    }

    private static void ReplacePlaceholders(
    JsonNode? node,
    IReadOnlyDictionary<string, string?> values,
    IReadOnlyDictionary<string, string?> defaults,
    IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToArray();
            foreach (var key in keys)
            {
                var current = obj[key];
                if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
                {
                    if (!TryResolveVariable(placeholderKey, values, defaults, definitions, out var resolved, out var variableType))
                    {
                        throw new KeyNotFoundException($"Missing variable: {placeholderKey}");
                    }

                    obj[key] = ConvertResolvedValue(resolved, variableType);
                }
                else
                {
                    ReplacePlaceholders(current, values, defaults, definitions);
                }
            }
            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var current = arr[i];
                if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
                {
                    if (!TryResolveVariable(placeholderKey, values, defaults, definitions, out var resolved, out var variableType))
                    {
                        throw new KeyNotFoundException($"Missing variable: {placeholderKey}");
                    }

                    arr[i] = ConvertResolvedValue(resolved, variableType);
                }
                else
                {
                    ReplacePlaceholders(current, values, defaults, definitions);
                }
            }
        }
    }

    private static bool TryResolveVariable(
    string key,
    IReadOnlyDictionary<string, string?> values,
    IReadOnlyDictionary<string, string?> defaults,
    IReadOnlyDictionary<string, TemplateVariableDefinition> definitions,
    out string? resolved,
    out TemplateVariableType variableType)
    {
        variableType = TemplateVariableType.Auto;
        if (definitions.TryGetValue(key, out var def) && def is not null)
        {
            variableType = def.Type;
        }

        if (values.TryGetValue(key, out resolved) && resolved is not null)
        {
            return true;
        }

        if (values.TryGetValue($"{{{{{key}}}}}", out resolved) && resolved is not null)
        {
            return true;
        }

        if (def?.DefaultValue is not null)
        {
            resolved = def.DefaultValue;
            return true;
        }

        if (defaults.TryGetValue(key, out resolved))
        {
            return true;
        }

        if (defaults.TryGetValue($"{{{{{key}}}}}", out resolved))
        {
            return true;
        }

        return false;
    }

    private static JsonNode? ConvertResolvedValue(string? value, TemplateVariableType type)
    {
        if (value is null)
        {
            return null;
        }

        switch (type)
        {
            case TemplateVariableType.String:
                return JsonValue.Create(value);

            case TemplateVariableType.File:
                return JsonValue.Create(value);

            case TemplateVariableType.Boolean:
                if (!bool.TryParse(value, out var boolValue))
                {
                    throw new FormatException($"Value '{value}' is not a valid {type}.");
                }
                return JsonValue.Create(boolValue);

            case TemplateVariableType.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    throw new FormatException($"Value '{value}' is not a valid {type}.");
                }
                return JsonValue.Create(longValue);

            case TemplateVariableType.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    throw new FormatException($"Value '{value}' is not a valid {type}.");
                }
                return JsonValue.Create(doubleValue);

            case TemplateVariableType.Json:
                return JsonNode.Parse(value);
        }

        if (value.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
        {
            return JsonNode.Parse(value.Substring(5));
        }

        if (bool.TryParse(value, out var b))
        {
            return JsonValue.Create(b);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            return JsonValue.Create(i);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(value);
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        CreateButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;
        ImportTemplateButton.IsEnabled = !isBusy;
        FieldSearchBar.IsEnabled = !isBusy;
        VariablesCollectionView.IsEnabled = !isBusy;
        ProjectNameEntry.IsEnabled = !isBusy;
        AssetPickerCancelButton.IsEnabled = !isBusy;
        AssetPickerConfirmButton.IsEnabled = !isBusy && InlineAssetPicker.SelectedAsset is not null && !string.IsNullOrWhiteSpace(InlineAssetPicker.SelectedAsset.AssetId);
    }

    private async Task<AssetItem?> PickAssetWithPickerPageAsync()
    {
        if (_assetPickerCompletion is not null)
        {
            return await _assetPickerCompletion.Task;
        }

        _assetPickerCompletion = new TaskCompletionSource<AssetItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        InlineAssetPicker.SelectedAsset = null;
        AssetPickerConfirmButton.IsEnabled = false;
        AssetPickerOverlay.IsVisible = true;
        return await _assetPickerCompletion.Task;
    }

    private void InlineAssetPicker_SelectedAssetChanged(object? sender, AssetItem? selected)
    {
        AssetPickerConfirmButton.IsEnabled = !_isBusy && selected is not null && !string.IsNullOrWhiteSpace(selected.AssetId);
    }

    private void InlineAssetPicker_AssetDoubleTapped(object? sender, AssetItem selected)
    {
        if (string.IsNullOrWhiteSpace(selected.AssetId))
        {
            return;
        }

        CloseInlineAssetPicker(selected);
    }

    private void AssetPickerCancel_Clicked(object sender, EventArgs e)
    {
        CloseInlineAssetPicker(null);
    }

    private void AssetPickerConfirm_Clicked(object sender, EventArgs e)
    {
        if (InlineAssetPicker.SelectedAsset is null || string.IsNullOrWhiteSpace(InlineAssetPicker.SelectedAsset.AssetId))
        {
            return;
        }

        CloseInlineAssetPicker(InlineAssetPicker.SelectedAsset);
    }

    private void CloseInlineAssetPicker(AssetItem? result)
    {
        AssetPickerOverlay.IsVisible = false;
        AssetPickerConfirmButton.IsEnabled = false;
        var tcs = _assetPickerCompletion;
        _assetPickerCompletion = null;
        tcs?.TrySetResult(result);
        InlineAssetPicker.SelectedAsset = null;
    }

    private void RefreshStats()
    {
        if (_template is null)
        {
            StatsLabel.Text = Localized.TemplateCreatePage_Stats_UnloadedTemplate;
            return;
        }

        StatsLabel.Text = Localized.TemplateCreatePage_CountOfVariable(_allVariables.Count);
    }

    private sealed class VariableAggregate
    {
        public string Key { get; set; } = string.Empty;
        public string Scope { get; set; } = "Mixed";
        public TemplateVariableType Type { get; set; } = TemplateVariableType.Auto;
        public string? DefaultValue { get; set; }
        public List<string> Paths { get; } = [];
    }

    private sealed class TemplateVariableItem(
    string key,
    string location,
    string firstPath,
    string pathsDisplay,
    TemplateVariableType type,
    string? defaultValue,
    string value) : INotifyPropertyChanged
    {
        private string _value = value;

        public string Key => key;
        public string Location => location;
        public string PathsDisplay => string.IsNullOrWhiteSpace(pathsDisplay) ? firstPath : pathsDisplay;
        public TemplateVariableType Type => type;
        public string? DefaultValue => defaultValue;
        public string TypeDisplay => GetTypeDisplay(type);
        public bool IsFileType => type == TemplateVariableType.File;
        public Keyboard InputKeyboard => type switch
        {
            TemplateVariableType.Number => Keyboard.Numeric,
            TemplateVariableType.Integer => Keyboard.Numeric,
            _ => Keyboard.Default
        };
        public string DefaultDisplay => defaultValue is null ? "<null>" : defaultValue;

        public string Value
        {
            get => _value;
            set
            {
                if (string.Equals(_value, value, StringComparison.Ordinal))
                {
                    return;
                }

                _value = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string GetTypeDisplay(TemplateVariableType variableType)
        {
            var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            string[] map = lang switch
            {
                "zh" => ["字符串", "数字", "整数", "布尔", "文件", "JSON"],
                "ja" => ["文字列", "数値", "整数", "真偽値", "ファイル", "JSON"],
                "ko" => ["문자열", "숫자", "정수", "불리언", "파일", "JSON"],
                "fr" => ["Chaine", "Nombre", "Entier", "Booleen", "Fichier", "JSON"],
                "de" => ["Zeichenfolge", "Zahl", "Ganzzahl", "Boolesch", "Datei", "JSON"],
                "es" => ["Cadena", "Numero", "Entero", "Booleano", "Archivo", "JSON"],
                "it" => ["Stringa", "Numero", "Intero", "Booleano", "File", "JSON"],
                "pl" => ["Lancuch", "Liczba", "Calkowita", "Logiczna", "Plik", "JSON"],
                "pt" => ["Texto", "Numero", "Inteiro", "Booleano", "Arquivo", "JSON"],
                "ru" => ["Stroka", "Chislo", "Tseloe", "Bulovo", "Fail", "JSON"],
                "tr" => ["Metin", "Sayi", "Tam sayi", "Mantiksal", "Dosya", "JSON"],
                "ar" => ["نص", "رقم", "عدد صحيح", "منطقي", "ملف", "JSON"],
                _ => ["String", "Number", "Integer", "Boolean", "File", "JSON"]
            };
            return map[(int)variableType];
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
