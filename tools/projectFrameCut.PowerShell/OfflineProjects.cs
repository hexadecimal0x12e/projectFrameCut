using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#if WINDOWS
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
#endif

namespace projectFrameCut.PowerShell;

public sealed class OfflineProjectInfo
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string ProjectFile { get; init; } = "";
    public Guid ProjectId { get; init; }
    public DateTime? LastChanged { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint FrameRate { get; init; }
    public string LastOpenAppIdentifier { get; init; } = "";
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

public sealed class ProjectInstanceInfo
{
    public string Name { get; init; } = "";
    public string PackageIdentifier { get; init; } = "";
    public string Version { get; init; } = "";
    public string AppUserModelId { get; init; } = "";
    public string ExecutableAlias { get; init; } = "";
}

public sealed class ProjectTemplateVariableInfo
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "String";
    public object? DefaultValue { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}

public abstract class OfflineProjectRootCmdlet : CancellableCmdlet
{
    [Parameter] public string? ProjectRoot { get; set; }
    [Parameter] public string PjfcExecutablePath { get; set; } = "pjfc";

    protected string ResolveProjectsRoot() => string.IsNullOrWhiteSpace(ProjectRoot)
        ? Path.Combine(OfflineProjectService.GetUserDataRoot(PjfcExecutablePath, Cancellation.Token), "My Drafts")
        : GetUnresolvedProviderPathFromPSPath(ProjectRoot);
}

[Cmdlet(VerbsCommon.Get, "Project")]
[OutputType(typeof(OfflineProjectInfo))]
public sealed class GetProjectCommand : OfflineProjectRootCmdlet
{
    [Parameter(Position = 0, ValueFromPipelineByPropertyName = true)] public string? Project { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Project))
            {
                WriteObject(OfflineProjectService.ReadProject(GetUnresolvedProviderPathFromPSPath(Project)));
                return;
            }

            string root = Path.GetFullPath(ResolveProjectsRoot());
            if (!Directory.Exists(root)) return;
            foreach (string path in Directory.EnumerateDirectories(root, "*.pjfc", SearchOption.TopDirectoryOnly))
                WriteObject(OfflineProjectService.ReadProject(path));
        }
        catch (Exception ex) { Fail(ex); }
    }
}

[Cmdlet(VerbsCommon.New, "Project", SupportsShouldProcess = true)]
[OutputType(typeof(OfflineProjectInfo))]
public sealed class NewProjectCommand : OfflineProjectRootCmdlet
{
    [Parameter(Mandatory = true, Position = 0)][ValidateNotNullOrEmpty] public string Name { get; set; } = "";
    [Parameter][ValidateRange(1, int.MaxValue)] public int? Width { get; set; }
    [Parameter][ValidateRange(1, int.MaxValue)] public int? Height { get; set; }
    [Parameter][ValidateRange(1, uint.MaxValue)] public uint? FrameRate { get; set; }
    [Parameter] public string? Template { get; set; }
    [Parameter] public Hashtable Variables { get; set; } = new();
    [Parameter] public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            string root = Path.GetFullPath(ResolveProjectsRoot());
            string target = OfflineProjectService.GetNewProjectPath(root, Name);
            if (!ShouldProcess(target, "Create project")) return;
            string? template = string.IsNullOrWhiteSpace(Template) ? null : GetUnresolvedProviderPathFromPSPath(Template);
            var values = Variables.Cast<DictionaryEntry>().ToDictionary(
                static item => item.Key?.ToString() ?? "",
                static item => item.Value,
                StringComparer.OrdinalIgnoreCase);
            OfflineProjectInfo result = OfflineProjectService.CreateProject(
                root, Name, Width, Height, FrameRate, template, values, PromptForVariable);
            if (PassThru) WriteObject(result);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private object? PromptForVariable(ProjectTemplateVariableInfo variable)
    {
        try
        {
            Host.UI.Write($"{variable.DisplayName ?? variable.Name} ({variable.Type})");
            if (!string.IsNullOrWhiteSpace(variable.Description)) Host.UI.Write($": {variable.Description}");
            Host.UI.Write(": ");
            return Host.UI.ReadLine();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Template variable '{variable.Name}' is required. Supply it with -Variables.", ex);
        }
    }
}

