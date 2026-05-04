#if ANDROID
using Android.Content;
using Android.Database;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace projectFrameCut.Platforms.Android;

/// <summary>
/// 从 Android 媒体库的指定相簿（文件夹）查询图片和视频，并提供按 URI 读取能力。
/// </summary>
public static class MediaStoreAlbumHelper
{
    private const string ColumnId = "_id";
    private const string ColumnDisplayName = "_display_name";
    private const string ColumnMimeType = "mime_type";
    private const string ColumnBucketDisplayName = "bucket_display_name";
    private const string ColumnRelativePath = "relative_path";
    private const string ColumnDateAdded = "date_added";
    private const string ColumnSize = "_size";
    private const string ColumnMediaType = "media_type";
    private const string ColumnDuration = "duration";
    private const int MediaTypeImage = 1;
    private const int MediaTypeVideo = 3;

    public enum AlbumMediaKind
    {
        Image,
        Video
    }

    public sealed record AlbumMediaItem(
        long MediaId,
        AlbumMediaKind Kind,
        Uri ContentUri,
        string DisplayName,
        string MimeType,
        string BucketDisplayName,
        string RelativePath,
        DateTimeOffset DateAdded,
        long SizeBytes,
        long? DurationMilliseconds);

    /// <summary>
    /// 枚举指定相簿（文件夹）中的图片和视频。
    /// albumIdentifier 同时匹配 BUCKET_DISPLAY_NAME（精确）和 RELATIVE_PATH（包含）。
    /// </summary>
    public static IReadOnlyList<AlbumMediaItem> EnumerateAlbumMedia(
        string albumIdentifier,
        bool includeImages = true,
        bool includeVideos = true)
    {
        if (string.IsNullOrWhiteSpace(albumIdentifier))
            throw new ArgumentException("Album identifier cannot be null or empty.", nameof(albumIdentifier));
        if (!includeImages && !includeVideos)
            return Array.Empty<AlbumMediaItem>();

        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver ?? throw new InvalidOperationException("ContentResolver is not available.");

        var collection = MediaStore.Files.GetContentUri("external");
        var projection = new[]
        {
            ColumnId,
            ColumnDisplayName,
            ColumnMimeType,
            ColumnBucketDisplayName,
            ColumnRelativePath,
            ColumnDateAdded,
            ColumnSize,
            ColumnMediaType,
            ColumnDuration
        };

        var mediaTypeParts = new List<string>(2);
        var selectionArgs = new List<string>(4);

        if (includeImages)
        {
            mediaTypeParts.Add($"{ColumnMediaType} = ?");
            selectionArgs.Add(MediaTypeImage.ToString());
        }

        if (includeVideos)
        {
            mediaTypeParts.Add($"{ColumnMediaType} = ?");
            selectionArgs.Add(MediaTypeVideo.ToString());
        }

        var normalizedPathKey = NormalizeAsRelativePathKey(albumIdentifier);
        var selection =
            $"({string.Join(" OR ", mediaTypeParts)}) AND (" +
            $"{ColumnBucketDisplayName} = ? OR " +
            $"{ColumnRelativePath} LIKE ?)";

        selectionArgs.Add(albumIdentifier);
        selectionArgs.Add($"%{normalizedPathKey}%");

        using var cursor = resolver.Query(
            collection,
            projection,
            selection,
            selectionArgs.ToArray(),
            $"{ColumnDateAdded} DESC");

        if (cursor == null || cursor.Count <= 0)
            return Array.Empty<AlbumMediaItem>();

        var idIndex = cursor.GetColumnIndex(ColumnId);
        var displayNameIndex = cursor.GetColumnIndex(ColumnDisplayName);
        var mimeTypeIndex = cursor.GetColumnIndex(ColumnMimeType);
        var bucketNameIndex = cursor.GetColumnIndex(ColumnBucketDisplayName);
        var relativePathIndex = cursor.GetColumnIndex(ColumnRelativePath);
        var dateAddedIndex = cursor.GetColumnIndex(ColumnDateAdded);
        var sizeIndex = cursor.GetColumnIndex(ColumnSize);
        var mediaTypeIndex = cursor.GetColumnIndex(ColumnMediaType);
        var durationIndex = cursor.GetColumnIndex(ColumnDuration);

        var items = new List<AlbumMediaItem>(cursor.Count);
        while (cursor.MoveToNext())
        {
            var mediaId = ReadLong(cursor, idIndex);
            var mediaType = ReadInt(cursor, mediaTypeIndex);
            var kind = mediaType == MediaTypeVideo
                ? AlbumMediaKind.Video
                : AlbumMediaKind.Image;

            var contentUri = kind == AlbumMediaKind.Video
                ? ContentUris.WithAppendedId(MediaStore.Video.Media.ExternalContentUri!, mediaId)
                : ContentUris.WithAppendedId(MediaStore.Images.Media.ExternalContentUri!, mediaId);

            var dateAdded = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, ReadLong(cursor, dateAddedIndex)));
            var sizeBytes = Math.Max(0, ReadLong(cursor, sizeIndex));
            long? duration = kind == AlbumMediaKind.Video
                ? Math.Max(0, ReadLong(cursor, durationIndex))
                : null;

            items.Add(new AlbumMediaItem(
                MediaId: mediaId,
                Kind: kind,
                ContentUri: contentUri,
                DisplayName: ReadString(cursor, displayNameIndex),
                MimeType: ReadString(cursor, mimeTypeIndex),
                BucketDisplayName: ReadString(cursor, bucketNameIndex),
                RelativePath: ReadString(cursor, relativePathIndex),
                DateAdded: dateAdded,
                SizeBytes: sizeBytes,
                DurationMilliseconds: duration));
        }

        return items;
    }

    /// <summary>
    /// 通过媒体 URI 打开只读流。
    /// </summary>
    public static Stream OpenRead(Uri contentUri)
    {
        ArgumentNullException.ThrowIfNull(contentUri);
        var context = global::Android.App.Application.Context;
        var resolver = context.ContentResolver ?? throw new InvalidOperationException("ContentResolver is not available.");
        return resolver.OpenInputStream(contentUri)
            ?? throw new IOException($"Cannot open stream for uri: {contentUri}");
    }

    /// <summary>
    /// 通过媒体 URI 读取所有内容。
    /// </summary>
    public static async Task<byte[]> ReadAllBytesAsync(Uri contentUri, CancellationToken cancellationToken = default)
    {
        using var input = OpenRead(contentUri);
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, 81920, cancellationToken);
        return memory.ToArray();
    }

    private static string NormalizeAsRelativePathKey(string pathLike)
    {
        var normalized = pathLike.Replace('\\', '/').Trim().Trim('/');
        return normalized + "/";
    }

    private static string ReadString(ICursor cursor, int index)
    {
        if (index < 0 || cursor.IsNull(index))
            return string.Empty;
        return cursor.GetString(index) ?? string.Empty;
    }

    private static long ReadLong(ICursor cursor, int index)
    {
        if (index < 0 || cursor.IsNull(index))
            return 0L;
        return cursor.GetLong(index);
    }

    private static int ReadInt(ICursor cursor, int index)
    {
        if (index < 0 || cursor.IsNull(index))
            return 0;
        return cursor.GetInt(index);
    }
}
#endif
