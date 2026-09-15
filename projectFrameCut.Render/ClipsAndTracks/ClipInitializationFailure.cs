using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Drawing.Processing.Composing;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;

namespace projectFrameCut.Render.ClipsAndTracks;

/// <summary>
/// Stores recoverable clip initialization failures in ExtraData and creates the
/// deliberately conspicuous frame used while a clip cannot be initialized.
/// </summary>
public static class ClipInitializationFailure
{
    public const string FailedKey = "__ClipInitializationFailed";
    public const string StageKey = "__ClipInitializationFailureStage";
    public const string MessageKey = "__ClipInitializationFailureMessage";
    public const string FailedEffectsKey = "__ClipInitializationFailedEffects";
    public const string FailedEffectProvidersKey = "__ClipInitializationFailedEffectProviders";

    private const float IconToCanvasScale = 0.32f;    // 图标边长占画布较短边的比例
    private const float IconVerticalAnchor = 0.40f;   // 图标中心在画布中的垂直位置（略偏上，为下方错误文本留出空间）
    private const float IconMaxAspectRatio = 2f;      // 图标宽高比钳制上限，避免图标过长贴到文本
    private const float TextFontSizeScale = 0.034f;   // 错误文本字号占画布较短边的比例
    private const float TextVerticalAnchor = 0.62f;   // 错误文本顶部的垂直位置
    private const float TextMaxWidthScale = 0.82f;    // 错误文本最大换行宽度占画布宽度的比例

    /// <summary>
    /// 图标资源缓存：资源名 → 原始（未缩放）像素图。
    /// 资源按需懒加载并常驻内存（图标尺寸小、数量少，可接受）；加载失败缓存 null 以免重复尝试。
    /// </summary>
    private static readonly ConcurrentDictionary<string, IPicture?> IconCache = new();

    /// <summary>
    /// Stage → 图标资源名。目前所有 Stage 均使用 ErrorSign；
    /// 后续可按 Stage 注册不同图标（见 <see cref="RegisterStageIcon"/>）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> StageIconResourceMap = new(StringComparer.OrdinalIgnoreCase);

    static ClipInitializationFailure()
    {
        StageIconResourceMap["Initialization"] = "projectFrameCut.Render.Resource.Error.png";
        StageIconResourceMap["SourceReading"] = "projectFrameCut.Render.Resource.Error_SourceFail.png";
        StageIconResourceMap["SourceNotFound"] = "projectFrameCut.Render.Resource.Error_SourceNotFound.png";
        StageIconResourceMap["SourceNotMatch"] = "projectFrameCut.Render.Resource.Error_SourceNotFound.png";
        StageIconResourceMap["ResolveBinding"] = "projectFrameCut.Render.Resource.Error_BindingFail.png";
        StageIconResourceMap["ResolveEffect"] = "projectFrameCut.Render.Resource.Error.png";
    }

    /// <summary>从失败标记中读取失败阶段；未标记或缺失时返回 null。</summary>
    public static string? GetStage(Dictionary<string, object>? extraData)
    {
        if (extraData is null) return null;
        var stage = ReadString(extraData, StageKey, string.Empty);
        return string.IsNullOrWhiteSpace(stage) ? null : stage;
    }

    public static void Mark(IClip clip, string stage, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(clip);
        clip.ExtraData ??= new Dictionary<string, object>();
        Mark(clip.ExtraData, stage, exception);
        clip.EffectsInstances = [];
        clip.SpeedVarianceProviderInstance = null;
        clip.MixtureInstance = null;
        clip.AlternativeSource = null;
    }

    public static void Mark(Dictionary<string, object> extraData, string stage, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(extraData);
        ArgumentNullException.ThrowIfNull(exception);
        extraData[FailedKey] = true;
        extraData[StageKey] = string.IsNullOrWhiteSpace(stage) ? "Initialization" : stage;
        extraData[MessageKey] = GetUsefulMessage(exception);
    }

    public static void Clear(IClip clip)
    {
        Clear(clip.ExtraData);
    }

