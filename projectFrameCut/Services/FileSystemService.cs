using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Storage;
using System;
using System.Diagnostics;
using System.IO;

#if WINDOWS
using System.Runtime.InteropServices;
using System.Threading;
#elif ANDROID
using Android.Content;
using Android.Net;
using Android.Provider;
using AndroidX.Core.Content;
using projectFrameCut.Platforms.Android;
#endif

namespace projectFrameCut.Services
{
    /// <summary>
    /// 跨平台文件系统服务，用于在不同平台上打开文件或文件夹
    /// </summary>
    public static class FileSystemService
    {
        public static async Task<string?> PickFolderAsync(CancellationToken ct = default)
        {
            var result = await FolderPicker.Default.PickAsync(ct);
            if (result.IsSuccessful)
            {
                return result.Folder.Path;
            }
            else
            {
                return null;
            }
        }

        public static async Task<string> PickSavePathAsync(string defaultName, string? defaultRootDir = null)
        {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker();

        // 获取当前窗口句柄
        var hwnd = ((MauiWinUIWindow)Application.Current.Windows[0].Handler.PlatformView).WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        // 设置文件类型过滤器
        picker.FileTypeChoices.Add("视频文件", new List<string> { ".mp4", ".mkv", ".avi", ".mov" });
        //picker.FileTypeChoices.Add("所有文件", new List<string> { ".*" });

        // 设置默认文件名
        picker.SuggestedFileName = defaultName ?? $"Export_{DateTime.Now:yyyyMMdd_HHmmss}";

        if(defaultRootDir is not null && Directory.Exists(defaultRootDir)) 
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(defaultRootDir);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        }

        // 显示保存对话框
        var file = await picker.PickSaveFileAsync();

        return file?.Path ?? string.Empty;
#elif ANDROID
            if (!HasAllFilesAccess())
            {
                var result = await FileSaver.Default.SaveAsync(defaultRootDir ?? string.Empty,defaultName, new MemoryStream([0]));
                if (result.IsSuccessful)
                {
                    return result.FilePath;

                }
                else
                {
                    throw new OperationCanceledException("User cancelled the save file operation.");
                }
            }
            else 
            {
                if (defaultRootDir is null || defaultName is null) throw new ArgumentNullException("On this case (android and no full-file-access permission), defaultRootDir and defaultName must be provided.");
                return Path.Combine(defaultRootDir, defaultName);
            }
#else
            if (defaultRootDir is null || defaultName is null) throw new ArgumentNullException("On this platform, defaultRootDir and defaultName must be provided.");
            return Path.Combine(defaultRootDir, defaultName);
#endif
        }


        /// <summary>
        /// 打开文件夹
        /// </summary>
        /// <param name="folderPath">文件夹路径</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> OpenFolderAsync(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                    return false;

#if WINDOWS
                // Windows 平台
                if (!Directory.Exists(folderPath))
                    return false;

                // 使用 explorer 打开文件夹
                var processInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = folderPath,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    return process != null;
                }

#elif ANDROID
                var ctx = MainApplication.MainContext ?? throw new InvalidOperationException("MainApplication.MainContext is null");

                var uri = Android.Net.Uri.Parse("file://" + folderPath);
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, "resource/folder");
                intent.AddFlags(ActivityFlags.NewTask);
                
                try
                {
                    ctx.StartActivity(intent);
                    return true;
                }
                catch
                {
                    // 如果默认文件管理器不支持，尝试使用其他方式
                    return await TryOpenWithFileManager(folderPath);
                }

#elif IOS || MACCATALYST
                // iOS/Mac Catalyst 平台 - 不支持直接打开文件夹
                return false;

#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening folder: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 打开文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> OpenFileAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return false;

#if WINDOWS
                // Windows 平台
                if (!File.Exists(filePath))
                    return false;

                // 使用默认关联程序打开文件
                var processInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    return process != null;
                }

#elif ANDROID
                if (!File.Exists(filePath))
                    return false;

                var ctx = MainApplication.MainContext ?? throw new InvalidOperationException("MainApplication.MainContext is null");
                var file = new Java.IO.File(filePath);
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, $"{ctx.PackageName}.fileprovider", file);
                
                var intent = new Intent(Intent.ActionView);
                intent.SetData(uri);
                intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                
                try
                {
                    ctx.StartActivity(intent);
                    return true;
                }
                catch
                {
                    return false;
                }

#elif IOS || MACCATALYST
                // iOS/Mac Catalyst 平台 - 不支持直接打开文件
                return false;

#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening file: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 在文件浏览器中显示文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <returns>操作是否成功</returns>
        public static async Task<bool> ShowFileInFolderAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return false;

#if WINDOWS
                // Windows 平台 - 使用 explorer /select 显示文件
                if (!File.Exists(filePath))
                    return false;

                var processInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select, \"{filePath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    return process != null;
                }

#elif ANDROID
                // Android 平台 - 打开文件所在文件夹
                var folderPath = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(folderPath))
                    return false;

                return await OpenFolderAsync(folderPath);

#elif IOS || MACCATALYST
                return false;

#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing file in folder: {ex.Message}");
                return false;
            }
        }

#if ANDROID
        /// <summary>
        /// Android 平台 - 尝试使用文件管理器打开文件夹
        /// </summary>
        private static async Task<bool> TryOpenWithFileManager(string folderPath)
        {
            try
            {
                var ctx = MainApplication.MainContext ?? throw new InvalidOperationException("MainApplication.MainContext is null");
                
                // 尝试使用常见的文件管理器
                var fileManagerPackages = new[]
                {
                    "com.android.documentsui",  // Android Files
                    "com.sec.android.app.myfiles", // Samsung Files
                    "com.mi.android.globalFileexplorer", // Xiaomi Files
                };

                var intent = new Intent(Intent.ActionView);
                var uri = Android.Net.Uri.Parse("file://" + folderPath);
                intent.SetData(uri);
                intent.AddFlags(ActivityFlags.NewTask);

                foreach (var packageName in fileManagerPackages)
                {
                    intent.SetPackage(packageName);
                    try
                    {
                        ctx.StartActivity(intent);
                        return true;
                    }
                    catch
                    {
                        // 继续尝试下一个
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in TryOpenWithFileManager: {ex.Message}");
                return false;
            }
        }
#endif

#if ANDROID
        public static bool HasAllFilesAccess()
        {
#if ANDROID30_0_OR_GREATER
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
            {
                return Android.OS.Environment.IsExternalStorageManager;
            }
#endif
            return true;
        }

        public static void RequestAllFilesAccess()
        {
            var ctx = MainApplication.MainContext ?? throw new InvalidOperationException("MainApplication.MainContext is null");

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R && !Android.OS.Environment.IsExternalStorageManager)
            {
                Intent intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission);
                intent.SetData(Android.Net.Uri.Parse($"package:{ctx.PackageName}"));
                intent.AddFlags(ActivityFlags.NewTask);
                ctx.StartActivity(intent);
            }
        }
#endif
    }
}
