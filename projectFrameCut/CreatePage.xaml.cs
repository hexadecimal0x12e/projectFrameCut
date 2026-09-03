using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.TemplateSystem;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Template;

namespace projectFrameCut;

public partial class CreatePage : ContentPage
{
    private const double NarrowLayoutThreshold = 600d;

    private readonly CreatePageViewModel _viewModel;
    private bool _isNarrowLayout;

    public Command<ProjectTemplateItem> CreateWithTemplateCommand { get; private set; }

    public CreatePage()
    {
        InitializeComponent();
        _viewModel = new CreatePageViewModel();
        BindingContext = _viewModel;
        _viewModel.CreateProjectRequested = CreateAndOpenProjectAsync;
        CreateWithTemplateCommand = new Command<ProjectTemplateItem>(async item =>
        {
            if (item is not null)
            {
                var id = item.TemplateId;
                await Navigation.PushAsync(new TemplateViewPage(id));
            }
        });
        SizeChanged += (_, _) => ApplyAdaptiveLayout();
        Loaded += (_, _) => ApplyAdaptiveLayout();
    }

    private void ApplyAdaptiveLayout()
    {
        var width = Width;
        if (width <= 0)
        {
            return;
        }

        var shouldBeNarrow = width < NarrowLayoutThreshold;
        if (_isNarrowLayout == shouldBeNarrow)
        {
            return;
        }

        _isNarrowLayout = shouldBeNarrow;

        if (_isNarrowLayout)
        {
            QuickStartGrid.ColumnDefinitions.Clear();
            QuickStartGrid.RowDefinitions.Clear();
            QuickStartGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            QuickStartGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            QuickStartGrid.RowSpacing = 12;
            QuickStartGrid.ColumnSpacing = 0;

            Grid.SetColumn(FormBorder, 0);
            Grid.SetRow(FormBorder, 0);
            Grid.SetColumn(PreviewBorder, 0);
            Grid.SetRow(PreviewBorder, 1);
            PreviewBorder.HorizontalOptions = LayoutOptions.Center;
            FormBorder.HorizontalOptions = LayoutOptions.Fill;
        }
        else
        {
            QuickStartGrid.RowDefinitions.Clear();
            QuickStartGrid.ColumnDefinitions.Clear();
            QuickStartGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2, GridUnitType.Star)));
            QuickStartGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            QuickStartGrid.ColumnSpacing = 12;

            Grid.SetColumn(FormBorder, 0);
            Grid.SetRow(FormBorder, 0);
            Grid.SetColumn(PreviewBorder, 1);
            Grid.SetRow(PreviewBorder, 0);
            PreviewBorder.HorizontalOptions = LayoutOptions.Fill;
            FormBorder.HorizontalOptions = LayoutOptions.Fill;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.ReloadTemplates();
    }

    private async Task CreateAndOpenProjectAsync(
        string projectName,
        int width,
        int height,
        uint frameRate,
        JSONBasedTemplateStructure? template)
    {
        var draftRoot = Path.Combine(MauiProgram.DataPath, "My Drafts");
        Directory.CreateDirectory(draftRoot);
        var projectDir = Path.Combine(draftRoot, projectName + ".pjfc");

        if (Path.GetInvalidPathChars().Any(projectName.Contains)
            || Path.GetInvalidFileNameChars().Any(projectName.Contains)
            || projectDir.Length > HomePage.GetMaxPathLength())
        {
            await DisplayAlertAsync(Localized._Error,
                HomePage.GetInvalidFileNameWarn(),
                Localized._OK);
            return;
        }

        if (Directory.Exists(projectDir))
        {
            await DisplayAlertAsync(Localized._Info,
                Localized.HomePage_CreateAProject_Exists,
                Localized._OK);
            return;
        }

        try
        {
            Directory.CreateDirectory(projectDir);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error,
                HomePage.GetInvalidFileNameWarn(),
                Localized._OK);
            return;
        }

        ProjectJSONStructure projectInfo;
        DraftStructureJSON draft;
        List<AssetItem> projectAssets = [];

        if (template is not null)
        {
            try
            {
                var projectNode = JsonNode.Parse(JsonSerializer.Serialize(template.Project, DraftPage.DraftJSONOption)) as JsonObject
                    ?? new JsonObject();
                var draftNode = JsonNode.Parse(JsonSerializer.Serialize(template.Draft, DraftPage.DraftJSONOption)) as JsonObject
                    ?? new JsonObject();

                var defaults = template.Variables ?? new Dictionary<string, string?>();
                var definitions = template.VariableDefinitions ?? new Dictionary<string, TemplateVariableDefinition>();

                ReplacePlaceholdersWithDefaults(projectNode, defaults, definitions);
                ReplacePlaceholdersWithDefaults(draftNode, defaults, definitions);

                projectInfo = projectNode.Deserialize<ProjectJSONStructure>(DraftPage.DraftJSONOption)
                    ?? new ProjectJSONStructure();
                draft = draftNode.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption)
                    ?? new DraftStructureJSON();
            }
            catch
            {
                projectInfo = new ProjectJSONStructure();
                draft = new DraftStructureJSON();
            }
        }
        else
        {
            projectInfo = new ProjectJSONStructure();
            draft = new DraftStructureJSON();
        }

        projectInfo.ProjectName = projectName;
        projectInfo.RelativeWidth = Math.Max(1, width);
        projectInfo.RelativeHeight = Math.Max(1, height);
        projectInfo.TargetFrameRate = Math.Max(1u, frameRate);
        projectInfo.NormallyExited = true;
        projectInfo.LastChanged = DateTime.Now;
        projectInfo.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
        projectInfo.LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        projectInfo.LastOpenAppName = MauiProgram.AssemblyName; 
        projectInfo.LastOpenAppIdentifier = MauiProgram.AppIdentifier;
        projectInfo.PluginUsed = [];
        projectInfo.ProjectUniqueId = Guid.CreateVersion7();

        draft.SavedAt = DateTime.Now;

        File.WriteAllText(
            Path.Combine(projectDir, "project.pjfc"),
            JsonSerializer.Serialize(projectInfo, DraftPage.DraftJSONOption));
        File.WriteAllText(
            Path.Combine(projectDir, "timeline.json"),
            JsonSerializer.Serialize(draft, DraftPage.DraftJSONOption));
        File.WriteAllText(
            Path.Combine(projectDir, "assets.json"),
            JsonSerializer.Serialize(projectAssets, DraftPage.DraftJSONOption));
        DraftImportAndExportHelper.EnsureProjectDirectoryShellIntegration(projectDir);

        await Navigation.PushAsync(new HomePage(Path.Combine(projectDir, "project.pjfc"), true));
    }



    private static void ReplacePlaceholdersWithDefaults(
        JsonNode? node,
        IReadOnlyDictionary<string, string?> defaults,
        IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var current = obj[key];
                if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
                {
                    if (TryResolveWithDefault(placeholderKey, defaults, definitions, out var resolved))
                    {
                        obj[key] = ConvertResolvedValue(resolved, TemplateVariableType.Auto);
                    }
                }
                else
                {
                    ReplacePlaceholdersWithDefaults(current, defaults, definitions);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var current = arr[i];
                if (current is JsonValue val && TryGetPlaceholderKey(val, out var placeholderKey))
                {
                    if (TryResolveWithDefault(placeholderKey, defaults, definitions, out var resolved))
                    {
                        arr[i] = ConvertResolvedValue(resolved, TemplateVariableType.Auto);
                    }
                }
                else
                {
                    ReplacePlaceholdersWithDefaults(current, defaults, definitions);
                }
            }
        }
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

    private static bool TryResolveWithDefault(
        string key,
        IReadOnlyDictionary<string, string?> defaults,
        IReadOnlyDictionary<string, TemplateVariableDefinition> definitions,
        out string? resolved)
    {
        if (definitions.TryGetValue(key, out var def) && def?.DefaultValue is not null)
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

        resolved = null;
        return false;
    }

    private static JsonNode? ConvertResolvedValue(string? value, TemplateVariableType type)
    {
        if (value is null)
        {
            return null;
        }

        if (type == TemplateVariableType.Boolean && bool.TryParse(value, out var b))
        {
            return JsonValue.Create(b);
        }

        if (type == TemplateVariableType.Integer && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (type == TemplateVariableType.Number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        if (type == TemplateVariableType.Json)
        {
            return JsonNode.Parse(value);
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            return JsonValue.Create(i);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l2))
        {
            return JsonValue.Create(l2);
        }

        if (bool.TryParse(value, out var b2))
        {
            return JsonValue.Create(b2);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2))
        {
            return JsonValue.Create(d2);
        }

        return JsonValue.Create(value);
    }

    public sealed class ProjectTemplateItem
    {
        public JSONBasedTemplateStructure Structure { get; }

        public string TemplateId { get; }
        public string Name { get; }
        public string Description { get; }
        public IReadOnlyList<string> Tags { get; }
        public bool HasTags => Tags.Count > 0;
        public string ResolutionText { get; }
        public string FrameRateText { get; }

        public ProjectTemplateItem(JSONBasedTemplateStructure structure)
        {
            Structure = structure;
            TemplateId = structure.TemplateID.ToString();
            Name = string.IsNullOrWhiteSpace(structure.TemplateName) ? "Unnamed Template" : structure.TemplateName;
            Description = TryGetVariable(structure, "description")
                ?? TryGetVariable(structure, "desc")
                ?? Name;
            Tags = BuildTags(structure);
            ResolutionText = $"{Math.Max(1, structure.Project.RelativeWidth)} × {Math.Max(1, structure.Project.RelativeHeight)}";
            FrameRateText = $"{Math.Max(1u, structure.Project.TargetFrameRate)} fps";
        }

        private static IReadOnlyList<string> BuildTags(JSONBasedTemplateStructure structure)
        {
            var tagsRaw = TryGetVariable(structure, "tags");
            if (string.IsNullOrWhiteSpace(tagsRaw))
            {
                return Array.Empty<string>();
            }

            return tagsRaw
                .Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string? TryGetVariable(JSONBasedTemplateStructure structure, string key)
        {
            if (structure.Variables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return null;
        }
    }
}