[Cmdlet(VerbsCommon.Rename, "Project", SupportsShouldProcess = true)]
[OutputType(typeof(OfflineProjectInfo))]
public sealed class RenameProjectCommand : OfflineProjectRootCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)][Alias("Path")] public string Project { get; set; } = "";
    [Parameter(Mandatory = true, Position = 1)][ValidateNotNullOrEmpty] public string NewName { get; set; } = "";
    [Parameter] public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            OfflineProjectInfo source = OfflineProjectService.RequireProject(GetUnresolvedProviderPathFromPSPath(Project));
            string root = Path.GetFullPath(ResolveProjectsRoot());
            OfflineProjectService.EnsureProjectIsUnderRoot(source.Path, root);
            string target = OfflineProjectService.GetNewProjectPath(root, NewName);
            if (!ShouldProcess(source.Path, $"Rename project to '{NewName}'")) return;
            OfflineProjectInfo result = OfflineProjectService.RenameProject(source, target, NewName);
            if (PassThru) WriteObject(result);
        }
        catch (Exception ex) { Fail(ex); }
    }
}

[Cmdlet(VerbsCommon.Remove, "Project", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.High)]
public sealed class RemoveProjectCommand : OfflineProjectRootCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)][Alias("Path")] public string Project { get; set; } = "";
    [Parameter] public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            OfflineProjectInfo source = OfflineProjectService.RequireProject(GetUnresolvedProviderPathFromPSPath(Project));
            OfflineProjectService.EnsureProjectIsUnderRoot(source.Path, Path.GetFullPath(ResolveProjectsRoot()));
            if (!ShouldProcess(source.Path, "Delete project directory")) return;
            OfflineProjectService.DeleteProject(source.Path);
            if (PassThru) WriteObject(source);
        }
        catch (Exception ex) { Fail(ex); }
    }
}

[Cmdlet(VerbsLifecycle.Start, "Project")]
public sealed class StartProjectCommand : CancellableCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)][Alias("Path")] public string Project { get; set; } = "";
    [Parameter] public string? Instance { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            OfflineProjectInfo project = OfflineProjectService.RequireProject(GetUnresolvedProviderPathFromPSPath(Project));
            WriteObject(OfflineProjectService.StartProject(project, Instance, Cancellation.Token));
        }
        catch (Exception ex) { Fail(ex); }
    }
}

[Cmdlet(VerbsCommon.Get, "ProjectInstance")]
[OutputType(typeof(ProjectInstanceInfo))]
public sealed class GetProjectInstanceCommand : CancellableCmdlet
{
    protected override void ProcessRecord()
    {
        try
        {
            foreach (ProjectInstanceInfo instance in OfflineProjectService.GetProjectInstances(Cancellation.Token)) WriteObject(instance);
        }
        catch (Exception ex) { Fail(ex); }
    }
}

[Cmdlet(VerbsCommon.Get, "ProjectTemplateVariable")]
[OutputType(typeof(ProjectTemplateVariableInfo))]
public sealed class GetProjectTemplateVariableCommand : CancellableCmdlet
{
    [Parameter(Mandatory = true, Position = 0)][ValidateNotNullOrEmpty] public string Template { get; set; } = "";

    protected override void ProcessRecord()
    {
        try
        {
            foreach (ProjectTemplateVariableInfo variable in OfflineProjectService.ReadTemplateVariables(GetUnresolvedProviderPathFromPSPath(Template)))
                WriteObject(variable);
        }
        catch (Exception ex) { Fail(ex); }
    }
}

