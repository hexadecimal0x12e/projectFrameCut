using LocalizedResources;
using projectFrameCut.Asset;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace projectFrameCut.Setting.SettingPages;

public partial class UserDataManagePage : ContentPage
{
    private sealed class UserDataSnapshot
    {
        public List<UserDataEntry> DraftItems { get; init; } = [];
        public List<UserDataEntry> AssetItems { get; init; } = [];
        public List<UserDataEntry> TemplateItems { get; init; } = [];
        public List<UserDataEntry> PluginItems { get; init; } = [];
    }

    public sealed class UserDataEntry
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public long SizeInBytes { get; init; }
        public string SizeText => FormatSize(SizeInBytes);
        public bool IsDirectory { get; init; }
        public string? AssetId { get; init; }
    }

    private readonly string _draftPath = Path.Combine(MauiProgram.DataPath, "My Drafts");
    private readonly string _assetPath = Path.Combine(MauiProgram.DataPath, "My Assets");
    private readonly string _templatePath = Path.Combine(MauiProgram.DataPath, "My Templates");
    private readonly string _pluginPath = Path.Combine(MauiProgram.BasicDataPath, "Plugins");
    private readonly View _mainContent;
    private bool _hasLoadedOnce;
    private bool _isLoading;

    public ObservableCollection<UserDataEntry> DraftItems { get; } = [];
    public ObservableCollection<UserDataEntry> AssetItems { get; } = [];
    public ObservableCollection<UserDataEntry> TemplateItems { get; } = [];
    public ObservableCollection<UserDataEntry> PluginItems { get; } = [];

    public UserDataManagePage()
    {
        InitializeComponent();
        BindingContext = this;
        _mainContent = Content ?? new ContentView();
        Content = CreateLoadingContent();
        EnsureManagedDirectories();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_isLoading) return;

        await RefreshStatsAsync(showLoading: !_hasLoadedOnce);
        _hasLoadedOnce = true;
    }

    private static View CreateLoadingContent()
    {
        return new VerticalStackLayout
        {
            Children =
            {
                new ActivityIndicator
                {
                    IsRunning = true,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.Center
                },
                new Label
                {
                    Text = Localized.LandingPage_Loading,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
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

        var dbPath = Path.Combine(_assetPath, ".database", "database.json");
        if (!File.Exists(dbPath))
        {
            File.WriteAllText(dbPath, "{}");
        }
    }

    private async Task RefreshStatsAsync(bool showLoading = false)
    {
        if (_isLoading) return;
        _isLoading = true;

        if (showLoading)
        {
            Content = CreateLoadingContent();
        }

        try
        {
            var snapshot = await Task.Run(BuildSnapshot);
            ApplySnapshot(snapshot);
        }
        finally
        {
            if (showLoading)
            {
                Content = _mainContent;
            }

            _isLoading = false;
        }
    }

    private UserDataSnapshot BuildSnapshot()
    {
        var draftItems = new List<UserDataEntry>();
        var assetItems = new List<UserDataEntry>();
        var templateItems = new List<UserDataEntry>();
        var pluginItems = new List<UserDataEntry>();

        if (Directory.Exists(_draftPath))
        {
            foreach (var dir in Directory.GetDirectories(_draftPath, "*", SearchOption.TopDirectoryOnly))
            {
                draftItems.Add(new UserDataEntry
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    SizeInBytes = GetDirectorySize(dir),
                    IsDirectory = true
                });
            }
        }

        foreach (var asset in AssetDatabase.Assets.Values
            .Where(a => !string.IsNullOrWhiteSpace(a.Path) && File.Exists(a.Path)))
        {
            var path = asset.Path!;
            assetItems.Add(new UserDataEntry
            {
                Name = string.IsNullOrWhiteSpace(asset.Name) ? Path.GetFileName(path) : asset.Name,
                FullPath = path,
                SizeInBytes = GetFileSize(path),
                IsDirectory = false,
                AssetId = asset.AssetId
            });
        }

        if (Directory.Exists(_templatePath))
        {
            foreach (var file in Directory.GetFiles(_templatePath, "*.json", SearchOption.AllDirectories))
            {
                templateItems.Add(new UserDataEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FullPath = file,
                    SizeInBytes = GetFileSize(file),
                    IsDirectory = false
                });
            }
        }

        if (Directory.Exists(_pluginPath))
        {
            foreach (var dir in Directory.GetDirectories(_pluginPath, "*", SearchOption.TopDirectoryOnly))
            {
                pluginItems.Add(new UserDataEntry
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    SizeInBytes = GetDirectorySize(dir),
                    IsDirectory = true
                });
            }
        }

        return new UserDataSnapshot
        {
            DraftItems = draftItems.OrderByDescending(i => i.SizeInBytes).ThenBy(i => i.Name).ToList(),
            AssetItems = assetItems.OrderByDescending(i => i.SizeInBytes).ThenBy(i => i.Name).ToList(),
            TemplateItems = templateItems.OrderByDescending(i => i.SizeInBytes).ThenBy(i => i.Name).ToList(),
            PluginItems = pluginItems.OrderByDescending(i => i.SizeInBytes).ThenBy(i => i.Name).ToList()
        };
    }

    private void ApplySnapshot(UserDataSnapshot snapshot)
    {
        SetItems(DraftItems, snapshot.DraftItems);
        SetItems(AssetItems, snapshot.AssetItems);
        SetItems(TemplateItems, snapshot.TemplateItems);
        SetItems(PluginItems, snapshot.PluginItems);

        DraftStatLabel.Text = Localized.UserDataManagePage_DataState(DraftItems.Count, FormatSize(DraftItems.Sum(i => i.SizeInBytes)));
        AssetStatLabel.Text = Localized.UserDataManagePage_DataState(AssetItems.Count, FormatSize(AssetItems.Sum(i => i.SizeInBytes)));
        TemplateStatLabel.Text = Localized.UserDataManagePage_DataState(TemplateItems.Count, FormatSize(TemplateItems.Sum(i => i.SizeInBytes)));
        PluginStatLabel.Text = Localized.UserDataManagePage_DataState(PluginItems.Count, FormatSize(PluginItems.Sum(i => i.SizeInBytes)));
    }

    private static void SetItems(ObservableCollection<UserDataEntry> target, IEnumerable<UserDataEntry> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long size = 0;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                size += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore files currently locked by another process.
            }
        }

        return size;
    }

    private static string FormatSize(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var idx = 0;
        while (value >= 1024 && idx < units.Length - 1)
        {
            value /= 1024;
            idx++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[idx]}";
    }

    private async Task OpenDirectoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        var opened = await FileSystemService.OpenFolderAsync(path);
        if (!opened)
        {
            await DisplayAlertAsync(Localized._Error, Localized.UserDataManagePage_OpenActionNotSupported, Localized._OK);
        }
    }


    private async Task<bool> ConfirmDeleteItemAsync(string name)
    {
        var confirm0 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(name), Localized._Confirm, Localized._Cancel);
        if (!confirm0) return false;
        var confirm1 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm1(name), Localized._Confirm, Localized._Cancel);
        if (!confirm1) return false;
        var confirm2 = await DisplayPromptAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm2Input(name), Localized._Confirm, Localized._Cancel, "no");

        return confirm2 == "yes";
    }

    private static void ClearDirectoryContents(string path)
    {
        if (!Directory.Exists(path)) return;

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }

        foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
        {
            Directory.Delete(dir, true);
        }
    }

    private async void OnOpenDraftsClicked(object sender, EventArgs e)
    {
        await OpenDirectoryAsync(_draftPath);
    }

    private async void OnOpenAssetsClicked(object sender, EventArgs e)
    {
        await OpenDirectoryAsync(_assetPath);
    }

    private async void OnOpenTemplatesClicked(object sender, EventArgs e)
    {
        await OpenDirectoryAsync(_templatePath);
    }

    private async void OnOpenPluginsClicked(object sender, EventArgs e)
    {
        await OpenDirectoryAsync(_pluginPath);
    }

    private async void OnOpenItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string path || string.IsNullOrWhiteSpace(path)) return;

        if (Directory.Exists(path))
        {
            await OpenDirectoryAsync(path);
            return;
        }

        if (File.Exists(path))
        {
            var shown = await FileSystemService.ShowFileInFolderAsync(path);
            if (!shown)
            {
                await DisplayAlertAsync(Localized._Error, $"无法打开位置:\n{path}", Localized._OK);
            }
        }
    }

    private async void OnDeleteDraftItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not UserDataEntry entry) return;
        if (!await ConfirmDeleteItemAsync(entry.Name)) return;

        try
        {
            if (Directory.Exists(entry.FullPath))
            {
                Directory.Delete(entry.FullPath, true);
            }
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "Delete draft item", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private async void OnDeleteAssetItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not UserDataEntry entry) return;
        if (!await ConfirmDeleteItemAsync(entry.Name)) return;

        try
        {
            var removed = false;
            if (!string.IsNullOrWhiteSpace(entry.AssetId))
            {
                removed = AssetDatabase.Remove(entry.AssetId);
            }

            if (!removed && File.Exists(entry.FullPath))
            {
                File.Delete(entry.FullPath);
            }

            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "Delete asset item", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private async void OnDeleteTemplateItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not UserDataEntry entry) return;
        if (!await ConfirmDeleteItemAsync(entry.Name)) return;

        try
        {
            if (File.Exists(entry.FullPath))
            {
                File.Delete(entry.FullPath);
            }
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "Delete template item", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private async void OnDeletePluginItemClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not UserDataEntry entry) return;
        if (!await ConfirmDeleteItemAsync(entry.Name)) return;

        try
        {
            if (Directory.Exists(entry.FullPath))
            {
                Directory.Delete(entry.FullPath, true);
            }

            if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "plugins.json")))
            {
                var json = File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"));
                var items = System.Text.Json.JsonSerializer.Deserialize<List<PluginService.PluginItem>>(json) ?? [];
                items = items.Where(i => i.Id != entry.Name).ToList();
                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"), System.Text.Json.JsonSerializer.Serialize(items));
                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), System.Text.Json.JsonSerializer.Serialize(items));
            }

            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "Delete plugin item", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}