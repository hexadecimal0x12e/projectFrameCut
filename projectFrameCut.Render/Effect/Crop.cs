using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace projectFrameCut.Render.Effect
{
    public class CropEffect_IPicture : INormalEffect, IDynamicArgumentsEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }
        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX },
            {"StartY", StartY },
            {"Height", Height },
            {"Width", Width },
            {"Angle", Angle },
        };

        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
            "Angle"
        };

        public static List<string> OptionalParameters { get; } = new List<string>
        {
            "Angle",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
            {"Angle", "float" },
        };

        public string TypeName => "Crop";

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.Except(OptionalParameters).All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var unsupportedParameters = parameters.Keys.Except(ParametersNeeded).Except(OptionalParameters).ToList();
            if (unsupportedParameters.Count > 0)
            {
                throw new ArgumentException($"Unsupported parameters: {string.Join(", ", unsupportedParameters)}");
            }

            float angle = parameters.TryGetValue("Angle", out var angleVal) ? Convert.ToSingle(angleVal) : 0f;

            return new CropEffect_IPicture
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = angle,
                ImplementType = implementType,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int cropX = DynamicParam.Resolve(DynamicProviders, "StartX", StartX);
            int cropY = DynamicParam.Resolve(DynamicProviders, "StartY", StartY);
            int cropW = DynamicParam.Resolve(DynamicProviders, "Width", Width);
            int cropH = DynamicParam.Resolve(DynamicProviders, "Height", Height);
            float cropAngle = DynamicParam.Resolve(DynamicProviders, "Angle", Angle);
            int startX = cropX;
            int startY = cropY;
            int width = cropW;
            int height = cropH;

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)cropX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)cropY * targetHeight / RelativeHeight);
                width = (int)Math.Round((double)cropW * targetWidth / RelativeWidth);
                height = (int)Math.Round((double)cropH * targetHeight / RelativeHeight);
            }

            return CropEffectShared.CropAndProcess(source, startX, startY, width, height, cropAngle);
        }

        [Obsolete("Use Render directly instead.")]
        public IPicture Crop(IPicture source, int startX, int startY, int width, int height, int targetWidth, int targetHeight)
        {
            return CropEffectShared.CropAndProcess(source, startX, startY, width, height, Angle);
        }

        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }
    }

    public record struct CropData(double Index, int StartX, int StartY, int Width, int Height, float Angle = 0f);

    internal static class CropEffectShared
    {
        public static List<CropData> ParseCropList(object? value)
        {
            if (value is null)
            {
                return new List<CropData>();
            }

            if (value is List<CropData> list)
            {
                return new List<CropData>(list);
            }

            if (value is CropData[] array)
            {
                return new List<CropData>(array);
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var json = element.GetString();
                    return string.IsNullOrWhiteSpace(json)
                        ? new List<CropData>()
                        : JsonSerializer.Deserialize<List<CropData>>(json) ?? new List<CropData>();
                }

                return JsonSerializer.Deserialize<List<CropData>>(element.GetRawText()) ?? new List<CropData>();
            }

            if (value is string jsonString)
            {
                return string.IsNullOrWhiteSpace(jsonString)
                    ? new List<CropData>()
                    : JsonSerializer.Deserialize<List<CropData>>(jsonString) ?? new List<CropData>();
            }

            throw new ArgumentException("CropList parameter is invalid.", nameof(value));
        }

        public static CropData GetCropForProgress(IReadOnlyList<CropData> cropList, double progress)
        {
            if (cropList.Count == 0)
            {
                throw new ArgumentException("Crop list must not be empty.", nameof(cropList));
            }

            if (cropList.Count == 1)
            {
                return cropList[0];
            }

            if (progress <= cropList[0].Index)
            {
                return cropList[0];
            }

            int lastIndex = cropList.Count - 1;
            if (progress >= cropList[lastIndex].Index)
            {
                return cropList[lastIndex];
            }

            for (int i = 1; i < cropList.Count; i++)
            {
                var current = cropList[i];
                if (progress <= current.Index)
                {
                    var previous = cropList[i - 1];
                    double span = current.Index - previous.Index;
                    if (span <= 0)
                    {
                        return current;
                    }

                    double t = (progress - previous.Index) / span;
                    return Lerp(previous, current, t);
                }
            }

            return cropList[lastIndex];
        }

        public static CropData Lerp(CropData from, CropData to, double t)
        {
            int x = (int)Math.Round(from.StartX + (to.StartX - from.StartX) * t);
            int y = (int)Math.Round(from.StartY + (to.StartY - from.StartY) * t);
            int w = (int)Math.Round(from.Width + (to.Width - from.Width) * t);
            int h = (int)Math.Round(from.Height + (to.Height - from.Height) * t);
            float angle = (float)(from.Angle + (to.Angle - from.Angle) * t);
            return new CropData(from.Index + (to.Index - from.Index) * t, x, y, w, h, angle);
        }

        public static CropData Scale(CropData crop, int targetWidth, int targetHeight, int relativeWidth, int relativeHeight)
        {
            if (relativeWidth <= 0 || relativeHeight <= 0 || (relativeWidth == targetWidth && relativeHeight == targetHeight))
            {
                return crop;
            }

            int x = (int)Math.Round((double)crop.StartX * targetWidth / relativeWidth);
            int y = (int)Math.Round((double)crop.StartY * targetHeight / relativeHeight);
            int w = (int)Math.Round((double)crop.Width * targetWidth / relativeWidth);
            int h = (int)Math.Round((double)crop.Height * targetHeight / relativeHeight);
            return new CropData(crop.Index, x, y, w, h, crop.Angle);
        }

        public static (int X, int Y, int Width, int Height) BuildSafeCropRect(int startX, int startY, int width, int height, int sourceWidth, int sourceHeight)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            int maxX = Math.Max(0, startX);
            int maxY = Math.Max(0, startY);
            int maxW = Math.Min(width, sourceWidth - maxX);
            int maxH = Math.Min(height, sourceHeight - maxY);

            if (maxW <= 0 || maxH <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startX), "Crop rectangle must overlap source bounds.");
            }

            return (maxX, maxY, maxW, maxH);
        }

        public static IPicture CropAndProcess(IPicture source, int startX, int startY, int width, int height, float angle)
        {
            // Validate inputs early
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Width and Height must be positive.");

            // When the crop rect falls completely outside the source, return a transparent
            // canvas of the requested crop size rather than throwing at the Drawing layer.
            if (startX >= source.Width || startY >= source.Height ||
                startX + width <= 0 || startY + height <= 0)
            {
                return CreateTransparent(width, height, source.BitPerPixel);
            }

            var safe = BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
            if (Math.Abs(angle) <= float.Epsilon)
            {
                return CropEffect.Process(source, safe.X, safe.Y, safe.Width, safe.Height);
            }
            else
            {
                return source switch
                {
                    IPicture<byte> p8 => CropAndRotate(p8, startX, startY, width, height, angle),
                    IPicture<ushort> p16 => CropAndRotate(p16, startX, startY, width, height, angle),
                    _ => throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}"),
                };
            }
        }

        private static IPicture<byte> CropAndRotate(IPicture<byte> src, int startX, int startY, int width, int height, float angle)
        {
            var safe = BuildSafeCropRect(startX, startY, width, height, src.Width, src.Height);
            int outW = safe.Width, outH = safe.Height;
            int pixels = outW * outH;

            float cx = safe.X + safe.Width / 2f;
            float cy = safe.Y + safe.Height / 2f;
            float angleRad = angle * MathF.PI / 180f;
            float cosA = MathF.Cos(angleRad);
            float sinA = MathF.Sin(angleRad);

            var result = new Picture8bpp(outW, outH)
            {
                r = GC.AllocateUninitializedArray<byte>(pixels),
                g = GC.AllocateUninitializedArray<byte>(pixels),
                b = GC.AllocateUninitializedArray<byte>(pixels),
                a = src.HasAlphaChannel && src.a != null
                    ? GC.AllocateUninitializedArray<float>(pixels) : null,
                HasAlphaChannel = src.HasAlphaChannel,
                Tag = src.Tag,
            };

            int srcW = src.Width, srcH = src.Height;

            for (int oy = 0; oy < outH; oy++)
            {
                int dstRowStart = oy * outW;
                for (int ox = 0; ox < outW; ox++)
                {
                    float rx = safe.X + ox - cx;
                    float ry = safe.Y + oy - cy;

                    float sx = cosA * rx - sinA * ry + cx;
                    float sy = sinA * rx + cosA * ry + cy;

                    int idx = dstRowStart + ox;
                    if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH)
                    {
                        SampleBilinear(src, sx, sy, out byte rr, out byte gg, out byte bb);
                        result.r[idx] = rr;
                        result.g[idx] = gg;
                        result.b[idx] = bb;
                    }
                    // out-of-bounds pixels stay 0 (black/transparent)
                }
            }

            if (result.a != null && src.a != null)
            {
                for (int oy = 0; oy < outH; oy++)
                {
                    int dstRowStart = oy * outW;
                    for (int ox = 0; ox < outW; ox++)
                    {
                        float rx = safe.X + ox - cx;
                        float ry = safe.Y + oy - cy;
                        float sx = cosA * rx - sinA * ry + cx;
                        float sy = sinA * rx + cosA * ry + cy;
                        if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH)
                            result.a[dstRowStart + ox] = SampleBilinearAlpha(src, sx, sy);
                    }
                }
            }

            return result;
        }

        private static IPicture<ushort> CropAndRotate(IPicture<ushort> src, int startX, int startY, int width, int height, float angle)
        {
            var safe = BuildSafeCropRect(startX, startY, width, height, src.Width, src.Height);
            int outW = safe.Width, outH = safe.Height;
            int pixels = outW * outH;

            float cx = safe.X + safe.Width / 2f;
            float cy = safe.Y + safe.Height / 2f;
            float angleRad = angle * MathF.PI / 180f;
            float cosA = MathF.Cos(angleRad);
            float sinA = MathF.Sin(angleRad);

            var result = new Picture16bpp(outW, outH)
            {
                r = GC.AllocateUninitializedArray<ushort>(pixels),
                g = GC.AllocateUninitializedArray<ushort>(pixels),
                b = GC.AllocateUninitializedArray<ushort>(pixels),
                a = src.HasAlphaChannel && src.a != null
                    ? GC.AllocateUninitializedArray<float>(pixels) : null,
                HasAlphaChannel = src.HasAlphaChannel,
                Tag = src.Tag,
            };

            int srcW = src.Width, srcH = src.Height;

            for (int oy = 0; oy < outH; oy++)
            {
                int dstRowStart = oy * outW;
                for (int ox = 0; ox < outW; ox++)
                {
                    float rx = safe.X + ox - cx;
                    float ry = safe.Y + oy - cy;
                    float sx = cosA * rx - sinA * ry + cx;
                    float sy = sinA * rx + cosA * ry + cy;

                    int idx = dstRowStart + ox;
                    if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH)
                    {
                        SampleBilinear(src, sx, sy, out ushort rr, out ushort gg, out ushort bb);
                        result.r[idx] = rr;
                        result.g[idx] = gg;
                        result.b[idx] = bb;
                    }
                }
            }

            if (result.a != null && src.a != null)
            {
                for (int oy = 0; oy < outH; oy++)
                {
                    int dstRowStart = oy * outW;
                    for (int ox = 0; ox < outW; ox++)
                    {
                        float rx = safe.X + ox - cx;
                        float ry = safe.Y + oy - cy;
                        float sx = cosA * rx - sinA * ry + cx;
                        float sy = sinA * rx + cosA * ry + cy;
                        if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH)
                            result.a[dstRowStart + ox] = SampleBilinearAlpha(src, sx, sy);
                    }
                }
            }

            return result;
        }

        private static void SampleBilinear(IPicture<byte> src, float x, float y, out byte r, out byte g, out byte b)
        {
            int x0 = (int)MathF.Floor(x); if (x < 0f) x0--;
            int y0 = (int)MathF.Floor(y); if (y < 0f) y0--;
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            float fx = x - x0;
            float fy = y - y0;
            int w = src.Width, h = src.Height;
            if (x0 < 0) x0 = 0; if (x1 >= w) x1 = w - 1;
            if (y0 < 0) y0 = 0; if (y1 >= h) y1 = h - 1;
            int i00 = y0 * w + x0, i10 = y0 * w + x1, i01 = y1 * w + x0, i11 = y1 * w + x1;
            float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
            r = (byte)Math.Clamp((int)(src.r[i00] * w00 + src.r[i10] * w10 + src.r[i01] * w01 + src.r[i11] * w11 + 0.5f), 0, 255);
            g = (byte)Math.Clamp((int)(src.g[i00] * w00 + src.g[i10] * w10 + src.g[i01] * w01 + src.g[i11] * w11 + 0.5f), 0, 255);
            b = (byte)Math.Clamp((int)(src.b[i00] * w00 + src.b[i10] * w10 + src.b[i01] * w01 + src.b[i11] * w11 + 0.5f), 0, 255);
        }

        private static void SampleBilinear(IPicture<ushort> src, float x, float y, out ushort r, out ushort g, out ushort b)
        {
            int x0 = (int)MathF.Floor(x); if (x < 0f) x0--;
            int y0 = (int)MathF.Floor(y); if (y < 0f) y0--;
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            float fx = x - x0;
            float fy = y - y0;
            int w = src.Width, h = src.Height;
            if (x0 < 0) x0 = 0; if (x1 >= w) x1 = w - 1;
            if (y0 < 0) y0 = 0; if (y1 >= h) y1 = h - 1;
            int i00 = y0 * w + x0, i10 = y0 * w + x1, i01 = y1 * w + x0, i11 = y1 * w + x1;
            float w00 = (1f - fx) * (1f - fy), w10 = fx * (1f - fy), w01 = (1f - fx) * fy, w11 = fx * fy;
            r = (ushort)Math.Clamp((int)(src.r[i00] * w00 + src.r[i10] * w10 + src.r[i01] * w01 + src.r[i11] * w11 + 0.5f), 0, 65535);
            g = (ushort)Math.Clamp((int)(src.g[i00] * w00 + src.g[i10] * w10 + src.g[i01] * w01 + src.g[i11] * w11 + 0.5f), 0, 65535);
            b = (ushort)Math.Clamp((int)(src.b[i00] * w00 + src.b[i10] * w10 + src.b[i01] * w01 + src.b[i11] * w11 + 0.5f), 0, 65535);
        }

        private static float SampleBilinearAlpha(IPicture<byte> src, float x, float y)
        {
            if (src.a == null) return 1f;
            int x0 = (int)MathF.Floor(x); if (x < 0f) x0--;
            int y0 = (int)MathF.Floor(y); if (y < 0f) y0--;
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            float fx = x - x0, fy = y - y0;
            int w = src.Width, h = src.Height;
            if (x0 < 0) x0 = 0; if (x1 >= w) x1 = w - 1;
            if (y0 < 0) y0 = 0; if (y1 >= h) y1 = h - 1;
            int i00 = y0 * w + x0, i10 = y0 * w + x1, i01 = y1 * w + x0, i11 = y1 * w + x1;
            return src.a[i00] * (1f - fx) * (1f - fy) + src.a[i10] * fx * (1f - fy) + src.a[i01] * (1f - fx) * fy + src.a[i11] * fx * fy;
        }

        private static float SampleBilinearAlpha(IPicture<ushort> src, float x, float y)
        {
            if (src.a == null) return 1f;
            int x0 = (int)MathF.Floor(x); if (x < 0f) x0--;
            int y0 = (int)MathF.Floor(y); if (y < 0f) y0--;
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            float fx = x - x0, fy = y - y0;
            int w = src.Width, h = src.Height;
            if (x0 < 0) x0 = 0; if (x1 >= w) x1 = w - 1;
            if (y0 < 0) y0 = 0; if (y1 >= h) y1 = h - 1;
            int i00 = y0 * w + x0, i10 = y0 * w + x1, i01 = y1 * w + x0, i11 = y1 * w + x1;
            return src.a[i00] * (1f - fx) * (1f - fy) + src.a[i10] * fx * (1f - fy) + src.a[i01] * (1f - fx) * fy + src.a[i11] * fx * fy;
        }

        public static (float[] r, float[] g, float[] b, float[] a, bool sourceHasAlpha) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                return (
                    p16.r.Select(Convert.ToSingle).ToArray(),
                    p16.g.Select(Convert.ToSingle).ToArray(),
                    p16.b.Select(Convert.ToSingle).ToArray(),
                    p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray(),
                    p16.HasAlphaChannel && p16.a is not null
                );
            }

            if (source is IPicture<byte> p8)
            {
                return (
                    p8.r.Select(Convert.ToSingle).ToArray(),
                    p8.g.Select(Convert.ToSingle).ToArray(),
                    p8.b.Select(Convert.ToSingle).ToArray(),
                    p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray(),
                    p8.HasAlphaChannel && p8.a is not null
                );
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        public static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a, bool keepAlpha)
        {
            if (source.BitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.g = g.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.b = b.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            if (source.BitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.g = g.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.b = b.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }

        public static IPicture CreateTransparent(int width, int height, IPicture.PicturePixelMode bitPerPixel)
        {
            int pixels = width * height;
            if (bitPerPixel == IPicture.PicturePixelMode.UShortPicture)
            {
                return new Picture16bpp(width, height)
                {
                    r = new ushort[pixels],
                    g = new ushort[pixels],
                    b = new ushort[pixels],
                    a = new float[pixels],
                    HasAlphaChannel = true,
                };
            }
            else
            {
                return new Picture8bpp(width, height)
                {
                    r = new byte[pixels],
                    g = new byte[pixels],
                    b = new byte[pixels],
                    a = new float[pixels],
                    HasAlphaChannel = true,
                };
            }
        }
    }


    public class CropEffect_HwAccel : INormalEffect, IDynamicArgumentsEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Crop";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }
        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "Height", Height },
            { "Width", Width },
            { "Angle", Angle },
        };

        public string? NeedComputer => "CropComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
            "Angle"
        };

        public static List<string> OptionalParameters { get; } = new List<string>
        {
            "Angle",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
            { "Height", "int" },
            { "Width", "int" },
            { "Angle", "float" },
        };

        public string TypeName => "Crop";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.Except(OptionalParameters).All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var unsupportedParameters = parameters.Keys.Except(ParametersNeeded).Except(OptionalParameters).ToList();
            if (unsupportedParameters.Count > 0)
            {
                throw new ArgumentException($"Unsupported parameters: {string.Join(", ", unsupportedParameters)}");
            }

            float angle = parameters.TryGetValue("Angle", out var angleVal) ? Convert.ToSingle(angleVal) : 0f;

            return new CropEffect_HwAccel
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = angle,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int cropX = DynamicParam.Resolve(DynamicProviders, "StartX", StartX);
            int cropY = DynamicParam.Resolve(DynamicProviders, "StartY", StartY);
            int cropW = DynamicParam.Resolve(DynamicProviders, "Width", Width);
            int cropH = DynamicParam.Resolve(DynamicProviders, "Height", Height);
            float cropAngle = DynamicParam.Resolve(DynamicProviders, "Angle", Angle);
            int startX = cropX;
            int startY = cropY;
            int width = cropW;
            int height = cropH;

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)cropX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)cropY * targetHeight / RelativeHeight);
                width = (int)Math.Round((double)cropW * targetWidth / RelativeWidth);
                height = (int)Math.Round((double)cropH * targetHeight / RelativeHeight);
            }

            if (Math.Abs(cropAngle) > float.Epsilon)
            {
                return CropEffectShared.CropAndProcess(source, startX, startY, width, height, cropAngle);
            }

            if (startX >= source.Width || startY >= source.Height ||
                startX + width <= 0 || startY + height <= 0)
            {
                return CropEffectShared.CreateTransparent(width, height, source.BitPerPixel);
            }

            var safeRect = CropEffectShared.BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
            if (computer is null)
            {
                return CropEffect.Process(source, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height);
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = ExtractFloatChannels(source);

            FourChannelResult cropResult;
            if (computer is ICropComputer cc)
            {
                cropResult = cc.ComputeCrop(r, g, b, a, source.Width, source.Height,
                    safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height);
            }
            else
            {
                var resultArr = computer.Compute([
                    r, g, b, a,
                    source.Width, source.Height,
                    safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height
                ]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("CropComputer did not return expected channel buffers.");
                }

                cropResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = BuildPicture(source, safeRect.Width, safeRect.Height,
                cropResult.R, cropResult.G, cropResult.B, cropResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Crop (GPU)",
                Operator = typeof(CropEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "StartX", safeRect.X },
                    { "StartY", safeRect.Y },
                    { "Width", safeRect.Width },
                    { "Height", safeRect.Height },
                    { "Angle", 0f }
                }
            }).ToList();
            return result;
        }

        private static (float[] r, float[] g, float[] b, float[] a, bool sourceHasAlpha) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                return (
                    p16.r.Select(Convert.ToSingle).ToArray(),
                    p16.g.Select(Convert.ToSingle).ToArray(),
                    p16.b.Select(Convert.ToSingle).ToArray(),
                    p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray(),
                    p16.HasAlphaChannel && p16.a is not null
                );
            }

            if (source is IPicture<byte> p8)
            {
                return (
                    p8.r.Select(Convert.ToSingle).ToArray(),
                    p8.g.Select(Convert.ToSingle).ToArray(),
                    p8.b.Select(Convert.ToSingle).ToArray(),
                    p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray(),
                    p8.HasAlphaChannel && p8.a is not null
                );
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        private static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a, bool keepAlpha)
        {
            if (source.BitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.g = g.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.b = b.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            if (source.BitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.g = g.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.b = b.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }
    }





    /// <summary>
    /// The Render-side provider of the Crop effect. Builds either the normal crop or the continuous
    /// progress cropper depending on the <see cref="EffectProviderBase.IsContinuousEffectParameterKey"/> parameter.
    /// </summary>
    public class CropEffectProvider : EffectProviderBase
    {
        public CropEffectProvider()
        {
            Name = "Crop";
            Parameters = new Dictionary<string, object>
            {
                { "StartX", 0 }, { "StartY", 0 }, { "Width", 1280 }, { "Height", 720 }, { "Angle", 0f },
            };
        }

        public override string TypeName => "Crop";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("StartX", EffectArgumentFieldType.Integer, "0"),
                Field("StartY", EffectArgumentFieldType.Integer, "0"),
                Field("Width", EffectArgumentFieldType.Integer, "1280", min: "1"),
                Field("Height", EffectArgumentFieldType.Integer, "720", min: "1"),
                Field("Angle", EffectArgumentFieldType.Numeric, "0", min: "-180", max: "180"),
                // Declared so the continuous-Crop serialized parameters (which include CropList) can be
                // typed by ConvertParams / NormalizedParameters. The normal crop never emits this key.
                Field("CropList", EffectArgumentFieldType.String, "[]", remarks: "Serialized CropData array as JSON string (continuous crop only)")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration, EffectImplementType.IPicture];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            if (parameters.Remove(IsContinuousEffectParameterKey, out _))
                return [new ProgressCropperEffectFactory().Build(implementType, parameters)];
            return [new CropEffectFactory().Build(implementType, parameters)];
        }
    }
}
