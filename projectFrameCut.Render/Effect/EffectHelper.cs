
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Effect
{
    public static class EffectHelper
    {
        public static double GetContinuesEffectProgress(uint index, int startPoint, int endPoint)
        {
            if (endPoint <= startPoint) return 1.0;
            if (index < startPoint) return 0.0;
            if (index >= endPoint) return 1.0;
            return (double)(index - startPoint) / (endPoint - startPoint);
        }

        public static double GetContinuesEffectProgress(this IContinuousEffect effect, uint index)
        {
            if (effect.EndPoint <= effect.StartPoint) return 1.0;
            if (index < effect.StartPoint) return 0.0;
            if (index >= effect.EndPoint) return 1.0;
            return (double)(index - effect.StartPoint) / (effect.EndPoint - effect.StartPoint);
        }

        public static Dictionary<string, EffectImplementType> DefaultImplementsType = new();

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider) GetEffectsInstancesAndSpeedVariance(EffectAndMixtureJSONStructure[]? Effects)
        {
            var (effects, provider, _) = GetEffectsInstancesSpeedVarianceAndMixture(Effects);
            return (effects, provider);
        }

        public static (IEffect[] Effects, ISpeedVarianceProvider? SpeedVarianceProvider, IMixture? Mixture) GetEffectsInstancesSpeedVarianceAndMixture(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return (Array.Empty<IEffect>(), null, null);
            }
            List<IEffect> effects = new();
            bool haveSpeedVarProvider = false;
            bool haveMixture = false;
            ISpeedVarianceProvider? provider = null;
            IMixture? mixture = null;
            foreach (var item in Effects)
            {
                var e = PluginManager.CreateEffect(item, item.ImplementType == EffectImplementType.NotSpecified ? DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified) : item.ImplementType);
                effects.Add(e);
                if (e is ISpeedVarianceProvider p)
                {
                    if (haveSpeedVarProvider) throw new InvalidOperationException("Multiple SpeedVarianceProvider effects found.");
                    haveSpeedVarProvider = true;
                    provider = p;
                }
                if (e is IMixture m)
                {
                    if (haveMixture) throw new InvalidOperationException("Multiple IMixture effects found.");
                    haveMixture = true;
                    mixture = m;
                }
            }

            return (effects.Where(c => c.Enabled && c.TypeOfEffect != EffectType.SpeedVarianceProvider && c.TypeOfEffect != EffectType.MixtureProvider).OrderBy(c => c.Index).ToArray(), provider, mixture);
        }
        public static IEffect[] GetEffectsInstances(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return Array.Empty<IEffect>();
            }
            List<IEffect> effects = new();
            foreach (var item in Effects)
            {
                var e = PluginManager.CreateEffect(item, item.ImplementType == EffectImplementType.NotSpecified ? DefaultImplementsType.GetValueOrDefault($"{item.FromPlugin}.{item.TypeName}", EffectImplementType.NotSpecified) : item.ImplementType);
                effects.Add(e);


            }

            return effects.Where(c => c.Enabled).OrderBy(c => c.Index).ToArray();
        }

        public static Dictionary<string, Func<IEffect>> EffectsEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p =>
                       p.EffectProvider
                        .Concat(p.ContinuousEffectProvider)
                        .Concat(p.BindableArgumentEffectProvider))
                .DistinctBy(kv => kv.Value().TypeName)
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static Dictionary<string, IEffectFactory> EffectsFactoriesEnum =>
                PluginManager.LoadedPlugins.Values
                .SelectMany(p => p.EffectFactoryProvider
                        .Concat(p.ContinuousEffectFactoryProvider)
                        .Concat(p.BindableArgumentEffectFactoryProvider))
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public static IEnumerable<string> GetEffectTypes() => EffectsEnum.Keys;

        public static IPicture CropPicture(IPicture source, int startX, int startY, int width, int height, string operationDisplayName, Type operatorType)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive.");
            }
            if (startX < 0 || startY < 0 || startX + width > source.Width || startY + height > source.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(startX), "Crop rectangle must stay inside source bounds.");
            }

            var sw = Stopwatch.StartNew();
            IPicture result;
            if (source is IPicture<ushort> p16)
            {
                var dst = new Picture16bpp(width, height)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = p16.hasAlphaChannel,
                    a = p16.hasAlphaChannel ? new float[width * height] : null
                };

                for (int y = 0; y < height; y++)
                {
                    int srcOffset = (startY + y) * source.Width + startX;
                    int dstOffset = y * width;
                    Array.Copy(p16.r, srcOffset, dst.r, dstOffset, width);
                    Array.Copy(p16.g, srcOffset, dst.g, dstOffset, width);
                    Array.Copy(p16.b, srcOffset, dst.b, dstOffset, width);
                    if (dst.a is not null && p16.a is not null)
                    {
                        Array.Copy(p16.a, srcOffset, dst.a, dstOffset, width);
                    }
                }
                result = dst;
            }
            else if (source is IPicture<byte> p8)
            {
                var dst = new Picture8bpp(width, height)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = p8.hasAlphaChannel,
                    a = p8.hasAlphaChannel ? new float[width * height] : null
                };

                for (int y = 0; y < height; y++)
                {
                    int srcOffset = (startY + y) * source.Width + startX;
                    int dstOffset = y * width;
                    Array.Copy(p8.r, srcOffset, dst.r, dstOffset, width);
                    Array.Copy(p8.g, srcOffset, dst.g, dstOffset, width);
                    Array.Copy(p8.b, srcOffset, dst.b, dstOffset, width);
                    if (dst.a is not null && p8.a is not null)
                    {
                        Array.Copy(p8.a, srcOffset, dst.a, dstOffset, width);
                    }
                }
                result = dst;
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                OperationDisplayName = operationDisplayName,
                Operator = operatorType,
                ProcessingFuncStackTrace = new StackTrace(true),
                Elapsed = sw.Elapsed,
                Properties = new Dictionary<string, object>
                {
                    { "StartX", startX },
                    { "StartY", startY },
                    { "Width", width },
                    { "Height", height }
                }
            }).ToList();
            return result;
        }

        public static IPicture PlacePicture(IPicture source, int startX, int startY, int targetWidth, int targetHeight, string operationDisplayName, Type operatorType)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }

            var sw = Stopwatch.StartNew();
            IPicture result;
            int dstX = Math.Max(0, startX);
            int dstY = Math.Max(0, startY);
            int srcX = Math.Max(0, -startX);
            int srcY = Math.Max(0, -startY);
            int copyWidth = Math.Min(source.Width - srcX, targetWidth - dstX);
            int copyHeight = Math.Min(source.Height - srcY, targetHeight - dstY);

            if (source is IPicture<ushort> p16)
            {
                var dst = new Picture16bpp(targetWidth, targetHeight)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = true,
                    a = new float[targetWidth * targetHeight]
                };

                if (copyWidth > 0 && copyHeight > 0)
                {
                    for (int y = 0; y < copyHeight; y++)
                    {
                        int srcOffset = (srcY + y) * source.Width + srcX;
                        int dstOffset = (dstY + y) * targetWidth + dstX;
                        Array.Copy(p16.r, srcOffset, dst.r, dstOffset, copyWidth);
                        Array.Copy(p16.g, srcOffset, dst.g, dstOffset, copyWidth);
                        Array.Copy(p16.b, srcOffset, dst.b, dstOffset, copyWidth);
                        if (p16.a is not null)
                        {
                            Array.Copy(p16.a, srcOffset, dst.a!, dstOffset, copyWidth);
                        }
                        else
                        {
                            Array.Fill(dst.a!, 1f, dstOffset, copyWidth);
                        }
                    }
                }
                result = dst;
            }
            else if (source is IPicture<byte> p8)
            {
                var dst = new Picture8bpp(targetWidth, targetHeight)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = true,
                    a = new float[targetWidth * targetHeight]
                };

                if (copyWidth > 0 && copyHeight > 0)
                {
                    for (int y = 0; y < copyHeight; y++)
                    {
                        int srcOffset = (srcY + y) * source.Width + srcX;
                        int dstOffset = (dstY + y) * targetWidth + dstX;
                        Array.Copy(p8.r, srcOffset, dst.r, dstOffset, copyWidth);
                        Array.Copy(p8.g, srcOffset, dst.g, dstOffset, copyWidth);
                        Array.Copy(p8.b, srcOffset, dst.b, dstOffset, copyWidth);
                        if (p8.a is not null)
                        {
                            Array.Copy(p8.a, srcOffset, dst.a!, dstOffset, copyWidth);
                        }
                        else
                        {
                            Array.Fill(dst.a!, 1f, dstOffset, copyWidth);
                        }
                    }
                }
                result = dst;
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                OperationDisplayName = operationDisplayName,
                Operator = operatorType,
                ProcessingFuncStackTrace = new StackTrace(true),
                Elapsed = sw.Elapsed,
                Properties = new Dictionary<string, object>
                {
                    { "StartX", startX },
                    { "StartY", startY },
                    { "TargetWidth", targetWidth },
                    { "TargetHeight", targetHeight }
                }
            }).ToList();
            return result;
        }

        public static IPicture ApplyMaskPicture(IPicture frame, BitMaskPicture maskPic, string operationDisplayName, Type operatorType)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(maskPic);

            var sw = Stopwatch.StartNew();
            bool sizeMatch = maskPic.Width == frame.Width && maskPic.Height == frame.Height;

            IPicture result;
            if (frame is IPicture<ushort> p16)
            {
                var dst = new Picture16bpp(frame.Width, frame.Height)
                {
                    frameIndex = frame.frameIndex,
                    filePath = frame.filePath,
                    hasAlphaChannel = true,
                    a = new float[frame.Pixels]
                };

                for (int y = 0; y < frame.Height; y++)
                {
                    int frameRowOffset = y * frame.Width;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int frameIndex = frameRowOffset + x;
                        bool keepPixel = ResolveMaskValue(maskPic, sizeMatch, x, y, frame.Width, frame.Height);
                        if (keepPixel)
                        {
                            dst.r[frameIndex] = p16.r[frameIndex];
                            dst.g[frameIndex] = p16.g[frameIndex];
                            dst.b[frameIndex] = p16.b[frameIndex];
                            dst.a![frameIndex] = p16.a?[frameIndex] ?? 1f;
                        }
                    }
                }
                result = dst;
            }
            else if (frame is IPicture<byte> p8)
            {
                var dst = new Picture8bpp(frame.Width, frame.Height)
                {
                    frameIndex = frame.frameIndex,
                    filePath = frame.filePath,
                    hasAlphaChannel = true,
                    a = new float[frame.Pixels]
                };

                for (int y = 0; y < frame.Height; y++)
                {
                    int frameRowOffset = y * frame.Width;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int frameIndex = frameRowOffset + x;
                        bool keepPixel = ResolveMaskValue(maskPic, sizeMatch, x, y, frame.Width, frame.Height);
                        if (keepPixel)
                        {
                            dst.r[frameIndex] = p8.r[frameIndex];
                            dst.g[frameIndex] = p8.g[frameIndex];
                            dst.b[frameIndex] = p8.b[frameIndex];
                            dst.a![frameIndex] = p8.a?[frameIndex] ?? 1f;
                        }
                    }
                }
                result = dst;
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {frame.GetType().Name}");
            }

            sw.Stop();
            result.ProcessStack = frame.ProcessStack.Append(new PictureProcessStack
            {
                OperationDisplayName = operationDisplayName,
                Operator = operatorType,
                ProcessingFuncStackTrace = new StackTrace(true),
                Elapsed = sw.Elapsed
            }).ToList();
            return result;
        }

        public static IPicture BlurPicture(IPicture source, float sigma, string operationDisplayName, Type operatorType)
        {
            ArgumentNullException.ThrowIfNull(source);
            int radius = Math.Max(0, (int)Math.Ceiling(sigma));
            if (radius == 0)
            {
                var copied = source.DeepCopy();
                copied.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = operationDisplayName,
                    Operator = operatorType,
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "Sigma", sigma },
                        { "Radius", radius }
                    },
                    Elapsed = TimeSpan.Zero
                }).ToList();
                return copied;
            }

            var sw = Stopwatch.StartNew();
            IPicture result;
            if (source is IPicture<ushort> p16)
            {
                var r = BoxBlurChannel(p16.r, source.Width, source.Height, radius);
                var g = BoxBlurChannel(p16.g, source.Width, source.Height, radius);
                var b = BoxBlurChannel(p16.b, source.Width, source.Height, radius);
                var a = p16.a is null ? null : BoxBlurChannel(p16.a, source.Width, source.Height, radius);

                var dst = new Picture16bpp(source.Width, source.Height)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = p16.hasAlphaChannel,
                    a = a
                };
                for (int i = 0; i < source.Pixels; i++)
                {
                    dst.r[i] = (ushort)Math.Clamp((int)Math.Round(r[i]), 0, 65535);
                    dst.g[i] = (ushort)Math.Clamp((int)Math.Round(g[i]), 0, 65535);
                    dst.b[i] = (ushort)Math.Clamp((int)Math.Round(b[i]), 0, 65535);
                }
                result = dst;
            }
            else if (source is IPicture<byte> p8)
            {
                var r = BoxBlurChannel(p8.r, source.Width, source.Height, radius);
                var g = BoxBlurChannel(p8.g, source.Width, source.Height, radius);
                var b = BoxBlurChannel(p8.b, source.Width, source.Height, radius);
                var a = p8.a is null ? null : BoxBlurChannel(p8.a, source.Width, source.Height, radius);

                var dst = new Picture8bpp(source.Width, source.Height)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = p8.hasAlphaChannel,
                    a = a
                };
                for (int i = 0; i < source.Pixels; i++)
                {
                    dst.r[i] = (byte)Math.Clamp((int)Math.Round(r[i]), 0, 255);
                    dst.g[i] = (byte)Math.Clamp((int)Math.Round(g[i]), 0, 255);
                    dst.b[i] = (byte)Math.Clamp((int)Math.Round(b[i]), 0, 255);
                }
                result = dst;
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                OperationDisplayName = operationDisplayName,
                Operator = operatorType,
                ProcessingFuncStackTrace = new StackTrace(true),
                Elapsed = sw.Elapsed,
                Properties = new Dictionary<string, object>
                {
                    { "Sigma", sigma },
                    { "Radius", radius }
                }
            }).ToList();
            return result;
        }

        private static bool ResolveMaskValue(BitMaskPicture maskPic, bool sizeMatch, int x, int y, int frameWidth, int frameHeight)
        {
            if (sizeMatch)
            {
                return maskPic.r[y * maskPic.Width + x];
            }

            int maskX = (int)((float)x / frameWidth * maskPic.Width);
            int maskY = (int)((float)y / frameHeight * maskPic.Height);
            int maskIndex = maskY * maskPic.Width + maskX;
            return maskIndex >= 0 && maskIndex < maskPic.r.Length ? maskPic.r[maskIndex] : true;
        }

        private static float[] BoxBlurChannel(float[] source, int width, int height, int radius)
        {
            var horizontal = new float[source.Length];
            var result = new float[source.Length];

            for (int y = 0; y < height; y++)
            {
                var prefix = new double[width + 1];
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    prefix[x + 1] = prefix[x] + source[rowOffset + x];
                }
                for (int x = 0; x < width; x++)
                {
                    int left = Math.Max(0, x - radius);
                    int right = Math.Min(width - 1, x + radius);
                    horizontal[rowOffset + x] = (float)((prefix[right + 1] - prefix[left]) / (right - left + 1));
                }
            }

            for (int x = 0; x < width; x++)
            {
                var prefix = new double[height + 1];
                for (int y = 0; y < height; y++)
                {
                    prefix[y + 1] = prefix[y] + horizontal[y * width + x];
                }
                for (int y = 0; y < height; y++)
                {
                    int top = Math.Max(0, y - radius);
                    int bottom = Math.Min(height - 1, y + radius);
                    result[y * width + x] = (float)((prefix[bottom + 1] - prefix[top]) / (bottom - top + 1));
                }
            }

            return result;
        }

        private static float[] BoxBlurChannel(byte[] source, int width, int height, int radius)
        {
            return BoxBlurChannel(Array.ConvertAll(source, static x => (float)x), width, height, radius);
        }

        private static float[] BoxBlurChannel(ushort[] source, int width, int height, int radius)
        {
            return BoxBlurChannel(Array.ConvertAll(source, static x => (float)x), width, height, radius);
        }

    }
}
