using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;

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
        /// Resolves a localized string by key. Registered by the App layer.
        /// Signature: (key, fallback, locate) => localized_string
        /// </summary>
        public static Func<string, string, string, string>? TaggedLocalizedStringResolver;

        /// <summary>
        /// Maps effect type names to their sample thumbnail / video file names (relative to "EffectSample").
        /// ".mp4" files produce a video thumbnail; ".png" files produce an image thumbnail.
        /// Effects not listed here fall back to "source.png".
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> EffectSampleThumbnails = new Dictionary<string, string>
        {
            { "Blur", "blur.png" },
            { "ClassicOverlayMixture", "classicOverlayMixture.png" },
            { "FadeOpacity", "fadeOpacity.png" },
            { "Flip", "flip.png" },
            { "Jitter", "jittter.mp4" },
            { "ProgressCrop", "progresscrop.mp4" },
            { "ProgressPlacer", "progressplace.mp4" },
            { "RemoveColor", "removeColor.png" },
            { "Sharpen", "sharpen.png" },
            { "Vignette", "vignette.png" },
            { "ZoomIn", "zoomin.mp4" },
        };

        /// <summary>
        /// Resolves the thumbnail file names for a given effect type name.
        /// Returns (imageFile, videoFile) — one of them may be null.
        /// For ".mp4" entries, imageFile is "source.png" and videoFile is the mp4.
        /// For ".png" entries, imageFile is the png and videoFile is null.
        /// Fallback: ("source.png", null).
        /// </summary>
        public static (string? imageFile, string? videoFile) ResolveThumbnailFiles(string typeName)
        {
            if (EffectSampleThumbnails.TryGetValue(typeName, out var file))
            {
                if (file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    return ("source.png", file);
                return (file, null);
            }
            return ("source.png", null);
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
        /// and the thumbnail mapping table.
        /// </summary>
        public static EffectProviderDisplayItem BuildDefault(IEffectProvider source)
        {
            var (imageFile, videoFile) = ResolveThumbnailFiles(source.TypeName);

            ImageSource? ResolveImageSource(string? file)
            {
                if (file is null) return null;
                var absolutePath = AppPackageFileResolver?.Invoke(new[] { "EffectSample", file }) ?? file;
                return ImageSource.FromFile(absolutePath);
            }

            return new EffectProviderDisplayItem
            {
                LocalizedNameKey = "DisplayName_Effect_" + source.TypeName,
                LocalizedDescriptionKey = "Description_Effect_" + source.TypeName,
                Thumbnail = ResolveImageSource(imageFile),
                VideoThumbnail = videoFile is not null ? MediaSource.FromFile(AppPackageFileResolver?.Invoke(new[] { "EffectSample", videoFile }) ?? videoFile) : null,
                Fields = null,
            };
        }
    }
}