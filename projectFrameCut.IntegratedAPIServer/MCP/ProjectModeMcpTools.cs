using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;

namespace projectFrameCut.IntegratedAPIServer.MCP;

internal sealed class ProjectMcpModeController : IIntegratedApiBackend, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, CancellationToken, ValueTask<IIntegratedApiBackend>> _backendFactory;
    private readonly Func<CancellationToken, ValueTask>? _exitProject;
    private IIntegratedApiBackend? _backend;

    public ProjectMcpModeController(
        string userDataRoot,
        Func<string, CancellationToken, ValueTask<IIntegratedApiBackend>> backendFactory,
        Func<CancellationToken, ValueTask>? exitProject = null)
    {
        UserDataRoot = Path.GetFullPath(userDataRoot);
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _exitProject = exitProject;
    }

    public string UserDataRoot { get; }

    public string? ProjectRoot { get; private set; }

    public bool HasProject => ProjectRoot is not null;

    public event Action? ModeChanged;

    public async ValueTask EnterProjectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        string normalizedRoot = ValidateProjectRoot(projectRoot);
        IIntegratedApiBackend? previous = null;
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(ProjectRoot, normalizedRoot, PathComparison))
                return;

            IIntegratedApiBackend replacement = await _backendFactory(normalizedRoot, cancellationToken).ConfigureAwait(false);
            previous = _backend;
            _backend = replacement;
            ProjectRoot = normalizedRoot;
            changed = true;
        }
        finally
        {
            _gate.Release();
        }

        await DisposeBackendAsync(previous).ConfigureAwait(false);
        if (changed) ModeChanged?.Invoke();
    }

    public async ValueTask ExitProjectAsync(bool save, CancellationToken cancellationToken = default)
    {
        IIntegratedApiBackend? previous;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is null) return;
            if (save)
            {
                JsonElement arguments = JsonSerializer.SerializeToElement(new { changeReason = "MCP exit project" });
                await _backend.ExecuteAsync(
                    IntegratedApiOperation.SaveProject,
                    arguments,
                    cancellationToken).ConfigureAwait(false);
            }

            previous = _backend;
            _backend = null;
            ProjectRoot = null;
        }
        finally
        {
            _gate.Release();
        }

        await DisposeBackendAsync(previous).ConfigureAwait(false);
        if (_exitProject is not null)
            await _exitProject(cancellationToken).ConfigureAwait(false);
        ModeChanged?.Invoke();
    }

    public async ValueTask<JsonElement> ExecuteAsync(
        IntegratedApiOperation operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_backend is null)
                throw new InvalidOperationException("No project is active. Use enter_project first.");
            return await _backend.ExecuteAsync(operation, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> RequestAuthorizationAsync(
        IntegratedApiAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _backend is null ||
                await _backend.RequestAuthorizationAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public JsonElement ListProjects()
    {
        string draftsRoot = Path.Combine(UserDataRoot, "My Drafts");
        if (!Directory.Exists(draftsRoot)) return ToElement(new { projects = Array.Empty<object>(), count = 0 });

        var projects = Directory.EnumerateDirectories(draftsRoot)
            .Select(static path => TryReadProject(path))
            .Where(static project => project is not null)
            .OrderByDescending(static project => project!.LastChanged)
            .ToArray();
        return ToElement(new { projects, count = projects.Length });
    }

    public JsonElement ListTemplates()
    {
        string templatesRoot = Path.Combine(UserDataRoot, "My Templates");
        if (!Directory.Exists(templatesRoot)) return ToElement(new { templates = Array.Empty<object>(), count = 0 });

        var templates = Directory.EnumerateFiles(templatesRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Select(static path => TryReadTemplateListing(path))
            .Where(static template => template is not null)
            .OrderBy(static template => template!.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ToElement(new { templates, count = templates.Length });
    }

    public JsonElement ReadAssetLibrary()
    {
        string databasePath = Path.Combine(UserDataRoot, "My Assets", ".database", "database.json");
        if (!File.Exists(databasePath)) return ToElement(new { assets = Array.Empty<object>(), count = 0 });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(databasePath));
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            JsonElement[] assets = root.EnumerateObject().Select(static property => property.Value.Clone()).ToArray();
            return ToElement(new { assets, count = assets.Length });
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] assets = root.EnumerateArray().Select(static value => value.Clone()).ToArray();
            return ToElement(new { assets, count = assets.Length });
        }

        throw new InvalidDataException($"Asset database '{databasePath}' must contain an object or array.");
    }

    public async ValueTask<string> CreateEmptyProjectAsync(
        string projectName,
        int width,
        int height,
        uint frameRate,
        bool enterProject,
        CancellationToken cancellationToken)
    {
        string projectRoot = GetNewProjectRoot(projectName);
        var project = CreateProjectInfo(projectName, width, height, frameRate);
        await WriteProjectAsync(projectRoot, project, new DraftStructureJSON(), cancellationToken).ConfigureAwait(false);
        if (enterProject) await EnterProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return projectRoot;
    }

    public async ValueTask<string> CreateProjectFromTemplateAsync(
        Guid templateId,
        string projectName,
        IReadOnlyDictionary<string, string?> variables,
        int? width,
        int? height,
        uint? frameRate,
        bool enterProject,
        CancellationToken cancellationToken)
    {
        JSONBasedTemplateStructure template = await LoadTemplateAsync(templateId, cancellationToken).ConfigureAwait(false);
        JsonNode projectNode = JsonSerializer.SerializeToNode(template.Project, JsonOptions) ?? new JsonObject();
        JsonNode draftNode = JsonSerializer.SerializeToNode(template.Draft, JsonOptions) ?? new JsonObject();
        var resolvedVariables = new Dictionary<string, string?>(template.Variables, StringComparer.Ordinal);
        foreach ((string key, TemplateVariableDefinition definition) in template.VariableDefinitions)
            if (!resolvedVariables.ContainsKey(key)) resolvedVariables[key] = definition.DefaultValue;
        foreach ((string key, string? value) in variables) resolvedVariables[key] = value;
        ReplacePlaceholders(projectNode, resolvedVariables, template.VariableDefinitions);
        ReplacePlaceholders(draftNode, resolvedVariables, template.VariableDefinitions);
        if (projectNode is JsonObject projectObject) projectObject.Remove(nameof(ProjectJSONStructure.ProjectUniqueId));

        ProjectJSONStructure project = projectNode.Deserialize<ProjectJSONStructure>(JsonOptions) ?? new ProjectJSONStructure();
        DraftStructureJSON draft = draftNode.Deserialize<DraftStructureJSON>(JsonOptions) ?? new DraftStructureJSON();
        project.ProjectName = projectName;
        project.RelativeWidth = Math.Max(1, width ?? project.RelativeWidth);
        project.RelativeHeight = Math.Max(1, height ?? project.RelativeHeight);
        project.TargetFrameRate = Math.Max(1, frameRate ?? project.TargetFrameRate);
        project.NormallyExited = true;
        project.LastChanged = DateTime.Now;
        project.ProjectUniqueId = Guid.CreateVersion7();
        project.PluginUsed = [];
        draft.SavedAt = DateTime.Now;

        string projectRoot = GetNewProjectRoot(projectName);
        await WriteProjectAsync(projectRoot, project, draft, cancellationToken).ConfigureAwait(false);
        await MaterializeTemplateAssetsAsync(templateId, projectRoot, cancellationToken).ConfigureAwait(false);
        if (enterProject) await EnterProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        return projectRoot;
    }

    public async ValueTask DisposeAsync()
    {
        IIntegratedApiBackend? backend;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            backend = _backend;
            _backend = null;
            ProjectRoot = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
        await DisposeBackendAsync(backend).ConfigureAwait(false);
    }

    private async ValueTask<JSONBasedTemplateStructure> LoadTemplateAsync(Guid templateId, CancellationToken cancellationToken)
    {
        string templatesRoot = Path.Combine(UserDataRoot, "My Templates");
        string packagePath = Path.Combine(templatesRoot, $"{templateId:N}.pjfcTemplate");
        string listingPath = Path.Combine(templatesRoot, $"{templateId:N}.json");
        if (!File.Exists(packagePath))
        {
            if (!File.Exists(listingPath)) throw new FileNotFoundException($"Template '{templateId}' was not found.", packagePath);
            JSONBasedTemplateStructure? legacyTemplate = JsonSerializer.Deserialize<JSONBasedTemplateStructure>(
                await File.ReadAllTextAsync(listingPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (legacyTemplate is null || legacyTemplate.TemplateID != templateId)
                throw new InvalidDataException($"Template '{templateId}' does not contain project template data.");
            return legacyTemplate;
        }

        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Any(static item => string.Equals(item.FullName, "script.ps1", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Script templates cannot be used to create a project through MCP.");
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(static item =>
            string.Equals(item.FullName, "template.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = archive.Entries.FirstOrDefault(static item =>
                item.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.FullName, "metadata.json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.FullName, "assets.json", StringComparison.OrdinalIgnoreCase));
        }
        if (entry is null || entry.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The template package does not contain a valid template.json.");

        await using Stream stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<JSONBasedTemplateStructure>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The template package contains invalid project template data.");
    }

    private string GetNewProjectRoot(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        projectName = projectName.Trim();
        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The project name contains invalid file-name characters.", nameof(projectName));

        string draftsRoot = Path.Combine(UserDataRoot, "My Drafts");
        string projectRoot = Path.Combine(draftsRoot, projectName + ".pjfc");
        if (Directory.Exists(projectRoot)) throw new IOException($"Project '{projectName}' already exists.");
        return projectRoot;
    }

    private async Task MaterializeTemplateAssetsAsync(Guid templateId, string projectRoot, CancellationToken cancellationToken)
    {
        string packagePath = Path.Combine(UserDataRoot, "My Templates", $"{templateId:N}.pjfcTemplate");
        if (!File.Exists(packagePath)) return;

        using var archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry? manifestEntry = archive.Entries.FirstOrDefault(static entry =>
            string.Equals(entry.FullName, "assets.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null || manifestEntry.Length > 16 * 1024 * 1024) return;

        List<AssetItem>? assets;
        await using (Stream manifestStream = manifestEntry.Open())
        {
            assets = await JsonSerializer.DeserializeAsync<List<AssetItem>>(
                manifestStream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        if (assets is not { Count: > 0 }) return;

        string assetsRoot = Path.Combine(projectRoot, "assets");
        Directory.CreateDirectory(assetsRoot);
        var materialized = new List<AssetItem>();
        foreach (AssetItem asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? assetId = asset.AssetId?.Trim();
            if (string.IsNullOrWhiteSpace(assetId)) continue;

            ZipArchiveEntry? sourceEntry = ResolveTemplateAssetEntry(archive, assetId, asset.Path);
            if (sourceEntry is null || sourceEntry.Length > 2L * 1024 * 1024 * 1024) continue;

            string safeId = string.Concat(assetId.Select(static character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            string extension = Path.GetExtension(sourceEntry.Name);
            string fileName = safeId + (string.IsNullOrWhiteSpace(extension) ? ".bin" : extension);
            string targetPath = Path.Combine(assetsRoot, fileName);
            await using (Stream source = sourceEntry.Open())
            await using (FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

            asset.Path = Path.Combine("assets", fileName).Replace('\\', '/');
            materialized.Add(asset);
        }

        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "assets.json"),
            JsonSerializer.Serialize(materialized, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static ZipArchiveEntry? ResolveTemplateAssetEntry(ZipArchive archive, string assetId, string? manifestPath)
    {
        string normalizedManifestPath = (manifestPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item =>
            string.Equals(item.FullName.Replace('\\', '/'), normalizedManifestPath, StringComparison.OrdinalIgnoreCase));
        return entry ?? archive.Entries.FirstOrDefault(item =>
            item.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileNameWithoutExtension(item.Name), assetId, StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectJSONStructure CreateProjectInfo(string projectName, int width, int height, uint frameRate)
        => new()
        {
            ProjectName = projectName.Trim(),
            RelativeWidth = Math.Max(1, width),
            RelativeHeight = Math.Max(1, height),
            TargetFrameRate = Math.Max(1, frameRate),
            NormallyExited = true,
            LastChanged = DateTime.Now,
            LastOpenAppName = "projectFrameCut",
            LastOpenAppIdentifier = "hexadecimal0x12e.projectFrameCut",
            PluginUsed = [],
            ProjectUniqueId = Guid.CreateVersion7(),
        };

    private static async Task WriteProjectAsync(
        string projectRoot,
        ProjectJSONStructure project,
        DraftStructureJSON draft,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(projectRoot);
        try
        {
            draft.SavedAt = DateTime.Now;
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "project.pjfc"), JsonSerializer.Serialize(project, JsonOptions), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "timeline.json"), JsonSerializer.Serialize(draft, JsonOptions), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "assets.json"), "[]", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(projectRoot) && !Directory.EnumerateFileSystemEntries(projectRoot).Any()) Directory.Delete(projectRoot);
            throw;
        }
    }

    private static void ReplacePlaceholders(
        JsonNode? node,
        IReadOnlyDictionary<string, string?> variables,
        IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(static item => item.Key).ToArray())
            {
                JsonNode? child = obj[key];
                if (child is JsonValue value && value.TryGetValue(out string? text)) obj[key] = ReplaceValue(text, variables, definitions);
                else ReplacePlaceholders(child, variables, definitions);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                JsonNode? child = array[i];
                if (child is JsonValue value && value.TryGetValue(out string? text)) array[i] = ReplaceValue(text, variables, definitions);
                else ReplacePlaceholders(child, variables, definitions);
            }
        }
    }

    private static JsonNode? ReplaceValue(
        string? text,
        IReadOnlyDictionary<string, string?> variables,
        IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
    {
        string source = text ?? string.Empty;
        if (source.StartsWith("{{", StringComparison.Ordinal) && source.EndsWith("}}", StringComparison.Ordinal))
        {
            string key = source[2..^2].Trim();
            if (variables.TryGetValue(key, out string? value))
            {
                TemplateVariableType type = definitions.TryGetValue(key, out TemplateVariableDefinition? definition)
                    ? definition.Type
                    : TemplateVariableType.Auto;
                return ConvertResolvedValue(value, type);
            }
        }
        return JsonValue.Create(ReplaceText(source, variables));
    }

    private static JsonNode? ConvertResolvedValue(string? value, TemplateVariableType type)
    {
        if (value is null) return null;
        if (type == TemplateVariableType.Json) return JsonNode.Parse(value);
        if (type == TemplateVariableType.Boolean && bool.TryParse(value, out bool boolean)) return JsonValue.Create(boolean);
        if (type == TemplateVariableType.Integer && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return JsonValue.Create(integer);
        if (type == TemplateVariableType.Number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return JsonValue.Create(number);
        if (type == TemplateVariableType.Auto)
        {
            if (bool.TryParse(value, out boolean)) return JsonValue.Create(boolean);
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return JsonValue.Create(integer);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return JsonValue.Create(number);
        }
        return JsonValue.Create(value);
    }

    private static string ReplaceText(string? text, IReadOnlyDictionary<string, string?> variables)
    {
        string result = text ?? string.Empty;
        foreach ((string key, string? value) in variables)
            result = result.Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.Ordinal);
        return result;
    }

    private static string ValidateProjectRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string normalizedRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(normalizedRoot)) throw new DirectoryNotFoundException($"Project root '{normalizedRoot}' does not exist.");
        if (!File.Exists(Path.Combine(normalizedRoot, "project.pjfc")) && !File.Exists(Path.Combine(normalizedRoot, "project.json")))
            throw new FileNotFoundException("The directory does not contain project.pjfc or project.json.", normalizedRoot);
        if (!File.Exists(Path.Combine(normalizedRoot, "timeline.json")))
            throw new FileNotFoundException("The directory does not contain timeline.json.", normalizedRoot);
        return normalizedRoot;
    }

    private static ProjectListing? TryReadProject(string path)
    {
        try
        {
            string projectFile = File.Exists(Path.Combine(path, "project.pjfc")) ? "project.pjfc" : "project.json";
            ProjectJSONStructure? project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(path, projectFile)), JsonOptions);
            return project is null ? null : new ProjectListing(project.ProjectName ?? Path.GetFileNameWithoutExtension(path), path, project.LastChanged, project.RelativeWidth, project.RelativeHeight, project.TargetFrameRate);
        }
        catch
        {
            return new ProjectListing(Path.GetFileNameWithoutExtension(path), path, null, 0, 0, 0, false);
        }
    }

    private static TemplateListing? TryReadTemplateListing(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            Guid id = GetProperty(root, "TemplateId", "TemplateID").GetGuid();
            string name = GetProperty(root, "TemplateName").GetString() ?? "Unnamed template";
            int scope = GetOptionalInt32(root, "Scope");
            return new TemplateListing(
                id,
                name,
                scope,
                GetOptionalInt32(root, "TemplateVersion"),
                GetOptionalBoolean(root, "HaveAsset"),
                GetOptionalBoolean(root, "IsScript"),
                GetOptionalInt32(root, "ClipCount"),
                GetOptionalInt32(root, "TrackCount"),
                root.TryGetProperty("Variables", out JsonElement variables) ? variables.Clone() : EmptyObject(),
                root.TryGetProperty("VariableDefinitions", out JsonElement definitions) ? definitions.Clone() : EmptyObject());
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement GetProperty(JsonElement root, params string[] names)
    {
        foreach (string name in names) if (root.TryGetProperty(name, out JsonElement value)) return value;
        throw new JsonException($"Missing property '{names[0]}'.");
    }

    private static int GetOptionalInt32(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    private static bool GetOptionalBoolean(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) &&
           (value.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
           value.GetBoolean();

    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

    private static async ValueTask DisposeBackendAsync(IIntegratedApiBackend? backend)
    {
        if (backend is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (backend is IDisposable disposable) disposable.Dispose();
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record ProjectListing(string Name, string Path, DateTime? LastChanged, int Width, int Height, uint FrameRate, bool IsValid = true);
    private sealed record TemplateListing(Guid Id, string Name, int Scope, int Version, bool HaveAsset, bool IsScript, int ClipCount, int TrackCount, JsonElement Variables, JsonElement VariableDefinitions);
}

internal sealed class ProjectModeMcpToolCollection : IDisposable
{
    private readonly ProjectMcpModeController _controller;
    private readonly EndpointAuthorizationManager _authorization;
    private readonly IntegratedApiRequestContextAccessor _requestContextAccessor;
    private readonly bool _requireAuthorization;
    private readonly bool _includeIntegratedClientTools;

    public ProjectModeMcpToolCollection(
        ProjectMcpModeController controller,
        EndpointAuthorizationManager authorization,
        IntegratedApiRequestContextAccessor requestContextAccessor,
        bool requireAuthorization,
        bool includeIntegratedClientTools)
    {
        _controller = controller;
        _authorization = authorization;
        _requestContextAccessor = requestContextAccessor;
        _requireAuthorization = requireAuthorization;
        _includeIntegratedClientTools = includeIntegratedClientTools;
        Tools = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);
        Refresh();
        _controller.ModeChanged += Refresh;
    }

    public McpServerPrimitiveCollection<McpServerTool> Tools { get; }

    public void Dispose() => _controller.ModeChanged -= Refresh;

    private void Refresh()
    {
        using (Tools.DeferChangedEvents())
        {
            Tools.Clear();
            IEnumerable<McpServerTool> tools = _controller.HasProject
                ? [
                    .. IntegratedApiToolCatalog.Create(
                        _controller,
                        _authorization,
                        _requestContextAccessor,
                        _requireAuthorization,
                        _includeIntegratedClientTools),
                    .. ProjectModeEditingTools.Create(_controller, _requireAuthorization),
                    new ExitProjectTool(_controller),
                ]
                : ProjectLibraryTools.Create(_controller);
            foreach (McpServerTool tool in tools) Tools.Add(tool);
        }
    }
}

internal static class ProjectModeEditingTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<McpServerTool> Create(ProjectMcpModeController controller, bool requireAuthorization)
        =>
        [
            Tool("list_project_assets", "Search assets in the active project and/or global asset library.", ObjectSchema("""
                "scope":{"type":"string","enum":["all","project","global"],"default":"all"},
                "filter":{"type":"string"},
                "assetType":{"type":"string","enum":["Video","Audio","Image","Font","Other"]}
                """), async (arguments, cancellationToken) =>
            {
                AssetItem[] assets = await ReadAssetsAsync(controller, arguments, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { assets, count = assets.Length }, JsonOptions);
            }, requireAuthorization),
            Tool("add_clip_from_asset", "Add a video, image, or audio clip from a project/global asset ID.", PlacementSchema("""
                "assetId":{"type":"string"},"duration":{"type":"integer","minimum":1}
                """, "assetId"), async (arguments, cancellationToken) =>
            {
                string assetId = RequiredString(arguments, "assetId");
                AssetItem asset = (await ReadAssetsAsync(controller, EmptyObject(), cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(item => string.Equals(item.AssetId, assetId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
                JsonObject edit = WithKind(arguments, "addClipFromAsset");
                edit["asset"] = JsonSerializer.SerializeToNode(asset, JsonOptions);
                return await ExecuteEditAsync(controller, edit, cancellationToken).ConfigureAwait(false);
            }, requireAuthorization),
            Tool("add_text_clip", "Add a text clip with a common single TextEntry.", PlacementSchema("""
                "text":{"type":"string"},"name":{"type":"string"},"duration":{"type":"integer","minimum":1},
                "fontName":{"type":"string","default":"Arial"},"fontStyle":{"type":"string","default":"Regular"},
                "fontSize":{"type":"number","exclusiveMinimum":0},"x":{"type":"number","default":0},"y":{"type":"number","default":0},
                "fillR":{"type":"integer","minimum":0,"maximum":65535},"fillG":{"type":"integer","minimum":0,"maximum":65535},
                "fillB":{"type":"integer","minimum":0,"maximum":65535},"fillA":{"type":"number","minimum":0,"maximum":1},
                "targetWidth":{"type":"integer","minimum":1},"targetHeight":{"type":"integer","minimum":1}
                """, "text"), Edit(controller, "addTextClip"), requireAuthorization),
            Tool("set_text_entries", "Replace all TextEntry records on a text clip.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"entries":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
                    "text":{"type":"string"},"fontName":{"type":"string"},"fallbackFonts":{"type":"array","items":{"type":"string"}},
                    "fontStyle":{"type":"string"},"fontSize":{"type":"number","exclusiveMinimum":0},"x":{"type":"number"},"y":{"type":"number"},
                    "fillR":{"type":"integer","minimum":0,"maximum":65535},"fillG":{"type":"integer","minimum":0,"maximum":65535},
                    "fillB":{"type":"integer","minimum":0,"maximum":65535},"fillA":{"type":"number","minimum":0,"maximum":1},
                    "strokeR":{"type":"integer","minimum":0,"maximum":65535},"strokeG":{"type":"integer","minimum":0,"maximum":65535},
                    "strokeB":{"type":"integer","minimum":0,"maximum":65535},"strokeA":{"type":"number","minimum":0,"maximum":1},
                    "strokeThickness":{"type":"number","minimum":0},"characterSpacing":{"type":"number"},"wordSpacing":{"type":"number"},
                    "lineSpacing":{"type":"number"},"rotation":{"type":"number"},"layerIndex":{"type":"integer"},
                    "alignment":{"type":"string","enum":["Left","Center","Right"]},
                    "decoration":{"type":"string","enum":["None","Underline","Strikethrough"]},
                    "flowDirection":{"type":"string","enum":["LeftToRight","RightToLeft"]},
                    "variationAxes":{"type":"object","additionalProperties":{"type":"number"}},"extraData":{"type":"object"}
                },"required":["text"]}}
                """, "clipId", "entries"), Edit(controller, "setTextEntries"), requireAuthorization),
            Tool("add_solid_color_clip", "Add an infinite solid-color clip. Color uses #RRGGBB or #RRGGBBAA.", PlacementSchema("""
                "color":{"type":"string","pattern":"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$","default":"#FFFFFFFF"},
                "name":{"type":"string"},"duration":{"type":"integer","minimum":1}
                """), Edit(controller, "addSolidColorClip"), requireAuthorization),
            Tool("set_solid_color", "Change a solid-color clip. Color uses #RRGGBB or #RRGGBBAA.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"color":{"type":"string","pattern":"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"}
                """, "clipId", "color"), Edit(controller, "setSolidColor"), requireAuthorization),
            Tool("add_vector_canvas_clip", "Add an empty VectorCanvasClip.", PlacementSchema("""
                "name":{"type":"string"},"duration":{"type":"integer","minimum":1},
                "targetWidth":{"type":"integer","minimum":1},"targetHeight":{"type":"integer","minimum":1}
                """), Edit(controller, "addVectorCanvasClip"), requireAuthorization),
            Tool("list_vector_component_types", "List built-in vector component type names.", EmptySchema,
                Query(controller, "vectorComponentTypes"), requireAuthorization),
            Tool("list_vector_components", "List serialized vector components on a VectorCanvasClip.", ClipIdSchema,
                Query(controller, "vectorComponents"), requireAuthorization),
            Tool("add_vector_component", "Add a validated vector component object to a VectorCanvasClip.", ComponentEditSchema(false),
                Edit(controller, "addVectorComponent"), requireAuthorization),
            Tool("update_vector_component", "Replace one vector component while retaining its component ID.", ComponentEditSchema(true),
                Edit(controller, "updateVectorComponent"), requireAuthorization),
            Tool("remove_vector_component", "Remove one vector component.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"componentId":{"type":"string","format":"uuid"}
                """, "clipId", "componentId"), Edit(controller, "removeVectorComponent"), requireAuthorization),
            Tool("replace_vector_components", "Atomically replace all vector components with validated serialized component objects.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"components":{"type":"array","items":{"type":"object"}}
                """, "clipId", "components"), Edit(controller, "replaceVectorComponents"), requireAuthorization),
            Tool("set_vector_component_keyframes", "Replace keyframes for one animatable vector component field. Time is normalized to 0..1.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"componentId":{"type":"string","format":"uuid"},"fieldId":{"type":"string"},
                "keyframes":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"time":{"type":"number","minimum":0,"maximum":1},"value":{"type":"number"},"easing":{"type":"string","enum":["Linear","QuadIn","QuadOut","QuadInOut","CubicIn","CubicOut","CubicInOut","SineIn","SineOut","SineInOut","ElasticIn","ElasticOut","BounceOut"]}},"required":["time","value"]}}
                """, "clipId", "componentId", "fieldId", "keyframes"), Edit(controller, "setVectorComponentKeyframes"), requireAuthorization),

            Tool("list_clip_effect_providers", "List complete EffectProvider nodes, fields, ports, values, metadata, and stored bindings on a clip.", ClipIdSchema,
                Query(controller, "clipEffectProviders"), requireAuthorization),
            Tool("add_effect_provider", "Add an EffectProvider with typed static fields and optional automatic picture-chain connection.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"typeName":{"type":"string"},"providerId":{"type":"string","format":"uuid"},
                "name":{"type":"string"},"enabled":{"type":"boolean","default":true},"fields":{"type":"object"},"metadata":{"type":"object"},
                "implementType":{"type":"string","enum":["None","NotSpecified","IPicture","HwAcceleration","Custom1","Custom2","Custom3","Custom4","Custom5"]},
                "autoConnect":{"type":"string","enum":["output","input","none"],"default":"output"}
                """, "clipId", "typeName"), Edit(controller, "addEffectProvider"), requireAuthorization),
            Tool("update_effect_provider", "Update an EffectProvider's name, enabled state, typed static fields, or metadata.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},
                "name":{"type":"string"},"enabled":{"type":"boolean"},"fields":{"type":"object"},"metadata":{"type":"object"},
                "implementType":{"type":"string","enum":["None","NotSpecified","IPicture","HwAcceleration","Custom1","Custom2","Custom3","Custom4","Custom5"]}
                """, "clipId", "providerId"), Edit(controller, "updateEffectProvider"), requireAuthorization),
            Tool("remove_effect_provider", "Remove an EffectProvider and clear references to it.", ProviderIdSchema,
                Edit(controller, "removeEffectProvider"), requireAuthorization),
            Tool("connect_effect_provider_input", "Connect a provider picture input to clip-input, none, or another picture provider UUID.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},"source":{"type":"string"}
                """, "clipId", "providerId", "source"), Edit(controller, "connectEffectProviderInput"), requireAuthorization),
            Tool("set_effect_provider_output", "Select the provider connected to final picture output; omit providerId to disconnect output.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":["string","null"],"format":"uuid"}
                """, "clipId"), Edit(controller, "setEffectProviderOutput"), requireAuthorization),
            Tool("bind_effect_provider_field", "Bind a provider field to a compatible value-provider UUID, builtin://frame, or builtin://progress.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},"fieldId":{"type":"string"},"source":{"type":"string"}
                """, "clipId", "providerId", "fieldId", "source"), Edit(controller, "bindEffectProviderField"), requireAuthorization),
            Tool("unbind_effect_provider_field", "Remove a dynamic field binding while preserving its static fallback.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},"fieldId":{"type":"string"}
                """, "clipId", "providerId", "fieldId"), Edit(controller, "unbindEffectProviderField"), requireAuthorization),
            Tool("validate_effect_provider_graph", "Validate picture connections, final output, cycles, value sources, and port compatibility.", ClipIdSchema,
                Query(controller, "validateEffectProviderGraph"), requireAuthorization),
            Tool("replace_effect_provider_graph", "Atomically replace the complete provider-native graph using EffectProviderJSONStructure objects.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providers":{"type":"array","items":{"type":"object"}}
                """, "clipId", "providers"), Edit(controller, "replaceEffectProviderGraph"), requireAuthorization),

            Tool("set_color_adjustment", "Add or update ColorAdjustment with typed grading controls.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},
                "brightness":{"type":"number","minimum":0,"maximum":2},"contrast":{"type":"number","minimum":0,"maximum":3},
                "saturation":{"type":"number","minimum":0,"maximum":3},"hue":{"type":"number","minimum":0,"maximum":360},
                "gamma":{"type":"number","minimum":0.5,"maximum":2},"vibrance":{"type":"number","minimum":-1,"maximum":1},
                "temperature":{"type":"number","minimum":-100,"maximum":100},"invert":{"type":"boolean"},
                "grayscale":{"type":"number","minimum":0,"maximum":1},"opacity":{"type":"number","minimum":0,"maximum":1}
                """, "clipId"), Edit(controller, "setColorAdjustment"), requireAuthorization),
            Tool("set_clip_speed", "Add or update ClassicSpeedVarianceProvider.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},"ratio":{"type":"number","minimum":0.05,"maximum":8}
                """, "clipId", "ratio"), Edit(controller, "setClipSpeed"), requireAuthorization),
            Tool("set_linear_effect_animation", "Create/update LinearAnimationValueProvider and bind it to a numeric EffectProvider field.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"targetProviderId":{"type":"string","format":"uuid"},"fieldId":{"type":"string"},
                "animationProviderId":{"type":"string","format":"uuid"},"name":{"type":"string"},"fromValue":{"type":"number"},"toValue":{"type":"number"}
                """, "clipId", "targetProviderId", "fieldId", "fromValue", "toValue"), Edit(controller, "setLinearEffectAnimation"), requireAuthorization),
            Tool("set_position_keyframes", "Add or update ProgressPlacer position/size keyframes with normalized indexes.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},
                "keyframes":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"index":{"type":"number","minimum":0,"maximum":1},"position":{"type":"object","additionalProperties":false,"properties":{"targetX":{"type":"integer"},"targetY":{"type":"integer"},"targetWidth":{"type":"integer"},"targetHeight":{"type":"integer"},"isDelta":{"type":"boolean"}},"required":["targetX","targetY","targetWidth","targetHeight"]}},"required":["index","position"]}}
                """, "clipId", "keyframes"), Edit(controller, "setPositionKeyframes"), requireAuthorization),
            Tool("set_crop_keyframes", "Add or update ProgressCrop keyframes with normalized indexes.", ObjectSchema("""
                "clipId":{"type":"string","format":"uuid"},"providerId":{"type":"string","format":"uuid"},
                "startX":{"type":"integer"},"startY":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1},"angle":{"type":"number"},
                "keyframes":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"index":{"type":"number","minimum":0,"maximum":1},"startX":{"type":"integer"},"startY":{"type":"integer"},"width":{"type":"integer","minimum":1},"height":{"type":"integer","minimum":1},"angle":{"type":"number"}},"required":["index","startX","startY","width","height"]}}
                """, "clipId", "keyframes"), Edit(controller, "setCropKeyframes"), requireAuthorization),
        ];

    private static DelegateTool Tool(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler,
        bool requireAuthorization)
        => new(name, description, schema, handler, requireAuthorization);

    private static Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Query(ProjectMcpModeController controller, string kind)
        => (arguments, cancellationToken) => controller.ExecuteAsync(IntegratedApiOperation.ProjectModeQuery, ToElement(WithKind(arguments, kind)), cancellationToken);

    private static Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Edit(ProjectMcpModeController controller, string kind)
        => (arguments, cancellationToken) => ExecuteEditAsync(controller, WithKind(arguments, kind), cancellationToken);

    private static ValueTask<JsonElement> ExecuteEditAsync(ProjectMcpModeController controller, JsonObject edit, CancellationToken cancellationToken)
        => controller.ExecuteAsync(IntegratedApiOperation.ProjectModeEdit, ToElement(edit), cancellationToken);

    private static JsonObject WithKind(JsonElement arguments, string kind)
    {
        JsonObject result = JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? new JsonObject();
        result["kind"] = kind;
        return result;
    }

    private static async ValueTask<AssetItem[]> ReadAssetsAsync(ProjectMcpModeController controller, JsonElement arguments, CancellationToken cancellationToken)
    {
        string scope = OptionalString(arguments, "scope") ?? "all";
        string? filter = OptionalString(arguments, "filter");
        string? assetType = OptionalString(arguments, "assetType");
        var assets = new List<(AssetItem Asset, int Priority)>();
        if (!string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
        {
            JsonElement projectResult = await controller.ExecuteAsync(
                IntegratedApiOperation.ProjectModeQuery,
                ToElement(new { kind = "projectAssets" }),
                cancellationToken).ConfigureAwait(false);
            assets.AddRange(ReadAssetArray(projectResult).Select(asset => (asset, 0)));
        }
        if (!string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            assets.AddRange(ReadAssetArray(controller.ReadAssetLibrary()).Select(asset => (asset, 1)));
        IEnumerable<AssetItem> result = assets
            .Where(item => !string.IsNullOrWhiteSpace(item.Asset.AssetId))
            .GroupBy(item => item.Asset.AssetId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Priority).First().Asset);
        if (!string.IsNullOrWhiteSpace(filter))
            result = result.Where(asset => (asset.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) || (asset.AssetId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        if (!string.IsNullOrWhiteSpace(assetType) && Enum.TryParse(assetType, true, out AssetType parsedType))
            result = result.Where(asset => asset.AssetType == parsedType);
        return result.OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<AssetItem> ReadAssetArray(JsonElement result)
    {
        if (!result.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array) return [];
        return assets.EnumerateArray()
            .Select(item => item.Deserialize<AssetItem>(JsonOptions))
            .OfType<AssetItem>();
    }

    private static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static JsonElement EmptyObject() => ToElement(new { });
    private static string? OptionalString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement arguments, string name)
        => OptionalString(arguments, name) ?? throw new ArgumentException($"Missing or invalid '{name}'.");

    private const string EmptySchema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
    private const string ClipIdSchema = "{\"type\":\"object\",\"properties\":{\"clipId\":{\"type\":\"string\",\"format\":\"uuid\"}},\"required\":[\"clipId\"],\"additionalProperties\":false}";
    private const string ProviderIdSchema = "{\"type\":\"object\",\"properties\":{\"clipId\":{\"type\":\"string\",\"format\":\"uuid\"},\"providerId\":{\"type\":\"string\",\"format\":\"uuid\"}},\"required\":[\"clipId\",\"providerId\"],\"additionalProperties\":false}";

    private static string PlacementSchema(string extraProperties, params string[] extraRequired)
        => ObjectSchema(
            $"\"layerIndex\":{{\"type\":\"integer\",\"minimum\":0}},\"startFrame\":{{\"type\":\"integer\",\"minimum\":0}},\"subLayerIndex\":{{\"type\":\"integer\",\"minimum\":0}},{extraProperties}",
            new[] { "layerIndex", "startFrame" }.Concat(extraRequired).ToArray());

    private static string ComponentEditSchema(bool requireComponentId)
        => ObjectSchema(
            "\"clipId\":{\"type\":\"string\",\"format\":\"uuid\"},\"componentId\":{\"type\":\"string\",\"format\":\"uuid\"},\"component\":{\"type\":\"object\",\"properties\":{\"fromPlugin\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"index\":{\"type\":\"integer\"},\"parameters\":{\"type\":\"object\"},\"animationFrames\":{\"type\":\"array\"}},\"required\":[\"typeName\"]}",
            requireComponentId ? new[] { "clipId", "componentId", "component" } : new[] { "clipId", "component" });

    private static string ObjectSchema(string properties, params string[] required)
        => $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{string.Join(',', required.Select(name => $"\"{name}\""))}],\"additionalProperties\":false}}";
}

internal static class ProjectLibraryTools
{
    public static IReadOnlyList<McpServerTool> Create(ProjectMcpModeController controller)
        =>
        [
            new DelegateTool("list_projects", "List projects in the user's project library.", EmptySchema, (_, _) => ValueTask.FromResult(controller.ListProjects())),
            new DelegateTool("list_templates", "List templates in the user's template library.", EmptySchema, (_, _) => ValueTask.FromResult(controller.ListTemplates())),
            new DelegateTool("read_asset_library", "Read items in the user's global asset library.", EmptySchema, (_, _) => ValueTask.FromResult(controller.ReadAssetLibrary())),
            new DelegateTool("enter_project", "Enter an existing project and switch this MCP server to project tools.", ObjectSchema("\"projectRoot\":{\"type\":\"string\"}", "projectRoot"), async (arguments, cancellationToken) =>
            {
                string projectRoot = RequiredString(arguments, "projectRoot");
                await controller.EnterProjectAsync(projectRoot, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new
                {
                    mode = "project",
                    projectRoot = controller.ProjectRoot,
                    toolsChanged = true,
                });
            }),
            new DelegateTool("create_empty_project", "Create an empty project in the user's project library.", ObjectSchema("""
                "projectName":{"type":"string"},
                "width":{"type":"integer","minimum":1,"default":1920},
                "height":{"type":"integer","minimum":1,"default":1080},
                "frameRate":{"type":"integer","minimum":1,"default":60},
                "enterProject":{"type":"boolean","default":true}
                """, "projectName"), async (arguments, cancellationToken) =>
            {
                string projectRoot = await controller.CreateEmptyProjectAsync(
                    RequiredString(arguments, "projectName"),
                    OptionalInt32(arguments, "width") ?? 1920,
                    OptionalInt32(arguments, "height") ?? 1080,
                    OptionalUInt32(arguments, "frameRate") ?? 60,
                    OptionalBoolean(arguments, "enterProject") ?? true,
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { created = true, projectRoot, mode = controller.HasProject ? "project" : "no-project", toolsChanged = controller.HasProject });
            }),
            new DelegateTool("create_project_from_template", "Create a project from a project template in the user's template library.", ObjectSchema("""
                "templateId":{"type":"string","format":"uuid"},
                "projectName":{"type":"string"},
                "variables":{"type":"object","additionalProperties":{"type":["string","null"]}},
                "width":{"type":"integer","minimum":1},
                "height":{"type":"integer","minimum":1},
                "frameRate":{"type":"integer","minimum":1},
                "enterProject":{"type":"boolean","default":true}
                """, "templateId", "projectName"), async (arguments, cancellationToken) =>
            {
                string projectRoot = await controller.CreateProjectFromTemplateAsync(
                    Guid.Parse(RequiredString(arguments, "templateId")),
                    RequiredString(arguments, "projectName"),
                    OptionalStringDictionary(arguments, "variables"),
                    OptionalInt32(arguments, "width"),
                    OptionalInt32(arguments, "height"),
                    OptionalUInt32(arguments, "frameRate"),
                    OptionalBoolean(arguments, "enterProject") ?? true,
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { created = true, projectRoot, mode = controller.HasProject ? "project" : "no-project", toolsChanged = controller.HasProject });
            }),
        ];

    private const string EmptySchema = "{\"type\":\"object\",\"properties\":{}}";

    private static string ObjectSchema(string properties, params string[] required)
        => $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{string.Join(',', required.Select(static name => $"\"{name}\""))}]}}";

    private static string RequiredString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static int? OptionalInt32(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : null;

    private static uint? OptionalUInt32(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetUInt32(out uint result) ? result : null;

    private static bool? OptionalBoolean(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) &&
           (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static IReadOnlyDictionary<string, string?> OptionalStringDictionary(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return new Dictionary<string, string?>();
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Invalid '{name}'.", nameof(arguments));
        return value.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString(),
            StringComparer.Ordinal);
    }
}

internal sealed class ExitProjectTool(ProjectMcpModeController controller) : McpServerTool
{
    public override Tool ProtocolTool { get; } = CreateProtocolTool();

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        bool save = true;
        if (request.Params?.Arguments?.TryGetValue("save", out JsonElement value) == true &&
            (value.ValueKind is JsonValueKind.True or JsonValueKind.False))
            save = value.GetBoolean();
        try
        {
            await controller.ExitProjectAsync(save, cancellationToken).ConfigureAwait(false);
            return DelegateTool.Success(JsonSerializer.SerializeToElement(new { mode = "no-project", saved = save, toolsChanged = true }));
        }
        catch (Exception ex)
        {
            return DelegateTool.Error(ex.Message);
        }
    }

    private static Tool CreateProtocolTool()
    {
        using JsonDocument document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"save\":{\"type\":\"boolean\",\"default\":true}}}");
        return new Tool { Name = "exit_project", Description = "Save and exit the current project, then switch to no-project tools.", InputSchema = document.RootElement.Clone() };
    }
}

internal sealed class DelegateTool : McpServerTool
{
    private readonly Func<JsonElement, CancellationToken, ValueTask<JsonElement>> _handler;
    private readonly bool _requireAuthorization;

    public DelegateTool(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler,
        bool requireAuthorization = false)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        ProtocolTool = new Tool { Name = name, Description = description, InputSchema = document.RootElement.Clone() };
        _handler = handler;
        _requireAuthorization = requireAuthorization;
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_requireAuthorization)
            {
                var services = request.Services ?? throw new InvalidOperationException("MCP request services are unavailable.");
                var authorization = services.GetRequiredService<EndpointAuthorizationManager>();
                var requestContextAccessor = services.GetRequiredService<IntegratedApiRequestContextAccessor>();
                if (!authorization.IsAuthorized(request.Server, requestContextAccessor.Current?.RemoteAddress))
                    return Error("This client endpoint is not authorized. Call authorize_client first.");
            }
            JsonElement arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
            return Success(await _handler(arguments, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    internal static CallToolResult Success(JsonElement result)
        => new() { Content = [new TextContentBlock { Text = result.GetRawText() }], IsError = false };

    internal static CallToolResult Error(string message)
        => new() { Content = [new TextContentBlock { Text = message }], IsError = true };
}
