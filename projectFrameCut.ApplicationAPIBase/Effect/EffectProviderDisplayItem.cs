using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    /// <summary>
    /// Static display configuration for an <see cref="IEffectProvider"/>, including thumbnail sources
    /// and localization keys. The actual localized strings are resolved at runtime by
    /// <see cref="EffectProviderDisplayDefaults.TaggedLocalizedStringResolver"/>.
    /// </summary>
    public struct EffectProviderDisplayItem
    {
        /// <summary>
        /// Localization key for the effect name, e.g. "DisplayName_Effect_Blur". Null means fall back to the type name.
        /// </summary>
        public string? LocalizedNameKey;

        /// <summary>
        /// Localization key for the effect description, e.g. "Description_Effect_Blur". Null means empty string.
        /// </summary>
        public string? LocalizedDescriptionKey;

        /// <summary>
        /// Static thumbnail image for the effect card.
        /// </summary>
        public ImageSource? Thumbnail;

        /// <summary>
        /// Video thumbnail (hover preview) for the effect card.
        /// </summary>
        public MediaSource? VideoThumbnail;

        /// <summary>
        /// Optional per-field display items. Default is null (lazy resolution via key convention).
        /// </summary>
        public IReadOnlyDictionary<string, EffectProviderFieldDisplayItem>? Fields;
    }

    /// <summary>
    /// Per-field display configuration: localization keys for name and description.
    /// </summary>
    public struct EffectProviderFieldDisplayItem
    {
        /// <summary>
        /// Localization key for the field name. Default convention: "_{fieldId}".
        /// </summary>
        public string? LocalizedNameKey;

        /// <summary>
        /// Localization key for the field description/tooltip. Default convention: "Description_Field_{TypeName}_{fieldId}".
        /// </summary>
        public string? LocalizedDescriptionKey;
    }

    /// <summary>
    /// Static helpers and DI hooks for building <see cref="EffectProviderDisplayItem"/> instances
    /// and resolving localized strings at runtime.
    /// </summary>
    internal static class EffectProviderDisplayDefaults
    {
        /// <summary>
        /// Resolves app-package file paths. Registered by the App layer (e.g. via <see cref="Services.FileSystemService.GetAppPackageFileSync"/>).
        /// Signature: (string[] pathSegments) => absolute_file_path
        /// </summary>
        public static Func<string[], string>? AppPackageFileResolver;

        /// <summary>
        /// Lists file names in an app-package directory. Registered by the App layer.
        /// </summary>
        public static Func<string[], IEnumerable<string>>? AppPackageFileNamesResolver;

        /// <summary>
        /// Resolves a localized string by key. Registered by the App layer.
        /// Signature: (key, fallback, locate) => localized_string
        /// </summary>
        public static Func<string, string, string, string>? TaggedLocalizedStringResolver;

        /// <summary>
        /// Finds images and videos in EffectSample with the same name as the effect type, ignoring case.
        /// PNG and MP4 take priority; missing images fall back to source.png.
        /// </summary>
        public static (string imageFile, string? videoFile) ResolveThumbnailFiles(string typeName)
        {
            string? imageFile = null, videoFile = null;
            try
            {
                foreach (var file in (AppPackageFileNamesResolver?.Invoke(["EffectSample"]) ?? []).Order(StringComparer.Ordinal))
                {
                    if (!string.Equals(Path.GetFileNameWithoutExtension(file), typeName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    switch (Path.GetExtension(file).ToLowerInvariant())
                    {
                        case ".png":
                            imageFile = file;
                            break;
                        case ".jpg":
                        case ".jpeg":
                        case ".webp":
                        case ".gif":
                        case ".bmp":
                            imageFile ??= file;
                            break;
                        case ".mp4":
                            videoFile = file;
                            break;
                        case ".m4v":
                        case ".mov":
                        case ".webm":
                        case ".avi":
                        case ".mkv":
                            videoFile ??= file;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex, "scan effect sample thumbnails");
            }
            return (imageFile ?? "source.png", videoFile);
        }

        /// <summary>
        /// Resolves a localized string using the registered <see cref="TaggedLocalizedStringResolver"/>.
        /// If the key is null/empty or no resolver is registered, returns <paramref name="fallback"/>.
        /// </summary>
        public static string ResolveLocalized(string? key, string fallback, string locate)
        {
            if (string.IsNullOrEmpty(key) || TaggedLocalizedStringResolver is null)
                return fallback;
            return TaggedLocalizedStringResolver(key, fallback, locate);
        }

        /// <summary>
        /// Builds a default <see cref="EffectProviderDisplayItem"/> for the given provider using key conventions
        /// and matching sample files.
        /// </summary>
        public static EffectProviderDisplayItem BuildDefault(IEffectProvider source)
        {
            var (imageFile, videoFile) = ResolveThumbnailFiles(source.TypeName);

            string? ResolveSamplePath(string? file)
            {
                if (file is null) return null;
                try
                {
                    var path = AppPackageFileResolver?.Invoke(["EffectSample", file]) ?? file;
                    return File.Exists(path) ? path : null;
                }
                catch (Exception ex)
                {
                    Log(ex, $"resolve effect sample '{file}'");
                    return null;
                }
            }

            var imagePath = ResolveSamplePath(imageFile);
            if (imagePath is null && imageFile != "source.png")
                imagePath = ResolveSamplePath("source.png");
            var videoPath = ResolveSamplePath(videoFile);

            return new EffectProviderDisplayItem
            {
                LocalizedNameKey = "DisplayName_Effect_" + source.TypeName,
                LocalizedDescriptionKey = "Description_Effect_" + source.TypeName,
                Thumbnail = imagePath is not null ? ImageSource.FromFile(imagePath) : null,
                VideoThumbnail = videoPath is not null ? MediaSource.FromFile(videoPath) : null,
                Fields = null,
            };
        }
    }
}