    public static void Clear(Dictionary<string, object>? extraData)
    {
        if (extraData is null) return;
        extraData.Remove(FailedKey);
        extraData.Remove(StageKey);
        extraData.Remove(MessageKey);
    }

    public static bool IsMarked(IClip clip) => IsMarked(clip.ExtraData);

    public static bool HasDeferredFailures(Dictionary<string, object>? extraData) =>
        extraData is not null && (extraData.ContainsKey(FailedEffectsKey) || extraData.ContainsKey(FailedEffectProvidersKey));

    public static bool IsMarked(Dictionary<string, object>? extraData)
    {
        if (extraData is null || !extraData.TryGetValue(FailedKey, out var raw)) return false;
        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement element when element.ValueKind == JsonValueKind.String => bool.TryParse(element.GetString(), out var value) && value,
            _ => bool.TryParse(raw?.ToString(), out var value) && value
        };
    }

    public static string GetDescription(Dictionary<string, object>? extraData)
    {
        if (!IsMarked(extraData)) return string.Empty;
        var stage = ReadString(extraData, StageKey, "Initialization");
        var message = ReadString(extraData, MessageKey, "Unknown error");
        var localizedHeader = "A error happens while reading source.";
        if(LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources is ISimpleLocalizerBase_PropertyPanel pp)
        {
            localizedHeader = stage switch
            {
                "Initialization" => pp.ClipFallback_ClipInit,
                "SourceReading" => pp.ClipFallback_Source,
                "SourceNotFound" => pp.ClipFallback_SourceNotFound,
                "SourceNotMatch" => pp.ClipFallback_SourceNotMatch,
                "ResolveBinding" => pp.ClipFallback_ResolveBinding,
                "ResolveEffect" => pp.ClipFallback_ResolveEffect,
                _ => pp.ClipFallback_NotSpecified(stage)
            };
        }
        else
        {
            localizedHeader = stage switch
            {
                "Initialization" => "Clip initialization failed.",
                "SourceReading" => "Source reading failed.",
                "SourceNotFound" => "Source not found.",
                "SourceNotMatch" => "Source not match.",
                "ResolveBinding" => "Resolve binding failed.",
                "ResolveEffect" => "Resolve effect failed.",
                _ => $"Stage '{stage}' failed."
            };
        }
        return $"{localizedHeader}{Environment.NewLine}{message}";
    }

    /// <summary>
    /// 创建失败回退帧：暗红纯色背景 + 居中偏上的 Stage 图标 + 下方错误描述文本。
    /// <paramref name="stage"/> 用于选择对应图标（null 时使用默认 ErrorSign）；
    /// <paramref name="description"/> 为显示在图标下方的错误文本（null/空则只画图标）。
    /// </summary>
    public static IPicture CreateFallbackFrame(
        int width,
        int height,
        IPicture.PicturePixelMode pixelMode,
        string? stage = null,
        string? description = null)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        // 暗红背景（RGB 58,22,22），与红色系图标、白字黑描边文字协调且不刺眼
        IPicture picture = (int)pixelMode switch
        {
            8 => Picture8bpp.GenerateSolidColor(width, height, 58, 22, 22, 1),
            16 => Picture16bpp.GenerateSolidColor(width, height, 14906, 5654, 5654, 1),
            _ => throw new InvalidDataException("Unsupport pixel mode.")
        };

        // 叠加 Stage 对应的错误图标（渲染时读取实际尺寸、按需缩放）
        var iconStage = string.IsNullOrWhiteSpace(stage) ? "Initialization" : stage;
        if (TryGetStageIcon(iconStage, out var icon) && icon is not null)
        {
            var withIcon = OverlayIcon(picture, icon, width, height, pixelMode);
            if (withIcon is not null)
            {
                if (!ReferenceEquals(picture, withIcon)) picture.Dispose(force: true);
                picture = withIcon;
            }
        }

        // 叠加下方错误描述文本
        if (!string.IsNullOrWhiteSpace(description))
        {
            var withText = OverlayText(picture, description, width, height, pixelMode);
            if (withText is not null)
            {
                if (!ReferenceEquals(picture, withText)) picture.Dispose(force: true);
                picture = withText;
            }
        }

        return picture;
    }

    /// <summary>从失败标记 <paramref name="extraData"/> 中读取 Stage 与描述并创建回退帧。</summary>
    public static IPicture CreateFallbackFrame(int width, int height, IPicture.PicturePixelMode pixelMode, Dictionary<string, object>? extraData)
        => CreateFallbackFrame(width, height, pixelMode, GetStage(extraData), GetDescription(extraData));


    /// <summary>在背景上按画布比例缩放图标并叠加（保持宽高比）。</summary>
    private static IPicture? OverlayIcon(IPicture basePicture, IPicture icon, int targetWidth, int targetHeight, IPicture.PicturePixelMode targetPPB)
    {
        IPicture? resized = null;
        try
        {
            int canvasMin = Math.Min(targetWidth, targetHeight);
            int iconSide = (int)MathF.Max(1f, canvasMin * IconToCanvasScale);
            float iconW = icon.Width;
            float iconH = icon.Height;
            if (iconW <= 0f || iconH <= 0f) return null;

            // 以较短边为基准做等比缩放；宽高比异常（过扁/过窄）的图标再钳制比例
            float aspect = iconW / iconH;
            if (aspect > IconMaxAspectRatio)
            {
                iconW = iconSide * IconMaxAspectRatio;
                iconH = iconSide;
            }
            else if (aspect < 1f / IconMaxAspectRatio)
            {
                iconW = iconSide;
                iconH = iconSide / IconMaxAspectRatio;
            }
            else
            {
                iconW = iconSide;
                iconH = iconSide;
            }

            int scaledW = Math.Max(1, (int)MathF.Round(iconW));
            int scaledH = Math.Max(1, (int)MathF.Round(iconH));
            resized = icon.Resize(scaledW, scaledH, preserveAspect: false);

            int startX = (targetWidth - scaledW) / 2;
            int startY = (int)(targetHeight * IconVerticalAnchor) - scaledH / 2;

            return ComposeOverlay(basePicture, resized, startX, startY, targetWidth, targetHeight, targetPPB);
        }
        catch
        {
            return null;
        }
        finally
        {
            // Resizer 在尺寸不变时返回源对象（缓存图标），不能释放
            if (resized is not null && !ReferenceEquals(resized, icon))
                resized.Dispose(force: true);
        }
    }

    /// <summary>将错误描述文本布局到画布下半部并叠加。</summary>
    private static IPicture? OverlayText(IPicture basePicture, string description, int targetWidth, int targetHeight, IPicture.PicturePixelMode targetPPB)
    {
        IPicture? textLayer = null;
        try
        {
            int canvasMin = Math.Min(targetWidth, targetHeight);
            float fontSize = MathF.Max(8f, canvasMin * TextFontSizeScale);
            float maxTextWidth = MathF.Max(64f, targetWidth * TextMaxWidthScale);

            TextClipFontRegistry.Initialize();

            var entry = new TextEntry
            {
                Text = description,
                FontName = "HarmonyOS Sans SC Medium",
                FontStyle = "Regular",
                FontSize = fontSize,
                X = targetWidth * 0.5f,        // 水平居中
                Y = targetHeight * TextVerticalAnchor,
                Alignment = TextAlignment.Center,
                FillR = ushort.MaxValue,
                FillG = ushort.MaxValue,
                FillB = ushort.MaxValue,
                FillA = 1f,
                StrokeR = 0,
                StrokeG = 0,
                StrokeB = 0,
                StrokeA = 1f,
                StrokeThickness = MathF.Max(1f, fontSize * 0.08f),
                CharacterSpacing = 0f,
                WordSpacing = 0f,
                LineSpacing = 0.2f,
            };
            entry.SetWrappingWidth(maxTextWidth);

            var ctx = TextLayoutContext.FromCanvas(targetWidth, targetHeight);
            var vectorCanvas = TextLayoutPipeline.LayoutForRender([entry], ctx, targetWidth, targetHeight);
            if (vectorCanvas.Elements.Count == 0)
                return null;   // 字体不可用：静默跳过文本层

            textLayer = IVectorContentClip.GlobalDefaultRasterizer.Convert(
                vectorCanvas, targetWidth, targetHeight, transparentBackground: true,
                aaMode: IVectorContentClip.GlobalDefaultAntiAliasMode);

            return ComposeOverlay(basePicture, textLayer, 0, 0, targetWidth, targetHeight, targetPPB);
        }
        catch
        {
            return null;
        }
        finally
        {
            textLayer?.Dispose(force: true);
        }
    }

    /// <summary>
    /// 统一位深后把 <paramref name="topLayer"/> 以 Overlay（标准 alpha 混合）叠加到
    /// <paramref name="basePicture"/> 上，返回指定位深的合成结果。调用方负责释放 basePicture 和 topLayer。
    /// </summary>
    private static IPicture? ComposeOverlay(
        IPicture basePicture,
        IPicture topLayer,
        int startX,
        int startY,
        int targetWidth,
        int targetHeight,
        IPicture.PicturePixelMode targetPPB)
    {
        IPicture baseForCompose = basePicture;
        IPicture topForCompose = topLayer;
        if (basePicture.BitPerPixel != topLayer.BitPerPixel)
        {
            var mode = basePicture.BitPerPixel.Value > topLayer.BitPerPixel.Value
                ? basePicture.BitPerPixel
                : topLayer.BitPerPixel;
            if (basePicture.BitPerPixel != mode) baseForCompose = basePicture.ToBitPerPixel(mode);
            if (topLayer.BitPerPixel != mode) topForCompose = topLayer.ToBitPerPixel(mode);
        }

        IPicture? result = null;
        try
        {
            if (baseForCompose is IPicture<ushort> uBase && topForCompose is IPicture<ushort> uTop)
                result = PictureComposer.Default.Compose(uBase, uTop, BlendMode.Overlay, startX, startY, targetWidth, targetHeight);
            else if (baseForCompose is IPicture<byte> bBase && topForCompose is IPicture<byte> bTop)
                result = PictureComposer.Default.Compose(bBase, bTop, BlendMode.Overlay, startX, startY, targetWidth, targetHeight);

            if (result is null) return null;
            if (result.BitPerPixel != targetPPB)
            {
                var converted = result.ToBitPerPixel(targetPPB);
                result.Dispose(force: true);
                return converted;
            }
            return result;
        }
        finally
        {
            // 清理位深转换产生的临时对象（转换在尺寸不变时可能返回原对象，必须用引用比较防误释放）
            if (!ReferenceEquals(baseForCompose, basePicture)) baseForCompose.Dispose(force: true);
            if (!ReferenceEquals(topForCompose, topLayer)) topForCompose.Dispose(force: true);
        }
    }

    /// <summary>读取当前失败 Stage 对应的图标（从嵌入资源解码，缓存原始图）。</summary>
    private static bool TryGetStageIcon(string stage, out IPicture? icon)
    {
        var resourceName = StageIconResourceMap.TryGetValue(stage, out var mapped)
            ? mapped
            : "projectFrameCut.Render.Resource.Error.png";
        icon = IconCache.GetOrAdd(resourceName, LoadIcon);
        return icon is not null;
    }

    private static IPicture? LoadIcon(string resourceName)
    {
        try
        {
            var assembly = typeof(ClipInitializationFailure).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            if (!PictureExtensions.SharedPngPictureDecoder.TryLoad(stream, out IPicture? picture) || picture is null)
                return null;
            picture.CanBeDisposed = false;   // 缓存共享，禁止调用方意外释放
            return picture;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(Dictionary<string, object> data, string key, string fallback)
    {
        if (!data.TryGetValue(key, out var raw) || raw is null) return fallback;
        if (raw is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : element.ToString();
        }
        return raw.ToString() ?? fallback;
    }

    private static string GetUsefulMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null) current = current.InnerException;
        return string.IsNullOrWhiteSpace(current.Message) ? current.GetType().Name : current.Message;
    }
}
