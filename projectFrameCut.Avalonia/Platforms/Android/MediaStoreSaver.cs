#if ANDROID
using Android.Content;
using Android.OS;
using Android.Provider;
using Java.IO;
using Uri = Android.Net.Uri;
using AndroidEnvironment = Android.OS.Environment;

namespace projectFrameCut.Platforms.Android;

/// <summary>
/// 用于在 Android 设备上正确保存媒体文件到公共媒体目录
/// 支持 Android 10+ 的分区存储 (Scoped Storage)
/// </summary>
public static class MediaStoreSaver
{
    /// <summary>
    /// 媒体类型枚举
    /// </summary>
    public enum MediaType
    {
        Video,  // 保存到 Movies
        Image,  // 保存到 Pictures
        Audio   // 保存到 Music
    }

    /// <summary>
    /// 将文件保存到媒体目录
    /// </summary>
    /// <param name="sourceFilePath">源文件的完整路径</param>
    /// <param name="displayName">显示名称（不含扩展名）</param>
    /// <param name="mimeType">MIME 类型，如 "video/mp4"</param>
    /// <param name="mediaType">媒体类型</param>
    /// <param name="subFolder">子文件夹名称（可选，如 "projectFrameCut"）</param>
    /// <returns>保存后的文件 URI，失败返回 null</returns>
    public static Uri? SaveToMediaStore(
        string sourceFilePath,
        string displayName,
        string mimeType,
        MediaType mediaType,
        string? subFolder = null)
    {
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;

        if (resolver == null)
            return null;

        // 根据媒体类型选择正确的 URI 和相对路径
        var (externalUri, relativePath) = GetMediaUriAndPath(mediaType, subFolder);

        // 创建 ContentValues
        var contentValues = new ContentValues();
        contentValues.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        contentValues.Put(MediaStore.IMediaColumns.MimeType, mimeType);

        // Android 10+ 使用 RELATIVE_PATH
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            contentValues.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
            // 设置 IS_PENDING 为 1，表示文件正在写入
            contentValues.Put(MediaStore.IMediaColumns.IsPending, 1);
        }

        Uri? uri = null;
        try
        {
            // 插入记录获取 URI
            uri = resolver.Insert(externalUri, contentValues);
            if (uri == null)
                return null;

            // 打开输出流并写入文件
            using var outputStream = resolver.OpenOutputStream(uri);
            if (outputStream == null)
            {
                // 清理失败的记录
                resolver.Delete(uri, null, null);
                return null;
            }

            using var inputStream = new FileInputStream(sourceFilePath);
            inputStream.TransferTo(outputStream);
            // Android 10+ 完成写入后清除 IS_PENDING 标志
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                contentValues.Clear();
                contentValues.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(uri, contentValues, null, null);
            }

