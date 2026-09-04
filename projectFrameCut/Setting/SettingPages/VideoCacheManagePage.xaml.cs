using Microsoft.Maui.Controls.PlatformConfiguration;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static projectFrameCut.Setting.SettingManager.SettingsManager;


namespace projectFrameCut.Setting.SettingPages;

public partial class VideoCacheManagePage : ContentPage
{
    public sealed class CacheItem
    {
        public string MD5 { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public long SizeInBytes { get; init; }
        public string SizeText => FormatSize(SizeInBytes);
        public DateTime LastAccess { get; init; }
        public string LastAccessText => LastAccess.ToString("g", CultureInfo.CurrentCulture);
        public string DisplayName => System.IO.Path.GetFileName(Path);
    }

    public ObservableCollection<CacheItem> CacheItems { get; } = new();
    private readonly View _mainContent;
    private bool _hasLoadedOnce;
    private bool _isLoading;

    public VideoCacheManagePage()
    {
        InitializeComponent();
        if (string.IsNullOrWhiteSpace(VideoFrameDiskCache.CacheBaseDir) || !Directory.Exists(VideoFrameDiskCache.CacheBaseDir))
        {
            VideoFrameDiskCache.CacheBaseDir = Path.Combine(MauiProgram.CachePath, "VideoFrameCache");
        }
        CurrentPathLabel.Text = Localized.VideoCacheManagePage_CurrentPath(VideoFrameDiskCache.CacheBaseDir);
        if (GetSettingAs<long>("codec_VideoFrameDiskCacheMaxSizeMB", 0) != 0)
        {
            int sizeInGB = GetSettingAs<int>("codec_VideoFrameDiskCacheMaxSizeMB", 0) / 1024;
            MaxSizePicker.SelectedItem = MaxSizePicker.ItemsSource.Contains($"{sizeInGB} GB") ? $"{sizeInGB} GB" : Localized.DraftPage_PrevResultion_Custom;
        }
        else
        {
            MaxSizePicker.SelectedIndex = 0;
        }
        EnableCompressCheckBox.IsChecked = IsBoolSettingTrueOrDefault("codec_VideoFrameDiskCacheEnableCompress", true);
        SelectPathButton.IsVisible = OperatingSystem.IsWindows();
        BindingContext = this;
        _mainContent = Content ?? new ContentView();
        Content = CreateLoadingContent();

    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_isLoading) return;
        await RefreshAsync(showLoading: !_hasLoadedOnce);
        _hasLoadedOnce = true;
    }

