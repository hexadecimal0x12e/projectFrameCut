using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
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
        return $"{stage}: {message}";
    }

    public static IPicture CreateFallbackFrame(int width, int height, IPicture.PicturePixelMode pixelMode)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        const int tileSize = 48;
        int length = checked(width * height);

        if ((int)pixelMode == 16)
        {
            var r = new ushort[length];
            var g = new ushort[length];
            var b = new ushort[length];
            var a = new float[length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    bool purple = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                    r[index] = purple ? (ushort)0xA000 : (ushort)0;
                    b[index] = purple ? ushort.MaxValue : (ushort)0;
                    a[index] = 1f;
                }
            }

            return new Picture16bpp(width, height)
            {
                r = r,
                g = g,
                b = b,
                a = a,
                HasAlphaChannel = true
            };
        }

        var r8 = new byte[length];
        var g8 = new byte[length];
        var b8 = new byte[length];
        var a8 = new float[length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                bool purple = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                r8[index] = purple ? (byte)160 : (byte)0;
                b8[index] = purple ? byte.MaxValue : (byte)0;
                a8[index] = 1f;
            }
        }

        return new Picture8bpp(width, height)
        {
            r = r8,
            g = g8,
            b = b8,
            a = a8,
            HasAlphaChannel = true
        };
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
