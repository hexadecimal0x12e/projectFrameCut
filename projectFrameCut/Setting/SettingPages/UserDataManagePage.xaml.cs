using LocalizedResources;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace projectFrameCut.Setting.SettingPages;

public partial class UserDataManagePage : ContentPage
{
    private enum StorageCategory
    {
        Projects,
        Assets,
        Templates,
        Plugins,
        Cache,
        Diagnostics,
        ConfigResources,
        Unspecified
    }

    private sealed class ProjectSizeBuilder(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public long CacheSize { get; set; }
        public long ProxySize { get; set; }
        public long AssetSize { get; set; }
        public long OtherSize { get; set; }

        public ProjectDataEntry Build() => new()
        {
            Name = Name,
            FullPath = Path,
            CacheSizeInBytes = CacheSize,
            ProxySizeInBytes = ProxySize,
            AssetSizeInBytes = AssetSize,
            OtherSizeInBytes = OtherSize
        };
    }

    private sealed class DiskSnapshot
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public bool IsCombined { get; set; }
        public bool IsUserDataDisk { get; set; }
        public bool HasDiskInfo { get; init; }
        public long TotalSize { get; init; }
        public long FreeSize { get; init; }
        public Dictionary<StorageCategory, long> Categories { get; } = [];

        public long ManagedSize => Categories.Values.Sum();
    }

    private sealed class UserDataSnapshot
    {
        public List<ProjectDataEntry> DraftItems { get; init; } = [];
        public List<UserDataEntry> AssetItems { get; init; } = [];
        public List<UserDataEntry> TemplateItems { get; init; } = [];
        public List<UserDataEntry> PluginItems { get; init; } = [];
        public List<DiskSnapshot> Disks { get; init; } = [];
        public Dictionary<StorageCategory, long> CategorySizes { get; init; } = [];
    }

    private sealed record VolumeSnapshot(string Key, string Name, bool HasDiskInfo, long TotalSize, long FreeSize);
    private sealed record CleanupResult(long DeletedBytes, int FailedFiles);
    private sealed record CleanupProgress(int ProcessedFiles, int TotalFiles, long ProcessedBytes, long TotalBytes);

    public sealed class UserDataEntry
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public long SizeInBytes { get; init; }
        public string SizeText => FormatSize(SizeInBytes);
        public string? AssetId { get; init; }
    }

    public sealed class ProjectDataEntry
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public long CacheSizeInBytes { get; init; }
        public long ProxySizeInBytes { get; init; }
        public long AssetSizeInBytes { get; init; }
        public long OtherSizeInBytes { get; init; }
        public long SizeInBytes => CacheSizeInBytes + ProxySizeInBytes + AssetSizeInBytes + OtherSizeInBytes;
        public string SizeText => FormatSize(SizeInBytes);
        public string CacheSizeText => FormatSize(CacheSizeInBytes);
        public string ProxySizeText => FormatSize(ProxySizeInBytes);
        public string AssetSizeText => FormatSize(AssetSizeInBytes);
        public string OtherSizeText => FormatSize(OtherSizeInBytes);
        public bool HasCache => CacheSizeInBytes > 0;
        public bool HasProxy => ProxySizeInBytes > 0;
    }

    public sealed class ChartLegendItem
    {
        public required string Name { get; init; }
        public required string ValueText { get; init; }
        public required Color Color { get; init; }
        public long Value { get; init; }
    }

    public sealed class DiskChartItem
    {
        public required string Title { get; init; }
        public required string Summary { get; init; }
        public required IDrawable Drawable { get; init; }
        public List<ChartLegendItem> LegendItems { get; init; } = [];
    }

    private sealed class PieDrawable(IReadOnlyList<ChartLegendItem> slices) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            long total = slices.Sum(x => x.Value);
            if (total <= 0) return;

            float size = Math.Max(1, Math.Min(dirtyRect.Width, dirtyRect.Height) - 16);
            float left = dirtyRect.X + (dirtyRect.Width - size) / 2;
            float top = dirtyRect.Y + (dirtyRect.Height - size) / 2;
            float start = -90;
            canvas.Antialias = true;

            if (slices.Count(x => x.Value > 0) == 1)
            {
                canvas.FillColor = slices.First(x => x.Value > 0).Color;
                canvas.FillEllipse(left, top, size, size);
                return;
            }

            foreach (var slice in slices.Where(x => x.Value > 0))
            {
                float sweep = 360f * slice.Value / total;
                var path = new PathF();
                path.MoveTo(left + size / 2, top + size / 2);
                path.AddArc(left, top, left + size, top + size, start, start + sweep, false);
                path.Close();
                canvas.FillColor = slice.Color;
                canvas.FillPath(path);
                start += sweep;
            }
        }
    }

    private readonly string _draftPath = Path.Combine(MauiProgram.DataPath, "My Drafts");
    private readonly string _assetPath = Path.Combine(MauiProgram.DataPath, "My Assets");
    private readonly string _templatePath = Path.Combine(MauiProgram.DataPath, "My Templates");
    private readonly string _pluginPath = Path.Combine(MauiProgram.BasicDataPath, "Plugins");
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private CancellationTokenSource? _refreshCts;
    private UserDataSnapshot? _lastSnapshot;
    private bool _isLoading;
    private bool _operationRunning;
    private bool _themeSubscribed;

    public ObservableCollection<ProjectDataEntry> DraftItems { get; } = [];
    public ObservableCollection<UserDataEntry> AssetItems { get; } = [];
    public ObservableCollection<UserDataEntry> TemplateItems { get; } = [];
    public ObservableCollection<UserDataEntry> PluginItems { get; } = [];
    public ObservableCollection<DiskChartItem> DiskCharts { get; } = [];
    public UserDataManagePage()
    {
        InitializeComponent();
        BindingContext = this;
        EnsureManagedDirectories();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SubscribeTheme();
        await RefreshStatsAsync();
    }

    protected override void OnDisappearing()
    {
        _refreshCts?.Cancel();
        if (_themeSubscribed && Application.Current is not null)
        {
            Application.Current.RequestedThemeChanged -= OnRequestedThemeChanged;
            _themeSubscribed = false;
        }
        base.OnDisappearing();
    }

    private void SubscribeTheme()
    {
        if (_themeSubscribed || Application.Current is null) return;
        Application.Current.RequestedThemeChanged += OnRequestedThemeChanged;
        _themeSubscribed = true;
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        if (_lastSnapshot is not null) ApplyDiskCharts(_lastSnapshot.Disks);
    }

    private void EnsureManagedDirectories()
    {
        Directory.CreateDirectory(_draftPath);
        Directory.CreateDirectory(_assetPath);
        Directory.CreateDirectory(Path.Combine(_assetPath, ".database"));
        Directory.CreateDirectory(Path.Combine(_assetPath, ".thumbnails"));
        Directory.CreateDirectory(Path.Combine(_assetPath, ".proxy"));
        Directory.CreateDirectory(_templatePath);
        Directory.CreateDirectory(_pluginPath);

        string dbPath = Path.Combine(_assetPath, ".database", "database.json");
        if (!File.Exists(dbPath)) File.WriteAllText(dbPath, "{}");
    }

    private async Task RefreshStatsAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var cts = _refreshCts;
        SetBusy(true, Localized.LandingPage_Loading);

        try
        {
            var snapshot = await Task.Run(() => BuildSnapshot(cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _lastSnapshot = snapshot;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(ex, "Refresh user data statistics", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _isLoading = false;
                if (!_operationRunning) SetBusy(false);
            }
        }
    }

    private UserDataSnapshot BuildSnapshot(CancellationToken cancellationToken)
    {
        var projects = GetDirectoriesSafe(_draftPath)
            .ToDictionary(NormalizePath, p => new ProjectSizeBuilder(Path.GetFileName(p), p), PathComparer());
        var disks = CreateDiskSnapshots();
        var categorySizes = Enum.GetValues<StorageCategory>().ToDictionary(x => x, _ => 0L);

        foreach (string root in GetNonOverlappingScanRoots())
        {
            ScanFiles(root, file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = GetFileSize(file);
                StorageCategory category = Classify(file);
                categorySizes[category] += size;

                DiskSnapshot disk = SelectDisk(disks, file);
                disk.Categories[category] = disk.Categories.GetValueOrDefault(category) + size;

                ProjectSizeBuilder? project = FindProject(projects, file);
                if (project is null) return;
                if (IsUnder(file, Path.Combine(project.Path, "thumbs"))) project.CacheSize += size;
                else if (IsUnder(file, Path.Combine(project.Path, "proxy"))) project.ProxySize += size;
                else if (IsUnder(file, Path.Combine(project.Path, "assets")) ||
                         string.Equals(Path.GetFileName(file), "assets.json", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(Path.GetDirectoryName(file), project.Path, _pathComparison)) project.AssetSize += size;
                else project.OtherSize += size;
            }, cancellationToken);
        }

        var assetItems = AssetDatabase.Assets.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.Path) && File.Exists(a.Path))
            .Select(a => new UserDataEntry
            {
                Name = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileName(a.Path) : a.Name,
                FullPath = a.Path!,
                SizeInBytes = GetFileSize(a.Path!),
                AssetId = a.AssetId
            })
            .OrderByDescending(x => x.SizeInBytes).ThenBy(x => x.Name).ToList();

        var templateItems = GetFilesSafe(_templatePath, "*.json", SearchOption.AllDirectories)
            .Select(file => new UserDataEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                FullPath = file,
                SizeInBytes = GetFileSize(file)
            })
            .OrderByDescending(x => x.SizeInBytes).ThenBy(x => x.Name).ToList();

        var pluginItems = GetDirectoriesSafe(_pluginPath)
            .Select(dir => new UserDataEntry
            {
                Name = Path.GetFileName(dir),
                FullPath = dir,
                SizeInBytes = GetDirectorySize(dir, cancellationToken)
            })
            .OrderByDescending(x => x.SizeInBytes).ThenBy(x => x.Name).ToList();

        return new UserDataSnapshot
        {
            DraftItems = projects.Values.Select(x => x.Build()).OrderByDescending(x => x.SizeInBytes).ThenBy(x => x.Name).ToList(),
            AssetItems = assetItems,
            TemplateItems = templateItems,
            PluginItems = pluginItems,
            Disks = disks,
            CategorySizes = categorySizes
        };
    }

    private List<DiskSnapshot> CreateDiskSnapshots()
    {
        VolumeSnapshot user = GetVolumeSnapshot(MauiProgram.DataPath);
        VolumeSnapshot app = GetVolumeSnapshot(MauiProgram.BasicDataPath);
        if (string.Equals(user.Key, app.Key, _pathComparison))
        {
            return [new DiskSnapshot
            {
                Key = user.Key,
                Name = user.Name,
                IsCombined = true,
                IsUserDataDisk = true,
                HasDiskInfo = user.HasDiskInfo,
                TotalSize = user.TotalSize,
                FreeSize = user.FreeSize
            }];
        }

        return
        [
            new DiskSnapshot
            {
                Key = user.Key,
                Name = user.Name,
                IsUserDataDisk = true,
                HasDiskInfo = user.HasDiskInfo,
                TotalSize = user.TotalSize,
                FreeSize = user.FreeSize
            },
            new DiskSnapshot
            {
                Key = app.Key,
                Name = app.Name,
                HasDiskInfo = app.HasDiskInfo,
                TotalSize = app.TotalSize,
                FreeSize = app.FreeSize
            }
        ];
    }

    private DiskSnapshot SelectDisk(List<DiskSnapshot> disks, string file)
    {
        if (disks.Count == 1) return disks[0];
        bool appData = IsUnder(file, MauiProgram.BasicDataPath) ||
                       IsUnder(file, MauiProgram.CachePath) ||
                       IsUnder(file, FileSystem.AppDataDirectory);
        return appData ? disks.First(x => !x.IsUserDataDisk) : disks.First(x => x.IsUserDataDisk);
    }

    private VolumeSnapshot GetVolumeSnapshot(string path)
    {
        string root = path;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                return new VolumeSnapshot(root, drive.Name, true, drive.TotalSize, drive.AvailableFreeSpace);
            }

            return new VolumeSnapshot(root, drive.Name, false, 0, 0);
        }
        catch (Exception ex)
        {
            Log(ex, $"Read disk information for {path}", this);
            return new VolumeSnapshot(root, root, false, 0, 0);
        }
    }

    private ProjectSizeBuilder? FindProject(Dictionary<string, ProjectSizeBuilder> projects, string file)
    {
        if (!IsUnder(file, _draftPath)) return null;
        string relative = Path.GetRelativePath(_draftPath, file);
        string? first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return null;
        projects.TryGetValue(NormalizePath(Path.Combine(_draftPath, first)), out var project);
        return project;
    }

    private StorageCategory Classify(string file)
    {
        if (GetDiagnosticRoots().Any(root => IsUnder(file, root)) || IsCrashCachePath(file)) return StorageCategory.Diagnostics;
        if (IsUnder(file, _pluginPath)) return StorageCategory.Plugins;
        if (GetCacheRoots().Any(root => IsUnder(file, root))) return StorageCategory.Cache;
        if (IsUnder(file, _draftPath)) return StorageCategory.Projects;
        if (IsUnder(file, _assetPath)) return StorageCategory.Assets;
        if (IsUnder(file, _templatePath)) return StorageCategory.Templates;
        if (IsUnder(file, MauiProgram.DataPath)) return StorageCategory.Unspecified;
        return StorageCategory.ConfigResources;
    }

    private bool IsCrashCachePath(string file)
    {
        if (!IsUnder(file, MauiProgram.CachePath)) return false;
        string relative = Path.GetRelativePath(MauiProgram.CachePath, file);
        string first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return first.StartsWith("crash_", StringComparison.OrdinalIgnoreCase) || first.Equals("logging", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetCacheRoots()
    {
        yield return MauiProgram.CachePath;
        yield return Path.Combine(MauiProgram.DataPath, "RenderCache");
        yield return Path.Combine(MauiProgram.DataPath, "Remote Assets");
        yield return Path.Combine(MauiProgram.BasicDataPath, "EBWebView");
        yield return Path.Combine(FileSystem.AppDataDirectory, "EBWebView");
    }

    private IEnumerable<string> GetDiagnosticRoots()
    {
        foreach (string root in new[] { MauiProgram.DataPath, MauiProgram.BasicDataPath, FileSystem.AppDataDirectory })
        {
            yield return Path.Combine(root, "logging");
            yield return Path.Combine(root, "Logs");
            yield return Path.Combine(root, "Crashlogs");
            yield return Path.Combine(root, "diag");
            yield return Path.Combine(root, "RenderDiag");
            yield return Path.Combine(root, "RenderCheckpoint");
            yield return Path.Combine(root, "RenderJobs");
        }
    }

    private List<string> GetNonOverlappingScanRoots()
    {
        var roots = new[] { MauiProgram.DataPath, MauiProgram.BasicDataPath, MauiProgram.CachePath, FileSystem.AppDataDirectory }
            .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .Select(NormalizePath)
            .Distinct(PathComparer())
            .OrderBy(x => x.Length)
            .ToList();
        return roots.Where(root => !roots.Any(other => !string.Equals(root, other, _pathComparison) && IsUnder(root, other))).ToList();
    }

    private void ApplySnapshot(UserDataSnapshot snapshot)
    {
        SetItems(DraftItems, snapshot.DraftItems);
        SetItems(AssetItems, snapshot.AssetItems);
        SetItems(TemplateItems, snapshot.TemplateItems);
        SetItems(PluginItems, snapshot.PluginItems);
        ApplyDiskCharts(snapshot.Disks);

        DraftStatLabel.Text = Localized.UserDataManagePage_DataState(DraftItems.Count, FormatSize(DraftItems.Sum(x => x.SizeInBytes)));
        AssetStatLabel.Text = Localized.UserDataManagePage_DataState(AssetItems.Count, FormatSize(AssetItems.Sum(x => x.SizeInBytes)));
        TemplateStatLabel.Text = Localized.UserDataManagePage_DataState(TemplateItems.Count, FormatSize(TemplateItems.Sum(x => x.SizeInBytes)));
        PluginStatLabel.Text = Localized.UserDataManagePage_DataState(PluginItems.Count, FormatSize(PluginItems.Sum(x => x.SizeInBytes)));
        CacheStatusLabel.Text = FormatSize(snapshot.CategorySizes[StorageCategory.Cache]);
        DiagnosticsStatusLabel.Text = FormatSize(snapshot.CategorySizes[StorageCategory.Diagnostics]);
        ConfigStatusLabel.Text = FormatSize(snapshot.CategorySizes[StorageCategory.ConfigResources]);
        ClearCacheButton.IsEnabled = snapshot.CategorySizes[StorageCategory.Cache] > 0;
        ClearDiagnosticsButton.IsEnabled = snapshot.CategorySizes[StorageCategory.Diagnostics] > 0;
    }

    private void ApplyDiskCharts(IEnumerable<DiskSnapshot> disks)
    {
        DiskCharts.Clear();
        foreach (var disk in disks)
        {
            long otherUsed = disk.HasDiskInfo ? Math.Max(0, disk.TotalSize - disk.FreeSize - disk.ManagedSize) : 0;
            var values = new List<(string Name, long Value, Color Color)>
            {
                (Localized.UserDataManagePage_CategoryProjects, disk.Categories.GetValueOrDefault(StorageCategory.Projects), GetCategoryColor(StorageCategory.Projects)),
                (Localized.UserDataManagePage_CategoryAssets, disk.Categories.GetValueOrDefault(StorageCategory.Assets), GetCategoryColor(StorageCategory.Assets)),
                (Localized.UserDataManagePage_CategoryTemplates, disk.Categories.GetValueOrDefault(StorageCategory.Templates), GetCategoryColor(StorageCategory.Templates)),
                (Localized.UserDataManagePage_CategoryPlugins, disk.Categories.GetValueOrDefault(StorageCategory.Plugins), GetCategoryColor(StorageCategory.Plugins)),
                (Localized.UserDataManagePage_CategoryCache, disk.Categories.GetValueOrDefault(StorageCategory.Cache), GetCategoryColor(StorageCategory.Cache)),
                (Localized.UserDataManagePage_CategoryDiagnostics, disk.Categories.GetValueOrDefault(StorageCategory.Diagnostics), GetCategoryColor(StorageCategory.Diagnostics)),
                (Localized.UserDataManagePage_CategoryConfigResources, disk.Categories.GetValueOrDefault(StorageCategory.ConfigResources), GetCategoryColor(StorageCategory.ConfigResources)),
                (Localized.UserDataManagePage_CategoryUnspecified, disk.Categories.GetValueOrDefault(StorageCategory.Unspecified), GetCategoryColor(StorageCategory.Unspecified))
            };
            if (disk.HasDiskInfo)
            {
                values.Add((Localized.UserDataManagePage_CategoryOtherUsed, otherUsed, ResolveColor("Gray550", Colors.Gray)));
                values.Add((Localized.UserDataManagePage_CategoryFree, disk.FreeSize, ResolveColor("Gray200", Colors.LightGray)));
            }

            long denominator = disk.HasDiskInfo ? disk.TotalSize : Math.Max(1, disk.ManagedSize);
            var legends = values.Where(x => x.Value > 0).Select(x => new ChartLegendItem
            {
                Name = x.Name,
                Value = x.Value,
                Color = x.Color,
                ValueText = $"{FormatSize(x.Value)} · {100d * x.Value / denominator:0.##}%"
            }).ToList();
            string title = disk.IsCombined
                ? Localized.UserDataManagePage_DiskCombined
                : disk.IsUserDataDisk
                    ? Localized.UserDataManagePage_DiskUser
                    : Localized.UserDataManagePage_DiskApplication;
            DiskCharts.Add(new DiskChartItem
            {
                Title = string.IsNullOrWhiteSpace(disk.Name) ? title : $"{title} {(disk.IsUserDataDisk ? $"· {MauiProgram.DataPath}" : "")} ",
                Summary = disk.HasDiskInfo
                    ? $"{FormatSize(disk.TotalSize - disk.FreeSize)} / {FormatSize(disk.TotalSize)}"
                    : Localized.UserDataManagePage_DiskUnavailable,
                LegendItems = legends,
                Drawable = new PieDrawable(legends)
            });
        }
    }

    private Color GetCategoryColor(StorageCategory category)
    {
        return ClipElementUI.GetPaletteColor((int)category);
    }

    private static Color ResolveColor(string key, Color fallback)
    {
        return Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Color color ? color : fallback;
    }

    private static void SetItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private void SetBusy(bool busy, string? text = null, bool showProgress = false)
    {
        BusyOverlay.IsVisible = busy;
        if (!string.IsNullOrWhiteSpace(text)) BusyLabel.Text = text;
        BusyProgressBar.IsVisible = showProgress;
        BusyProgressLabel.IsVisible = showProgress;
        if (showProgress)
        {
            BusyProgressBar.Progress = 0;
            BusyProgressLabel.Text = "0%";
        }
    }

    private void UpdateCleanupProgress(CleanupProgress progress)
    {
        double value = progress.TotalFiles == 0 ? 1 : (double)progress.ProcessedFiles / progress.TotalFiles;
        BusyProgressBar.Progress = value;
        BusyProgressLabel.Text = $"{progress.ProcessedFiles:N0} / {progress.TotalFiles:N0} · {FormatSize(progress.ProcessedBytes)} / {FormatSize(progress.TotalBytes)} · {value:P0}";
    }

    private async Task RunCleanupAsync(string name, long size, Func<CancellationToken, IProgress<CleanupProgress>, CleanupResult> cleanup)
    {
        if (_operationRunning || size <= 0) return;
        if (!(await DisplayAlertAsync(Localized._Warn, Localized.UserDataManagePage_ClearWarn(name, FormatSize(size)), Localized._Confirm, Localized._Cancel) && await DisplayAlertAsync(Localized._Warn, Localized.UserDataManagePage_ClearWarn2(name), Localized._Confirm, Localized._Cancel))) return;

        _operationRunning = true;
        SetBusy(true, $"{Localized._Processing} · {name}", true);
        try
        {
            var progress = new Progress<CleanupProgress>(UpdateCleanupProgress);
            var result = await Task.Run(() => cleanup(CancellationToken.None, progress));
            await DisplayAlertAsync(Localized._Info,
                result.FailedFiles == 0
                    ? Localized.HomePage_ProjectContextMenu_Delete_Deleted(name)
                    : Localized.HomePage_ProjectContextMenu_Delete_Fail(name, new InvalidOperationException($"{result.FailedFiles} failed to delete. {FormatSize(result.DeletedBytes)} are cleaned.")),
                Localized._OK);
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, $"Clean {name}", this);
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_ProjectContextMenu_Delete_Fail(name, ex), Localized._OK);
        }
        finally
        {
            _operationRunning = false;
            if (!_isLoading) SetBusy(false);
        }
    }

    private CleanupResult DeleteCategoryFiles(StorageCategory category, CancellationToken cancellationToken, IProgress<CleanupProgress> progress)
    {
        var files = new List<(string Path, long Size)>();
        foreach (string root in GetNonOverlappingScanRoots())
        {
            ScanFiles(root, file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Classify(file) == category) files.Add((file, GetFileSize(file)));
            }, cancellationToken);
        }
        return DeleteFiles(files, cancellationToken, progress, $"Delete {category} file");
    }

    private CleanupResult DeleteFilesInDirectory(string path, CancellationToken cancellationToken, IProgress<CleanupProgress> progress, bool removeRoot = false)
    {
        var files = new List<(string Path, long Size)>();
        ScanFiles(path, file => files.Add((file, GetFileSize(file))), cancellationToken);
        var result = DeleteFiles(files, cancellationToken, progress, "Delete user data file");
        if (removeRoot && result.FailedFiles == 0 && Directory.Exists(path)) Directory.Delete(path, true);
        return result;
    }

    private CleanupResult DeleteFiles(List<(string Path, long Size)> files, CancellationToken cancellationToken, IProgress<CleanupProgress> progress, string operation)
    {
        long deleted = 0;
        int failed = 0;
        long processed = 0;
        long total = files.Sum(x => x.Size);
        long lastReport = 0;
        progress.Report(new CleanupProgress(0, files.Count, 0, total));
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(files[i].Path);
                deleted += files[i].Size;
            }
            catch (FileNotFoundException) { }
            catch (Exception ex)
            {
                failed++;
                Log(ex, $"{operation} {files[i].Path}", this);
            }
            processed += files[i].Size;
            if (i == files.Count - 1 || Environment.TickCount64 - lastReport >= 100)
            {
                progress.Report(new CleanupProgress(i + 1, files.Count, processed, total));
                lastReport = Environment.TickCount64;
            }
        }
        return new CleanupResult(deleted, failed);
    }

    private async void ClearCacheButton_Clicked(object sender, EventArgs e)
    {
        if (_lastSnapshot is null) return;
        await RunCleanupAsync(Localized.UserDataManagePage_CategoryCache,
            _lastSnapshot.CategorySizes.GetValueOrDefault(StorageCategory.Cache),
            (ct, progress) => DeleteCategoryFiles(StorageCategory.Cache, ct, progress));
    }

    private async void ClearDiagnosticsButton_Clicked(object sender, EventArgs e)
    {
        if (_lastSnapshot is null) return;
        await RunCleanupAsync(Localized.UserDataManagePage_CategoryDiagnostics,
            _lastSnapshot.CategorySizes.GetValueOrDefault(StorageCategory.Diagnostics),
            (ct, progress) => DeleteCategoryFiles(StorageCategory.Diagnostics, ct, progress));
    }

    private async void OnClearProjectCacheClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ProjectDataEntry entry }) return;
        await RunCleanupAsync($"{entry.Name} · {Localized.UserDataManagePage_ProjectCache}", entry.CacheSizeInBytes,
            (ct, progress) => DeleteFilesInDirectory(Path.Combine(entry.FullPath, "thumbs"), ct, progress));
    }

    private async void OnClearProjectProxyClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ProjectDataEntry entry }) return;
        await RunCleanupAsync($"{entry.Name} · {Localized.UserDataManagePage_ProjectProxy}", entry.ProxySizeInBytes,
            (ct, progress) => DeleteFilesInDirectory(Path.Combine(entry.FullPath, "proxy"), ct, progress));
    }

    private async Task OpenDirectoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        if (!await FileSystemService.OpenFolderAsync(path))
            await DisplayAlertAsync(Localized._Error, Localized.UserDataManagePage_OpenActionNotSupported, Localized._OK);
    }

    private async Task<bool> ConfirmDeleteItemAsync(string name)
    {
        if (!await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(name), Localized._Confirm, Localized._Cancel)) return false;
        if (!await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm1(name), Localized._Confirm, Localized._Cancel)) return false;
        return await DisplayPromptAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm2Input(name), Localized._Confirm, Localized._Cancel, "no") == "yes";
    }

    private async void OnOpenItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string path } || string.IsNullOrWhiteSpace(path)) return;
        if (Directory.Exists(path))
        {
            await OpenDirectoryAsync(path);
            return;
        }
        if (File.Exists(path) && !await FileSystemService.ShowFileInFolderAsync(path))
            await DisplayAlertAsync(Localized._Error, Localized.UserDataManagePage_OpenActionNotSupported, Localized._OK);
    }

    private async void OnDeleteDraftItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ProjectDataEntry entry } || !await ConfirmDeleteItemAsync(entry.Name)) return;
        await DeleteAndRefreshAsync(entry.Name,
            progress => DeleteFilesInDirectory(entry.FullPath, CancellationToken.None, progress, true));
    }

    private async void OnDeleteAssetItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: UserDataEntry entry } || !await ConfirmDeleteItemAsync(entry.Name)) return;
        await DeleteAndRefreshAsync(entry.Name, progress => RunTrackedAction(entry.SizeInBytes, progress, () =>
        {
            bool removed = !string.IsNullOrWhiteSpace(entry.AssetId) && AssetDatabase.Remove(entry.AssetId);
            if (!removed && File.Exists(entry.FullPath)) File.Delete(entry.FullPath);
        }));
    }

    private async void OnDeleteTemplateItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: UserDataEntry entry } || !await ConfirmDeleteItemAsync(entry.Name)) return;
        await DeleteAndRefreshAsync(entry.Name, progress => RunTrackedAction(entry.SizeInBytes, progress,
            () => { if (File.Exists(entry.FullPath)) File.Delete(entry.FullPath); }));
    }

    private async void OnDeletePluginItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: UserDataEntry entry } || !await ConfirmDeleteItemAsync(entry.Name)) return;
        await DeleteAndRefreshAsync(entry.Name, progress =>
        {
            var result = DeleteFilesInDirectory(entry.FullPath, CancellationToken.None, progress, true);
            string path = Path.Combine(MauiProgram.BasicDataPath, "plugins.json");
            if (File.Exists(path))
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<PluginService.PluginItem>>(File.ReadAllText(path)) ?? [];
                items = items.Where(x => x.Id != entry.Name).ToList();
                string json = System.Text.Json.JsonSerializer.Serialize(items);
                File.WriteAllText(path, json);
                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), json);
            }
            return result;
        });
    }

    private CleanupResult RunTrackedAction(long size, IProgress<CleanupProgress> progress, Action action)
    {
        progress.Report(new CleanupProgress(0, 1, 0, size));
        action();
        progress.Report(new CleanupProgress(1, 1, size, size));
        return new CleanupResult(size, 0);
    }

    private async Task DeleteAndRefreshAsync(string name, Func<IProgress<CleanupProgress>, CleanupResult> action)
    {
        if (_operationRunning) return;
        _operationRunning = true;
        SetBusy(true, $"{Localized._Processing} · {name}", true);
        try
        {
            var progress = new Progress<CleanupProgress>(UpdateCleanupProgress);
            await Task.Run(() => action(progress));
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, $"Delete {name}", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
        finally
        {
            _operationRunning = false;
            if (!_isLoading) SetBusy(false);
        }
    }

    private void ScanFiles(string root, Action<string> action, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir)) action(file);
                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if ((new DirectoryInfo(child).Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                    catch (Exception ex) { Log(ex, $"Inspect storage directory {child}", this); }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Log(ex, $"Scan storage directory {dir}", this);
            }
        }
    }

    private long GetDirectorySize(string path, CancellationToken cancellationToken)
    {
        long size = 0;
        ScanFiles(path, file => size += GetFileSize(file), cancellationToken);
        return size;
    }

    private long GetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
        catch (Exception ex)
        {
            Log(ex, $"Read storage file {path}", this);
            return 0;
        }
    }

    private IEnumerable<string> GetDirectoriesSafe(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception ex)
        {
            Log(ex, $"Enumerate storage directories {path}", this);
            return [];
        }
    }

    private IEnumerable<string> GetFilesSafe(string path, string pattern, SearchOption option)
    {
        try { return Directory.Exists(path) ? Directory.GetFiles(path, pattern, option) : []; }
        catch (Exception ex)
        {
            Log(ex, $"Enumerate storage files {path}", this);
            return [];
        }
    }

    private string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        string fullPath = NormalizePath(path);
        string fullRoot = NormalizePath(root);
        if (string.Equals(fullPath, fullRoot, _pathComparison)) return true;
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, _pathComparison) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, _pathComparison);
    }

    private StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FormatSize(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int idx = 0;
        while (value >= 1024 && idx < units.Length - 1)
        {
            value /= 1024;
            idx++;
        }
        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[idx]}";
    }
}