            return uri;
        }
        catch (Exception)
        {
            // 发生错误时清理
            if (uri != null)
            {
                try { resolver.Delete(uri, null, null); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// 异步保存文件到媒体目录
    /// </summary>
    public static async Task<Uri?> SaveToMediaStoreAsync(
        string sourceFilePath,
        string displayName,
        string mimeType,
        MediaType mediaType,
        string? subFolder = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver;

        if (resolver == null)
            return null;

        var (externalUri, relativePath) = GetMediaUriAndPath(mediaType, subFolder);

        var contentValues = new ContentValues();
        contentValues.Put(MediaStore.IMediaColumns.DisplayName, displayName);
        contentValues.Put(MediaStore.IMediaColumns.MimeType, mimeType);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            contentValues.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
            contentValues.Put(MediaStore.IMediaColumns.IsPending, 1);
        }

        Uri? uri = null;
        try
        {
            uri = resolver.Insert(externalUri, contentValues);
            if (uri == null)
                return null;

            using var outputStream = resolver.OpenOutputStream(uri);
            if (outputStream == null)
            {
                resolver.Delete(uri, null, null);
                return null;
            }

            var fileInfo = new System.IO.FileInfo(sourceFilePath);
            var totalBytes = fileInfo.Length;
            var buffer = new byte[81920]; // 80KB buffer
            long totalRead = 0;

            using var inputStream = System.IO.File.OpenRead(sourceFilePath);
            int bytesRead;
            while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await outputStream.WriteAsync(buffer, 0, bytesRead);
                totalRead += bytesRead;
                progress?.Report((double)totalRead / totalBytes);
            }

            await outputStream.FlushAsync();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                contentValues.Clear();
                contentValues.Put(MediaStore.IMediaColumns.IsPending, 0);
                resolver.Update(uri, contentValues, null, null);
            }

            return uri;
        }
        catch (System.OperationCanceledException)
        {
            if (uri != null)
            {
                try { resolver.Delete(uri, null, null); } catch { }
            }
            throw;
        }
        catch (Exception)
        {
            if (uri != null)
            {
                try { resolver.Delete(uri, null, null); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// 获取适用于旧版 Android 的直接文件路径（Android 9 及以下）
    /// </summary>
    public static string GetLegacyMediaPath(MediaType mediaType, string? subFolder = null)
    {
        string basePath = mediaType switch
        {
            MediaType.Video => AndroidEnvironment.GetExternalStoragePublicDirectory(
                AndroidEnvironment.DirectoryMovies)?.AbsolutePath ?? "/sdcard/Movies",
            MediaType.Image => AndroidEnvironment.GetExternalStoragePublicDirectory(
                AndroidEnvironment.DirectoryPictures)?.AbsolutePath ?? "/sdcard/Pictures",
            MediaType.Audio => AndroidEnvironment.GetExternalStoragePublicDirectory(
                AndroidEnvironment.DirectoryMusic)?.AbsolutePath ?? "/sdcard/Music",
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };

        if (!string.IsNullOrEmpty(subFolder))
        {
            basePath = Path.Combine(basePath, subFolder);
        }

        Directory.CreateDirectory(basePath);
        return basePath;
    }

    /// <summary>
    /// 统一的保存方法，自动根据 Android 版本选择正确的方式
    /// </summary>
    public static async Task<string?> SaveMediaFileAsync(
        string sourceFilePath,
        string fileName,
        string mimeType,
        MediaType mediaType,
        string? subFolder = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Android 10+ 使用 MediaStore
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            var displayName = Path.GetFileNameWithoutExtension(fileName);
            var uri = await SaveToMediaStoreAsync(
                sourceFilePath,
                displayName,
                mimeType,
                mediaType,
                subFolder,
                progress,
                cancellationToken);

            return uri?.ToString();
        }
        else
        {
            // Android 9 及以下使用直接文件路径
            var targetDir = GetLegacyMediaPath(mediaType, subFolder);
            var targetPath = Path.Combine(targetDir, fileName);

            using var source = System.IO.File.OpenRead(sourceFilePath);
            using var dest = System.IO.File.Create(targetPath);

            var fileInfo = new System.IO.FileInfo(sourceFilePath);
            var totalBytes = fileInfo.Length;
            var buffer = new byte[81920];
            long totalRead = 0;

            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dest.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;
                progress?.Report((double)totalRead / totalBytes);
            }

            // 通知媒体扫描器
            ScanMediaFile(targetPath);

            return targetPath;
        }
    }

    /// <summary>
    /// 通知系统媒体扫描器扫描新文件（用于旧版 Android）
    /// </summary>
    public static void ScanMediaFile(string filePath)
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(Intent.ActionMediaScannerScanFile);
        intent.SetData(Uri.FromFile(new Java.IO.File(filePath)));
        context.SendBroadcast(intent);
    }

    private static (Uri externalUri, string relativePath) GetMediaUriAndPath(MediaType mediaType, string? subFolder)
    {
        return mediaType switch
        {
            MediaType.Video => (
                MediaStore.Video.Media.ExternalContentUri!,
                string.IsNullOrEmpty(subFolder)
                    ? AndroidEnvironment.DirectoryMovies!
                    : $"{AndroidEnvironment.DirectoryMovies}/{subFolder}"
            ),
            MediaType.Image => (
                MediaStore.Images.Media.ExternalContentUri!,
                string.IsNullOrEmpty(subFolder)
                    ? AndroidEnvironment.DirectoryPictures!
                    : $"{AndroidEnvironment.DirectoryPictures}/{subFolder}"
            ),
            MediaType.Audio => (
                MediaStore.Audio.Media.ExternalContentUri!,
                string.IsNullOrEmpty(subFolder)
                    ? AndroidEnvironment.DirectoryMusic!
                    : $"{AndroidEnvironment.DirectoryMusic}/{subFolder}"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };
    }

    private static void CopyStream(System.IO.Stream input, System.IO.Stream output)
    {
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, bytesRead);
        }
        output.Flush();
    }
}
#endif