    private static View CreateLoadingContent()
    {
        return new VerticalStackLayout
        {
            Children =
            {
                new ActivityIndicator { IsRunning = true, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = Localized.LandingPage_Loading, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }
            },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
    }

    private async Task RefreshAsync(bool showLoading = false)
    {
        if (_isLoading) return;
        _isLoading = true;
        if (showLoading) Content = CreateLoadingContent();
        Dictionary<string, long> sizes = new();
        try
        {
            sizes = await Task.Run(() =>
            {
                var dict = new Dictionary<string, long>();
                foreach (var cache in VideoFrameDiskCacheManager.Caches)
                {
                    try
                    {
                        var size = Directory.GetFiles(Path.Combine(VideoFrameDiskCache.CacheBaseDir, cache.MD5), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                        dict[cache.MD5] = size;
                    }
                    catch { }
                }
                return dict;
            });
        }
        catch { }
        try
        {
            var items = await Task.Run(() => VideoFrameDiskCacheManager.Caches.Select(c => new CacheItem
            {
                MD5 = c.MD5,
                Path = c.Path,
                SizeInBytes = sizes.TryGetValue(c.MD5, out var size) ? size : 0,
                LastAccess = c.LastAccess
            }).OrderByDescending(i => i.SizeInBytes).ToList());

            SetItems(CacheItems, items);
        }
        finally
        {
            if (showLoading) Content = _mainContent;
            _isLoading = false;
        }

        try
        {
            var totalSize = Directory.GetFiles(VideoFrameDiskCache.CacheBaseDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            CurrentSizeLabel.Text = Localized.UserDataManagePage_DataState(VideoFrameDiskCacheManager.Caches.Count, FormatSize(totalSize));
        }
        catch { }
    }

    private static void SetItems(ObservableCollection<CacheItem> target, IEnumerable<CacheItem> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
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

    private async void RefreshButton_Clicked(object sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async void ClearAllButton_Clicked(object sender, EventArgs e)
    {
        if (!await DisplayAlertAsync(Localized._Warn, Localized.VideoCacheManagePage_CleanupAllWarn, Localized._OK, Localized._Cancel)) return;
        var hashes = VideoFrameDiskCacheManager.Caches.Select(c => c.MD5).ToList();
        foreach (var h in hashes) VideoFrameDiskCacheManager.RemoveFromCache(h);
        await RefreshAsync();
    }

    private async void OnDeleteCacheClicked(object sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string hash) return;
        if (!await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(hash), Localized._OK, Localized._Cancel)) return;
        try
        {
            VideoFrameDiskCacheManager.RemoveFromCache(hash);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "Remove cache", this);
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
    }

    private async void OnOpenFolderClicked(object sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string hash) return;
        var baseDir = VideoFrameDiskCache.CacheBaseDir ?? Path.Combine(MauiProgram.CachePath, "VideoFrameCache");
        var dir = Path.Combine(baseDir, hash);
        Directory.CreateDirectory(dir);
        var opened = await FileSystemService.OpenFolderAsync(dir);
    }

    private async void CleanUnusedButton_Clicked(object sender, EventArgs e)
    {
        if (this is not ContentPage page)
            return;

        string result = await page.DisplayActionSheetAsync(
            Localized.DraftSettingPage_Tab_History_CleanupAll_Title,
            Localized._Cancel,
            null,
            Localized.DraftSettingPage_Tab_History_HoursAgo(24),
            Localized.DraftSettingPage_Tab_History_HoursAgo(48),
            Localized.DraftSettingPage_Tab_History_HoursAgo(72),
            Localized.DraftSettingPage_Tab_History_DaysAgo(7),
            Localized.DraftSettingPage_Tab_History_DaysAgo(14),
            Localized.DraftSettingPage_Tab_History_DaysAgo(28)
        );

        if (string.IsNullOrWhiteSpace(result) || result == Localized._Cancel)
            return;

        var cutoffTime = result switch
        {
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(24) => DateTime.Now.AddHours(-24),
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(48) => DateTime.Now.AddHours(-48),
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(72) => DateTime.Now.AddHours(-72),
            _ when result == Localized.DraftSettingPage_Tab_History_DaysAgo(7) => DateTime.Now.AddDays(-7),
            _ when result == Localized.DraftSettingPage_Tab_History_DaysAgo(14) => DateTime.Now.AddDays(-14),
            _ when result == Localized.DraftSettingPage_Tab_History_DaysAgo(28) => DateTime.Now.AddDays(-28),
            _ => DateTime.Now.AddDays(-1)
        };

        await CleanUnusedCachesAsync(cutoffTime, result);
    }

    private async Task CleanUnusedCachesAsync(DateTime cutoffTime, string timeLabel)
    {
        try
        {
            var cachesToDelete = VideoFrameDiskCacheManager.Caches
                .Where(c => c.LastAccess < cutoffTime)
                .Select(c => c.MD5)
                .ToList();

            if (cachesToDelete.Count == 0)
            {
                await DisplayAlertAsync(Localized._Info, Localized.DraftSettingPage_Tab_History_CleanupAll_None, Localized._OK);
                return;
            }

            var totalSize = cachesToDelete.Sum(hash =>
            {
                try
                {
                    return Directory.GetFiles(Path.Combine(VideoFrameDiskCache.CacheBaseDir, hash), "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }
                catch { return 0; }
            });

            var confirmed = await DisplayAlertAsync(
                Localized._Warn,
                Localized.VideoCacheManagePage_CleanupWarn(timeLabel, cachesToDelete.Count),
                Localized._Confirm,
                Localized._Cancel);

            if (!confirmed) return;

            foreach (var hash in cachesToDelete)
            {
                try
                {
                    VideoFrameDiskCacheManager.RemoveFromCache(hash);
                }
                catch { }
            }

            await RefreshAsync();
            await DisplayAlertAsync("Success", $"Deleted {cachesToDelete.Count} cache(s).", "OK");
        }
        catch (Exception ex)
        {
            Log(ex, "Clean unused caches", this);
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
    }

    private async void SelectPathButton_Clicked(object sender, EventArgs e)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var mauiWin = Application.Current?.Windows?.FirstOrDefault();
        if (mauiWin?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        picker.CommitButtonText = SettingLocalizedResources.General_UserData_SelectFolder_ConfirmButton;
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null)
        {
            var conf = await DisplayAlertAsync(Localized._Warn,
                SettingLocalizedResources.General_UserData_SelectFolder_ConfirmReset,
                Localized._Confirm,
                Localized._Cancel);
            if (conf)
            {
                VideoFrameDiskCache.CacheBaseDir = Path.Combine(MauiProgram.CachePath, "VideoFrameCache");
                return;
            }
            else
            {
                return;
            }
        }
        var fullPath = folder.Path;
        var oldPath = VideoFrameDiskCache.CacheBaseDir;


        if (fullPath != null)
        {
            var conf1 = await DisplayAlertAsync(Localized._Info,
                SettingLocalizedResources.General_UserData_SelectFolder_Confirm(fullPath),
                Localized._Confirm,
                 Localized._Cancel);
            if (conf1)
            {
                var conf2 = await DisplayAlertAsync(Localized._Info,
                    SettingLocalizedResources.General_UserData_SelectFolder_ConfirmMigrateData,
                    Localized._Confirm,
                    Localized._Cancel);
                if (conf2)
                {
                    var cont = Content;
                    var files = Directory.GetFiles(oldPath, "*", SearchOption.AllDirectories);
                    uint finished = 0;
                    int duplicated = 0;
                    Stopwatch cd = Stopwatch.StartNew();
                    var procLabel = new Label
                    {
                        Text = Localized._Processing,
                        FontSize = 20,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                    };
                    var mover = Task.Run(async () =>
                    {
                        try
                        {
                            foreach (var f in files)
                            {
                                var destFile = Path.Combine(fullPath, Path.GetRelativePath(oldPath, f));
                                LogDiagnostic($"Clone {f} to {destFile}...");
                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                                    if (!File.Exists(destFile))
                                    {
                                        File.Copy(f, destFile);
                                        await Task.Delay(1);
                                        File.Delete(f);
                                    }
                                    else duplicated++;
                                    finished++;
                                    if (cd.Elapsed.Seconds > 2)
                                    {
                                        await Dispatcher.DispatchAsync(() =>
                                        {
                                            procLabel.Text = Localized._ProcessingWithProg(finished / files.Length);
                                        });
                                        cd.Restart();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    var skip = await Dispatcher.DispatchAsync(async () =>
                                    {
                                        var cont = await DisplayAlertAsync(Localized._Warn,
                                        SettingLocalizedResources.General_UserData_SelectFolder_MigrateError(Path.GetFileName(f), ex),
                                        SettingLocalizedResources.General_UserData_SelectFolder_MigrateError_Skip,
                                        Localized._Cancel);
                                        return cont;
                                    });
                                    if (!skip)
                                    {
                                        await Dispatcher.DispatchAsync(async () =>
                                        {
                                            Content = cont;
                                        });
                                        return;
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                    await Dispatcher.DispatchAsync(async () =>
                    {

                        Content = new VerticalStackLayout
                        {
                            Children =
                            {
                                new ActivityIndicator
                                {
                                    IsRunning = true,
                                    VerticalOptions = LayoutOptions.Center,
                                    HorizontalOptions = LayoutOptions.Center
                                },
                                procLabel,
                                new Label
                                {
                                    Text = SettingLocalizedResources.Diag_MakingReport_Sub,
                                    FontSize = 28,
                                    TextColor = Colors.OrangeRed,
                                    HorizontalOptions = LayoutOptions.Center,
                                    VerticalOptions = LayoutOptions.Center
                                }

                            },
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center
                        };

                    });
                    await mover;

                    Dispatcher.Dispatch(() => Content = cont);
                    if (duplicated > 0)
                    {
                        await DisplayAlertAsync(Localized._Info,
                            SettingLocalizedResources.General_UserData_SelectFolder_FinishedWithConflict(duplicated),
                            Localized._OK);
                    }
                    else
                    {
                        var del = await DisplayAlertAsync(Localized._Info,
                            SettingLocalizedResources.General_UserData_SelectFolder_FinishedNoConflict(),
                            Localized._OK,
                            Localized._Cancel);
                        if (del)
                        {
                            await Task.Run(() =>
                            {
                                try
                                {
                                    Directory.Delete(oldPath, true);
                                }
                                catch { }
                            });
                        }
                    }
                }
                WriteSetting("codec_VideoFrameDiskCachePath", fullPath);
                VideoFrameDiskCache.CacheBaseDir = fullPath;
            }
            else
            {
                return;
            }
        }

#endif

    }

    private async void MaxSizePicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        var sizeString = MaxSizePicker.SelectedItem as string;
        if (sizeString == Localized.VideoCacheManagePage_MaxSize_None)
        {
            WriteSetting("codec_VideoFrameDiskCacheMaxSizeMB", "0");
            return;
        }
        else if (uint.TryParse(sizeString.Split(' ', StringSplitOptions.TrimEntries).FirstOrDefault(""), out var gb))
        {
            WriteSetting("codec_VideoFrameDiskCacheMaxSizeMB", (gb * 1024).ToString());
            return;
        }
        else
        {
            var input = await DisplayPromptAsync(Localized._Info, Localized.VideoCacheManagePage_MaxSize_CustomMessage, initialValue: "10");
            if (uint.TryParse(input, out var gb1))
            {
                WriteSetting("codec_VideoFrameDiskCacheMaxSizeMB", (gb1 * 1024).ToString());
                return;
            }
        }
    }

    private async void BuildCacheButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var pickOptions = new PickOptions
            {
                PickerTitle = "Select video file",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".flv", ".webm" } },
                    { DevicePlatform.Android, new[] { "video/*" } },
                    { DevicePlatform.iOS, new[] { "public.movie" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.movie" } }
                })
            };

            var fileResult = await FilePicker.Default.PickAsync(pickOptions);
            if (fileResult == null || string.IsNullOrWhiteSpace(fileResult.FullPath))
                return;

            string videoPath = fileResult.FullPath;
            CancellationTokenSource cts = new();
            var procLabel = new Label
            {
                Text = Localized._Processing,
                FontSize = 20,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };

            var progressBar = new ProgressBar { Progress = 0, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Center, WidthRequest = 200 };

            var previousContent = Content;
            Content = new VerticalStackLayout
            {
                Children =
                {
                    procLabel,
                    progressBar,
                    new Button
                    {
                        Text = Localized._Cancel,
                        Command = new Command(cts.Cancel),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                    }
                },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Margin = new(50, 0, 50, 0),
                Spacing = 8

            };

            var progress = new Progress<double>(p =>
            {
                // Ensure UI update on UI thread
                Dispatcher.Dispatch(() =>
                {
                    progressBar.Progress = p;
                    procLabel.Text = Localized._ProcessingWithProg(p);
                });
            });

            await Task.Run(() => VideoFrameDiskCacheManager.ManualBuildCache(videoPath, progress, cts.Token), cts.Token);

            await RefreshAsync();

            await DisplayAlertAsync(Localized._Info, Localized._Done, Localized._OK);
            Content = previousContent;
        }
        catch (Exception ex)
        {
            Log(ex, "Build cache", this);
            await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private void EnableCompressCheckBox_CheckedChanged(object sender, CheckedChangedEventArgs e)
    {
        WriteSetting("codec_VideoFrameDiskCacheEnableCompress", e.Value.ToString());
        VideoFrameDiskCache.EnableCompression = e.Value;
    }
}