internal static class OfflineProjectService
{
    private const string CompatibleAliasPrefix = "projectFrameCutCompatible_";
    private static readonly Regex Placeholder = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string GetUserDataRoot(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("user_data_root");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start '{executable}'.");
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        string output = outputTask.GetAwaiter().GetResult().Trim();
        string error = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Failed to resolve the projectFrameCut user data root ({process.ExitCode}): {error}");
        string path = (string.IsNullOrWhiteSpace(error) ? output : error)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? throw new InvalidOperationException("projectFrameCut returned an empty user data root.");
        return Path.GetFullPath(path);
    }
    internal static OfflineProjectInfo ReadProject(string path)
    {
        try { return RequireProject(path); }
        catch (Exception ex)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); } catch { fullPath = path; }
            return new OfflineProjectInfo
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
                Path = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath,
                ProjectFile = File.Exists(fullPath) ? fullPath : "",
                IsValid = false,
                Error = ex.Message,
            };
        }
    }

    internal static OfflineProjectInfo RequireProject(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string directory;
        string projectFile;
        if (Directory.Exists(fullPath))
        {
            directory = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            projectFile = File.Exists(Path.Combine(directory, "project.pjfc"))
                ? Path.Combine(directory, "project.pjfc")
                : Path.Combine(directory, "project.json");
        }
        else
        {
            projectFile = fullPath;
            directory = Path.GetDirectoryName(projectFile) ?? throw new InvalidDataException("The project path has no parent directory.");
        }
        if (!File.Exists(projectFile)) throw new FileNotFoundException("The project does not contain project.pjfc or project.json.", projectFile);
        if (!File.Exists(Path.Combine(directory, "timeline.json"))) throw new FileNotFoundException("The project does not contain timeline.json.", Path.Combine(directory, "timeline.json"));
        JsonObject project = JsonNode.Parse(File.ReadAllText(projectFile)) as JsonObject
            ?? throw new InvalidDataException($"'{projectFile}' must contain a JSON object.");
        return new OfflineProjectInfo
        {
            Name = GetString(project, "ProjectName") ?? Path.GetFileNameWithoutExtension(directory),
            Path = directory,
            ProjectFile = projectFile,
            ProjectId = GetGuid(project, "ProjectUniqueId"),
            LastChanged = GetDateTime(project, "LastChanged"),
            Width = GetInt32(project, "RelativeWidth"),
            Height = GetInt32(project, "RelativeHeight"),
            FrameRate = GetUInt32(project, "TargetFrameRate"),
            LastOpenAppIdentifier = GetString(project, "LastOpenAppIdentifier") ?? "",
            IsValid = true,
        };
    }

    internal static string GetNewProjectPath(string root, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The project name contains invalid file-name characters.", nameof(name));
        return Path.Combine(Path.GetFullPath(root), name + ".pjfc");
    }

    internal static OfflineProjectInfo CreateProject(
        string root,
        string name,
        int? width,
        int? height,
        uint? frameRate,
        string? templatePath,
        IReadOnlyDictionary<string, object?> suppliedVariables,
        Func<ProjectTemplateVariableInfo, object?> prompt)
    {
        Directory.CreateDirectory(root);
        string target = GetNewProjectPath(root, name);
        if (Directory.Exists(target)) throw new IOException($"Project '{name}' already exists.");

        JsonObject project;
        JsonObject draft;
        if (templatePath is null)
        {
            project = new JsonObject();
            draft = new JsonObject
            {
                ["SnapshotID"] = Guid.Empty,
                ["Clips"] = new JsonArray(),
                ["SoundTracks"] = new JsonArray(),
                ["Duration"] = 0,
                ["AudioDuration"] = 0,
            };
        }
        else
        {
            JsonObject template = ReadTemplate(templatePath);
            project = FindProperty(template, "Project")?.DeepClone() as JsonObject
                ?? throw new InvalidDataException("The template does not contain a Project object.");
            draft = FindProperty(template, "Draft")?.DeepClone() as JsonObject
                ?? throw new InvalidDataException("The template does not contain a Draft object.");
            Dictionary<string, ProjectTemplateVariableInfo> definitions = ReadTemplateVariableMap(template);
            Dictionary<string, object?> values = ReadTemplateDefaults(template, definitions);
            foreach ((string key, object? value) in suppliedVariables)
            {
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Template variable names cannot be empty.", nameof(suppliedVariables));
                values[key] = value is PSObject ps ? ps.BaseObject : value;
            }
            HashSet<string> required = CollectPlaceholders(project).Concat(CollectPlaceholders(draft)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string key in required.Where(key => !values.ContainsKey(key)))
            {
                ProjectTemplateVariableInfo variable = definitions.TryGetValue(key, out ProjectTemplateVariableInfo? definition)
                    ? definition
                    : new ProjectTemplateVariableInfo { Name = key, Type = "Auto" };
                values[key] = prompt(variable) ?? throw new InvalidOperationException($"Template variable '{key}' is required.");
            }
            ReplacePlaceholders(project, values, definitions);
            ReplacePlaceholders(draft, values, definitions);
            string[] unresolved = CollectPlaceholders(project).Concat(CollectPlaceholders(draft)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (unresolved.Length > 0) throw new InvalidDataException("Unresolved template variables: " + string.Join(", ", unresolved));
        }

        SetProperty(project, "ProjectName", name.Trim());
        SetProperty(project, "ProjectUniqueId", Guid.CreateVersion7());
        SetProperty(project, "RelativeWidth", Math.Max(1, width ?? GetInt32(project, "RelativeWidth", 1920)));
        SetProperty(project, "RelativeHeight", Math.Max(1, height ?? GetInt32(project, "RelativeHeight", 1080)));
        SetProperty(project, "TargetFrameRate", Math.Max(1u, frameRate ?? GetUInt32(project, "TargetFrameRate", 60)));
        SetProperty(project, "NormallyExited", true);
        SetProperty(project, "LastChanged", DateTime.Now);
        SetProperty(project, "LastOpenAppVersion", typeof(OfflineProjectService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
        SetProperty(project, "LastOpenAppName", "projectFrameCut.PowerShell");
        if (string.IsNullOrWhiteSpace(GetString(project, "LastOpenAppIdentifier"))) SetProperty(project, "LastOpenAppIdentifier", "Unknown");
        SetProperty(project, "PluginUsed", new JsonArray());
        SetProperty(draft, "SavedAt", DateTime.Now);

        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            WriteJson(Path.Combine(temporary, "project.pjfc"), project);
            WriteJson(Path.Combine(temporary, "timeline.json"), draft);
            File.WriteAllText(Path.Combine(temporary, "assets.json"), "[]");
            Directory.Move(temporary, target);
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            throw;
        }
        return RequireProject(target);
    }
    internal static OfflineProjectInfo RenameProject(OfflineProjectInfo source, string target, string newName)
    {
        if (Directory.Exists(target)) throw new IOException($"Project '{newName}' already exists.");
        JsonObject project = JsonNode.Parse(File.ReadAllText(source.ProjectFile)) as JsonObject
            ?? throw new InvalidDataException($"'{source.ProjectFile}' must contain a JSON object.");
        SetProperty(project, "ProjectName", newName.Trim());
        SetProperty(project, "LastChanged", DateTime.Now);
        string temporary = source.ProjectFile + ".rename.tmp";
        WriteJson(temporary, project);
        try
        {
            Directory.Move(source.Path, target);
            string targetProjectFile = Path.Combine(target, Path.GetFileName(source.ProjectFile));
            File.Move(Path.Combine(target, Path.GetFileName(temporary)), targetProjectFile, true);
        }
        catch
        {
            if (Directory.Exists(target) && !Directory.Exists(source.Path))
            {
                try { Directory.Move(target, source.Path); } catch { }
            }
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
        return RequireProject(target);
    }

    internal static void EnsureProjectIsUnderRoot(string projectPath, string root)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedProject = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalizedProject.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException($"Project '{normalizedProject}' is outside project root '{normalizedRoot}'. Supply the matching -ProjectRoot explicitly.");
    }

    internal static void DeleteProject(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, true);
    }
    internal static IReadOnlyList<ProjectTemplateVariableInfo> ReadTemplateVariables(string path)
      => ReadTemplateVariableMap(ReadTemplate(path)).Values.OrderBy(static variable => variable.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<ProjectInstanceInfo> GetProjectInstances(CancellationToken cancellationToken)
    {
#if WINDOWS
        return DiscoverWindowsInstancesAsync(cancellationToken).GetAwaiter().GetResult();
#else
        throw new PlatformNotSupportedException("Installed projectFrameCut instance discovery is only available on Windows. Supply an executable path with Start-Project -Instance.");
#endif
    }

    internal static object StartProject(OfflineProjectInfo project, string? requestedInstance, CancellationToken cancellationToken)
    {
#if WINDOWS
        string identifier = string.IsNullOrWhiteSpace(requestedInstance) ? project.LastOpenAppIdentifier : requestedInstance.Trim();
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Project '{project.Path}' does not identify an installed instance. Supply -Instance <PackageIdentifier>.");
        IReadOnlyList<ProjectInstanceInfo> instances = GetProjectInstances(cancellationToken);
        ProjectInstanceInfo? instance = instances.FirstOrDefault(item => item.PackageIdentifier.Equals(identifier, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
            throw new InvalidOperationException($"projectFrameCut instance '{identifier}' was not found for project '{project.Path}'. Discovered instances: {FormatInstances(instances)}");
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            int result = manager.ActivateApplication(instance.AppUserModelId, $"\"{project.ProjectFile}\"", 0, out uint processId);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return new { project.Path, Instance = instance.PackageIdentifier, instance.AppUserModelId, ProcessId = processId };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to activate projectFrameCut instance '{instance.PackageIdentifier}' ({instance.AppUserModelId}) for project '{project.Path}': {ex.Message}", ex);
        }
#else
        if (string.IsNullOrWhiteSpace(requestedInstance))
            throw new InvalidOperationException("Start-Project requires -Instance <projectFrameCut executable path> on this operating system.");
        string executable = Path.GetFullPath(requestedInstance);
        if (!File.Exists(executable)) throw new FileNotFoundException("The projectFrameCut executable was not found.", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("gui");
        start.ArgumentList.Add(project.ProjectFile);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start '{executable}'.");
        if (process.WaitForExit(2000) && process.ExitCode != 0)
            throw new InvalidOperationException($"projectFrameCut instance '{executable}' exited with code {process.ExitCode} while opening '{project.Path}'.");
        return new { project.Path, Instance = executable, ProcessId = process.Id };
#endif
    }
#if WINDOWS
    private static async Task<IReadOnlyList<ProjectInstanceInfo>> DiscoverWindowsInstancesAsync(CancellationToken cancellationToken)
    {
        string aliasesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        if (!Directory.Exists(aliasesRoot)) return [];
        var aliases = Directory.EnumerateFiles(aliasesRoot, CompatibleAliasPrefix + "*.exe", SearchOption.TopDirectoryOnly)
            .Select(path => TryParseAlias(path, out string packageIdentifier, out Version? version)
                ? new { Path = path, PackageIdentifier = packageIdentifier, Version = version }
                : null)
            .Where(static item => item is not null)
            .ToArray();
        var result = new List<ProjectInstanceInfo>();
        var manager = new PackageManager();
        foreach (Package package in manager.FindPackagesForUser(string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string version = $"{package.Id.Version.Major}.{package.Id.Version.Minor}.{package.Id.Version.Build}.{package.Id.Version.Revision}";
            var alias = aliases.FirstOrDefault(item => item!.PackageIdentifier.Equals(package.Id.Name, StringComparison.OrdinalIgnoreCase) && item.Version!.ToString(4) == version);
            if (alias is null || package.IsFramework || package.IsResourcePackage) continue;
            IReadOnlyList<AppListEntry> entries = await package.GetAppListEntriesAsync();
            AppListEntry? app = entries.FirstOrDefault(static entry => entry.AppUserModelId.EndsWith("!App", StringComparison.OrdinalIgnoreCase));
            if (app is null) continue;
            string name;
            try { name = app.DisplayInfo.DisplayName; } catch { name = "projectFrameCut"; }
            result.Add(new ProjectInstanceInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? "projectFrameCut" : name,
                PackageIdentifier = package.Id.Name,
                Version = version,
                AppUserModelId = app.AppUserModelId,
                ExecutableAlias = alias.Path,
            });
        }
        return result.GroupBy(static item => item.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(item => Version.Parse(item.Version)).First())
            .OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.PackageIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseAlias(string path, out string packageIdentifier, out Version? version)
    {
        packageIdentifier = "";
        version = null;
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (!fileName.StartsWith(CompatibleAliasPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string identity = fileName[CompatibleAliasPrefix.Length..];
        int separator = identity.LastIndexOf('_');
        if (separator <= 0 || !Version.TryParse(identity[(separator + 1)..], out version) || version.Build < 0 || version.Revision < 0) return false;
        packageIdentifier = identity[..separator];
        return true;
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;
#endif

    private static JsonObject ReadTemplate(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The JSON template was not found.", path);
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"Template '{path}' must contain a JSON object.");
    }

    private static Dictionary<string, ProjectTemplateVariableInfo> ReadTemplateVariableMap(JsonObject template)
    {
        var result = new Dictionary<string, ProjectTemplateVariableInfo>(StringComparer.OrdinalIgnoreCase);
        if (FindProperty(template, "VariableDefinitions") is JsonObject definitions)
        {
            foreach ((string name, JsonNode? value) in definitions)
            {
                JsonObject definition = value as JsonObject ?? new JsonObject();
                result[name] = new ProjectTemplateVariableInfo
                {
                    Name = name,
                    Type = ReadVariableType(FindProperty(definition, "Type")),
                    DefaultValue = ToValue(FindProperty(definition, "DefaultValue")),
                    DisplayName = GetString(definition, "UserFriendlyName"),
                    Description = GetString(definition, "Description"),
                };
            }
        }
        if (FindProperty(template, "Variables") is JsonObject variables)
            foreach ((string name, JsonNode? value) in variables)
                result.TryAdd(name, new ProjectTemplateVariableInfo { Name = name, Type = "Auto", DefaultValue = ToValue(value) });
        return result;
    }

    private static Dictionary<string, object?> ReadTemplateDefaults(JsonObject template, IReadOnlyDictionary<string, ProjectTemplateVariableInfo> definitions)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (FindProperty(template, "Variables") is JsonObject variables)
            foreach ((string name, JsonNode? value) in variables) result[name] = ToValue(value);
        foreach ((string name, ProjectTemplateVariableInfo definition) in definitions)
            if (!result.ContainsKey(name) && definition.DefaultValue is not null) result[name] = definition.DefaultValue;
        return result;
    }

    private static IEnumerable<string> CollectPlaceholders(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
            foreach (Match match in Placeholder.Matches(text ?? "")) yield return match.Groups[1].Value.Trim();
        else if (node is JsonObject obj)
            foreach (JsonNode? child in obj.Select(static item => item.Value)) foreach (string key in CollectPlaceholders(child)) yield return key;
        else if (node is JsonArray array)
            foreach (JsonNode? child in array) foreach (string key in CollectPlaceholders(child)) yield return key;
    }

    private static void ReplacePlaceholders(JsonNode? node, IReadOnlyDictionary<string, object?> variables, IReadOnlyDictionary<string, ProjectTemplateVariableInfo> definitions)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(static item => item.Key).ToArray())
            {
                JsonNode? child = obj[key];
                if (child is JsonValue value && value.TryGetValue(out string? text)) obj[key] = ReplaceValue(text ?? "", variables, definitions);
                else ReplacePlaceholders(child, variables, definitions);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                JsonNode? child = array[i];
                if (child is JsonValue value && value.TryGetValue(out string? text)) array[i] = ReplaceValue(text ?? "", variables, definitions);
                else ReplacePlaceholders(child, variables, definitions);
            }
        }
    }

    private static JsonNode? ReplaceValue(string text, IReadOnlyDictionary<string, object?> variables, IReadOnlyDictionary<string, ProjectTemplateVariableInfo> definitions)
    {
        Match full = Placeholder.Match(text);
        if (full.Success && full.Index == 0 && full.Length == text.Length)
        {
            string key = full.Groups[1].Value.Trim();
            if (variables.TryGetValue(key, out object? value))
                return ConvertValue(value, definitions.TryGetValue(key, out ProjectTemplateVariableInfo? definition) ? definition.Type : "Auto");
        }
        return JsonValue.Create(Placeholder.Replace(text, match => variables.TryGetValue(match.Groups[1].Value.Trim(), out object? value)
            ? Convert.ToString(value is PSObject ps ? ps.BaseObject : value, CultureInfo.InvariantCulture) ?? ""
            : match.Value));
    }

    private static JsonNode? ConvertValue(object? value, string type)
    {
        value = value is PSObject ps ? ps.BaseObject : value;
        if (value is null) return null;
        if (type.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            if (value is JsonNode node) return node.DeepClone();
            if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
            return JsonNode.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null");
        }
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (type.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
            return bool.TryParse(text, out bool boolean) ? JsonValue.Create(boolean) : throw new FormatException($"'{text}' is not a Boolean.");
        if (type.Equals("Integer", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer) ? JsonValue.Create(integer) : throw new FormatException($"'{text}' is not an Integer.");
        if (type.Equals("Number", StringComparison.OrdinalIgnoreCase))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? JsonValue.Create(number) : throw new FormatException($"'{text}' is not a Number.");
        if (type.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(text, out var boolean)) return JsonValue.Create(boolean);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
        }
        return JsonValue.Create(text);
    }

    private static string ReadVariableType(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)) return text;
            if (value.TryGetValue(out int number) && number is >= 0 and <= 6) return new[] { "Auto", "String", "Number", "Integer", "Boolean", "File", "Json" }[number];
        }
        return "String";
    }

    private static object? ToValue(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonValue value) return node.DeepClone();
        if (value.TryGetValue(out string? text)) return text;
        if (value.TryGetValue(out bool boolean)) return boolean;
        if (value.TryGetValue(out long integer)) return integer;
        if (value.TryGetValue(out double number)) return number;
        return value.ToJsonString();
    }

    private static JsonNode? FindProperty(JsonObject obj, string name)
        => obj.FirstOrDefault(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void SetProperty(JsonObject obj, string name, object? value)
    {
        string key = obj.Select(static item => item.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        obj[key] = value switch
        {
            JsonNode node => node,
            Guid guid => JsonValue.Create(guid),
            DateTime time => JsonValue.Create(time),
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            int integer => JsonValue.Create(integer),
            uint integer => JsonValue.Create(integer),
            _ => JsonSerializer.SerializeToNode(value),
        };
    }

    private static string? GetString(JsonObject obj, string name)
        => FindProperty(obj, name) is JsonValue value && value.TryGetValue(out string? result) ? result : null;

    private static int GetInt32(JsonObject obj, string name, int fallback = 0)
        => FindProperty(obj, name) is JsonValue value && value.TryGetValue(out int result) ? result : fallback;

    private static uint GetUInt32(JsonObject obj, string name, uint fallback = 0)
        => FindProperty(obj, name) is JsonValue value && value.TryGetValue(out uint result) ? result : fallback;

    private static Guid GetGuid(JsonObject obj, string name)
        => Guid.TryParse(GetString(obj, name), out Guid result) ? result : Guid.Empty;

    private static DateTime? GetDateTime(JsonObject obj, string name)
        => DateTime.TryParse(GetString(obj, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime result) ? result : null;

    private static void WriteJson(string path, JsonNode node)
        => File.WriteAllText(path, node.ToJsonString(JsonOptions));

    private static string FormatInstances(IReadOnlyList<ProjectInstanceInfo> instances)
        => instances.Count == 0 ? "none" : string.Join(", ", instances.Select(static item => $"{item.PackageIdentifier} ({item.Version})"));

}
