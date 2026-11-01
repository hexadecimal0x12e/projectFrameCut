using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Text;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview
{
    private const string TextStyleProviderFromKey = "TextStyleProvider_FromPlugin";
    private const string TextStyleProviderTypeKey = "TextStyleProvider_TypeName";

    private int _viewportWidth;
    private int _viewportHeight;

    /// <summary>
    /// Resolves the bitmap canvas from the target resolution and optional viewport fitting.
    /// The returned dimensions deliberately exclude any letterbox/pillarbox area.
    /// </summary>
    public (int canvasWidth, int canvasHeight) ResolveDimensions(int projectWidth, int projectHeight, double CanvasWidth, double CanvasHeight)
    {
        projectWidth = Math.Max(1, projectWidth);
        projectHeight = Math.Max(1, projectHeight);

        var viewportWidth = IgnorePreviewWindowSize ? projectWidth : ResolveCanvasSize(CanvasWidth, _viewportWidth, projectWidth);
        var viewportHeight = IgnorePreviewWindowSize ? projectHeight : ResolveCanvasSize(CanvasHeight, _viewportHeight, projectHeight);
        var fittedRect = CalculateAspectFitRect(viewportWidth, viewportHeight, projectWidth, projectHeight);
        var targetPixels = TargetResolutionPixels;
        var scale = IgnorePreviewWindowSize && targetPixels > 0
            ? Math.Sqrt(targetPixels / ((double)projectWidth * projectHeight))
            : Math.Min(fittedRect.Width / projectWidth, fittedRect.Height / projectHeight);

        var canvasWidth = Math.Max(1, (int)Math.Round(projectWidth * scale, MidpointRounding.AwayFromZero));
        var canvasHeight = Math.Max(1, (int)Math.Round(projectHeight * scale, MidpointRounding.AwayFromZero));

        return (canvasWidth, canvasHeight);
    }

    private (int projectWidth, int projectHeight) ResolveProjectDimensions(int fallbackWidth, int fallbackHeight)
    {
        var projectWidth = _previewer?.ProjectRelativeWidth > 0
            ? _previewer.ProjectRelativeWidth
            : fallbackWidth;
        var projectHeight = _previewer?.ProjectRelativeHeight > 0
            ? _previewer.ProjectRelativeHeight
            : fallbackHeight;

        return (Math.Max(1, projectWidth), Math.Max(1, projectHeight));
    }

    public void UpdateCanvasSize(double width, double height)
    {
        if (width > 0)
        {
            _viewportWidth = Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero));
        }

        if (height > 0)
        {
            _viewportHeight = Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero));
        }
    }

    private static int ResolveCanvasSize(double canvasSize, int cachedSize, int fallbackSize)
    {
        if (canvasSize > 0)
        {
            return Math.Max(1, (int)Math.Round(canvasSize, MidpointRounding.AwayFromZero));
        }

        if (cachedSize > 0)
        {
            return cachedSize;
        }

        return Math.Max(1, fallbackSize);
    }

    private static Rect CalculateAspectFitRect(int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return new Rect(0, 0, Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));
        }

        double ratioViewport = (double)viewportWidth / viewportHeight;
        double ratioTarget = (double)targetWidth / targetHeight;
        double drawW;
        double drawH;
        double offX;
        double offY;

        if (ratioTarget > ratioViewport)
        {
            drawW = viewportWidth;
            drawH = drawW / ratioTarget;
            offX = 0;
            offY = (viewportHeight - drawH) / 2d;
        }
        else
        {
            drawH = viewportHeight;
            drawW = drawH * ratioTarget;
            offX = (viewportWidth - drawW) / 2d;
            offY = 0;
        }

        return new Rect(offX, offY, drawW, drawH);
    }

    private static int ResolveClipPreviewDimension(int clipDimension, int projectDimension, int canvasDimension)
    {
        if (clipDimension <= 0 || projectDimension <= 0)
        {
            return Math.Max(1, canvasDimension);
        }

        return Math.Max(1, (int)Math.Round((double)clipDimension * canvasDimension / projectDimension, MidpointRounding.AwayFromZero));
    }

    private static bool IsAiGeneratedClip(IClip clip)
    {
        if (clip.ExtraData is null || !clip.ExtraData.TryGetValue("IsAI", out var raw))
        {
            return false;
        }

        return raw switch
        {
            bool value => value,
            string value when bool.TryParse(value, out var parsed) => parsed,
            JsonElement value => value.ValueKind == JsonValueKind.True,
            _ => false,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public IReadOnlyList<PreviewRequest> ResolveRequests(IReadOnlyList<IClip>? clips, uint frameIndex, int projectWidth = 0, int projectHeight = 0)
    {
        if (clips is null || clips.Count == 0)
        {
            return [];
        }

        var visibleClips = new List<IClip>(clips.Count);
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (clip.ClipType == ClipMode.AudioClip || clip.ClipType == ClipMode.MarkingClip)
            {
                continue;
            }

            if (!clip.ContainsFrame(frameIndex))
            {
                continue;
            }

            visibleClips.Add(clip);
        }

        if (visibleClips.Count == 0)
        {
            return [];
        }

        visibleClips.Sort(ClipLayerOrder.BottomToTop);
        var requests = new PreviewRequest[visibleClips.Count];
        for (var i = 0; i < visibleClips.Count; i++)
        {
            var clip = visibleClips[i];
            if (clip is TextClip textClip)
            {
                var styleProvider = ResolveTextClipStyleProvider(clip);
                if (styleProvider is not null)
                {
                    styleProvider = TextClipStyles.Restore(clip.ExtraData) ?? styleProvider;
                    clip.ExtraData ??= new Dictionary<string, object>(StringComparer.Ordinal);
                    var resolvedEntries = TextServices.ResolveEntries(textClip);
                    if (resolvedEntries.Count == 0)
                    {
                        var entries = styleProvider.BuildEntries();
                        if (entries.Length > 0)
                        {
                            var rebuiltEntries = new List<TextEntry>(entries);
                            clip.ExtraData["TextEntries"] = rebuiltEntries;
                            resolvedEntries = rebuiltEntries;
                        }
                    }

                    if (textClip.TargetWidth <= 0 && textClip.TargetHeight <= 0
                        && projectWidth > 0 && projectHeight > 0)
                    {
                        if (resolvedEntries.Count > 0)
                        {
                            var bounds = TextServices.MeasureBounds(textClip);
                            textClip.TargetX = (int)Math.Round(bounds.X);
                            textClip.TargetY = (int)Math.Round(bounds.Y);
                            textClip.TargetWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width));
                            textClip.TargetHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                            // Shift entries to clip-local space so the layout canvas
                            // (TargetWidth × TargetHeight) matches the coordinate system.
                            ShiftEntriesToClipLocal(resolvedEntries, textClip.TargetX, textClip.TargetY);
                        }
                        else
                        {
                            // Text bounds are persisted as Target* values, so they must stay in
                            // project-relative coordinates and must never depend on preview density.
                            var rect = styleProvider.GetViewRect(projectWidth, projectHeight);
                            if (!rect.IsDelta)
                            {
                                textClip.TargetX = rect.TargetX;
                                textClip.TargetY = rect.TargetY;
                                textClip.TargetWidth = Math.Max(1, rect.TargetWidth);
                                textClip.TargetHeight = Math.Max(1, rect.TargetHeight);
                            }
                        }
                    }
                }
                else if (textClip.TargetWidth <= 0 && textClip.TargetHeight <= 0)
                {
                    var bounds = TextServices.MeasureBounds(textClip);
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        textClip.TargetX = (int)bounds.X;
                        textClip.TargetY = (int)bounds.Y;
                        textClip.TargetWidth = (int)Math.Ceiling(bounds.Width);
                        textClip.TargetHeight = (int)Math.Ceiling(bounds.Height);
                        // Shift entries to clip-local space so the layout canvas
                        // (TargetWidth × TargetHeight) matches the coordinate system.
                        var rawEntries = TextServices.ResolveEntries(textClip);
                        if (rawEntries.Count > 0)
                            ShiftEntriesToClipLocal(rawEntries, textClip.TargetX, textClip.TargetY);
                    }
                }
            }

            requests[i] = new PreviewRequest(clip);
        }

        return requests;
    }


    private static void ShiftEntriesToClipLocal(IReadOnlyList<TextEntry> entries, int offsetX, int offsetY)
    {
        if (offsetX == 0 && offsetY == 0) return;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e is not null)
            {
                e.X -= offsetX;
                e.Y -= offsetY;
            }
        }
    }

    private static bool HasLegacyInternalPlaceResizeEffects(IClip clip)
    {
        if (clip.EffectsInstances?.Any() != true)
        {
            return false;
        }

        return clip.EffectsInstances.Any(IsLegacyInternalLayoutEffect);
    }

    private static bool IsLegacyInternalLayoutEffect(IEffect effect)
    {
        if (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
            || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(effect.FromPlugin, InternalPluginBase.InternalPluginBaseID, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }


    [DebuggerStepThrough()]
    private static ITextClipStyleProvider? ResolveTextClipStyleProvider(IClip clip)
    {
        var providerFrom = ReadExtraDataString(clip.ExtraData, TextStyleProviderFromKey);
        var providerType = ReadExtraDataString(clip.ExtraData, TextStyleProviderTypeKey);
        if (!string.IsNullOrWhiteSpace(providerFrom)
            && !string.IsNullOrWhiteSpace(providerType)
            && PluginManager.LoadedPlugins.TryGetValue(providerFrom, out var ownerPlugin)
            && ownerPlugin is { } appPlugin
            && appPlugin.TextClipStyleProvider.TryGetValue(providerType, out var factory))
        {
            return factory();
        }

        if (PluginManager.LoadedPlugins.TryGetValue(clip.FromPlugin, out var clipOwner)
            && clipOwner is { } clipPlugin)
        {
            return ResolveTextClipStyleProviderFromDictionary(clipPlugin.TextClipStyleProvider, clip);
        }

        return null;
    }

    private static ITextClipStyleProvider? ResolveTextClipStyleProviderFromDictionary(
        IReadOnlyDictionary<string, Func<ITextClipStyleProvider>> providers, IClip clip)
    {
        if (providers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(clip.TypeName)
            && providers.TryGetValue(clip.TypeName, out var typedProvider))
        {
            return typedProvider();
        }

        var clipModeName = clip.ClipType.ToString();
        if (providers.TryGetValue(clipModeName, out var modeProvider))
        {
            return modeProvider();
        }

        var fallback = providers.Values.FirstOrDefault();
        return fallback is null ? null : fallback();
    }

    private static string? ReadExtraDataString(Dictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is string s)
        {
            return s;
        }

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                return je.GetString();
            }
            return je.ToString();
        }

        return raw.ToString();
    }



    private static bool TryReadStringDictionary(Dictionary<string, object>? data, string key, out Dictionary<string, string> values)
    {
        values = null!;
        if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is Dictionary<string, string> stringDict)
        {
            values = new Dictionary<string, string>(stringDict);
            return true;
        }

        if (raw is Dictionary<string, object> objDict)
        {
            values = objDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);
            return true;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in je.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? (prop.Value.GetString() ?? string.Empty)
                    : prop.Value.ToString();
            }
            values = dict;
            return true;
        }

        return false;
    }



    private static IEffect[] GetEnabledEffectsSorted(IEffect[]? effects)
    {
        if (effects is null || effects.Length == 0)
        {
            return Array.Empty<IEffect>();
        }

        var enabledEffects = new List<IEffect>(effects.Length);
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            if (effect.Enabled)
            {
                enabledEffects.Add(effect);
            }
        }

        if (enabledEffects.Count <= 1)
        {
            return enabledEffects.ToArray();
        }

        enabledEffects.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return enabledEffects.ToArray();
    }

    public sealed record PreviewRequest(IClip Clip);
}
