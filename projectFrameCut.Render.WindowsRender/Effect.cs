using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Shared;
using projectFrameCut.Render.Effects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Render.ILGPU
{
    public class Effect
    {
        private static object locker = new();
        public static bool ForceSync { get; set; } = false;
        public static Picture RenderEffect(Picture source, EffectType mode, Accelerator? accelerator, uint frameIndex, object[] arguments)
        {
            Log($"[#{frameIndex:d6}@Effect] start render effect '{mode}' ...");

            switch (mode)
            {
                case EffectType.RemoveColor:
                    {
#pragma warning disable CS8600
                        string colorString = arguments[0] is JsonElement je ? je.GetString() : (string)arguments[0];
#pragma warning restore CS8600
                        int range = arguments[1] is JsonElement je2 ? je2.GetInt32() : (int)arguments[1];
                        ArgumentNullException.ThrowIfNull(colorString, nameof(colorString));
                        if (colorString.Length !=8) throw new ArgumentException("Color string must be a8-character hexadecimal string representing ARGB color, e.g., '00FF00FF' for magenta.", nameof(colorString));

                        var color = System.Drawing.Color.FromArgb(int.Parse(colorString, System.Globalization.NumberStyles.HexNumber));
                        return RenderRemoveColorCPU(source, frameIndex, color, range);
                    }
                case EffectType.ReplaceAlpha:
                    {
                        // Expect argument: Picture b (replacement alpha source) or float alpha
                        if (arguments.Length >=1 && arguments[0] is Picture bpic)
                        {
                            return RenderReplaceAlpha(source, bpic, accelerator!, frameIndex);
                        }
                        if (arguments.Length >=1 && (arguments[0] is float f))
                        {
                            // replace alpha with constant
                            var dest = new Picture(source)
                            {
                                r = source.r,
                                g = source.g,
                                b = source.b,
                                a = Enumerable.Repeat(f, source.Pixels).ToArray(),
                                hasAlphaChannel = true,
                                Width = source.Width,
                                Height = source.Height,
                                frameIndex = frameIndex
                            };
                            return dest;
                        }
                        break;
                    }
            }

            var method = typeof(Effect).GetMethod($"Render{mode}", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new KeyNotFoundException($"Effect type '{mode}' is not supported or not exist.");
            var result = method.Invoke(null, Enumerable.Concat(new object?[] { source, accelerator, frameIndex }, arguments).ToArray());
            if (result is Picture pic) return pic;
            throw new InvalidCastException($"The result of effect render method '{mode}' is not a Picture.");
        }

        private static Picture RenderRemoveColorCPU(Picture a, uint frameIndex, System.Drawing.Color color, int range)
        {
            int pixels = a.Pixels;
            // convert bounds to normalized0..1
            int lowR = Math.Max(0, (color.R *257) - range);
            int lowG = Math.Max(0, (color.G *257) - range);
            int lowB = Math.Max(0, (color.B *257) - range);
            int highR = Math.Min(65535, (color.R *257) + range);
            int highG = Math.Min(65535, (color.G *257) + range);
            int highB = Math.Min(65535, (color.B *257) + range);

            float lowRf = lowR /65535f;
            float lowGf = lowG /65535f;
            float lowBf = lowB /65535f;
            float highRf = highR /65535f;
            float highGf = highG /65535f;
            float highBf = highB /65535f;

            float[] aR = new float[pixels];
            float[] aG = new float[pixels];
            float[] aB = new float[pixels];
            for (int i =0; i < pixels; i++)
            {
                aR[i] = a.r[i] /65535f;
                aG[i] = a.g[i] /65535f;
                aB[i] = a.b[i] /65535f;
            }

            float[] zeros = new float[pixels];

            float[] rMask = ComputerSource.RemoveColorComputer.Compute(MyAcceleratorType.CPU,3, aR, Enumerable.Repeat(lowRf, pixels).ToArray(), Enumerable.Repeat(highRf, pixels).ToArray(), zeros, zeros, zeros);
            float[] gMask = ComputerSource.RemoveColorComputer.Compute(MyAcceleratorType.CPU,3, aG, Enumerable.Repeat(lowGf, pixels).ToArray(), Enumerable.Repeat(highGf, pixels).ToArray(), zeros, zeros, zeros);
            float[] bMask = ComputerSource.RemoveColorComputer.Compute(MyAcceleratorType.CPU,3, aB, Enumerable.Repeat(lowBf, pixels).ToArray(), Enumerable.Repeat(highBf, pixels).ToArray(), zeros, zeros, zeros);

            a.EnsureAlpha();
            float[] newAlpha = new float[pixels];
            for (int i =0; i < pixels; i++)
            {
                bool removed = rMask[i] ==0f && gMask[i] ==0f && bMask[i] ==0f;
                newAlpha[i] = removed ?0f : a.a![i];
                if (newAlpha[i] ==0f)
                {
                    a.r[i] =0; a.g[i] =0; a.b[i] =0;
                }
            }

            var result = new Picture(a)
            {
                r = a.r,
                g = a.g,
                b = a.b,
                a = newAlpha,
                hasAlphaChannel = true,
                frameIndex = frameIndex
            };
            return result;
        }

        public static Picture RenderReplaceAlpha(Picture a, Picture b, Accelerator accelerator, uint frameIndex)
        {
            if (a.Pixels != b.Pixels) throw new ArgumentException("Picture a and b must have the same number of pixels to replace alpha channel.");
            var result = new Picture(a)
            {
                r = a.r,
                g = a.g,
                b = a.b,
                a = b.a,
                hasAlphaChannel = true,
                Width = a.Width,
                Height = a.Height
            };
            Log($"[Render #{frameIndex:d6}] Rendering ReplaceAlpha done.");
            return result;
        }

        public static Picture RenderCropAndResize(Picture a, Accelerator accelerator, uint frameIndex, int xStart, int yStart, int xEnd, int yEnd)
        {
            // Use existing CPU implementations in Picture.Resize instead of GPU kernels
            int w = xEnd - xStart;
            int h = yEnd - yStart;
            if (w <=0 || h <=0) throw new ArgumentException("Invalid crop rectangle");
            Picture cropped = new Picture(w, h);
            // naive copy
            for (int y =0; y < h; y++)
            {
                for (int x =0; x < w; x++)
                {
                    int dst = y * w + x;
                    int src = (y + yStart) * a.Width + (x + xStart);
                    cropped.r[dst] = a.r[src];
                    cropped.g[dst] = a.g[src];
                    cropped.b[dst] = a.b[src];
                }
            }
            return cropped.Resize(a.Width, a.Height);
        }

        public static Picture RenderResize(Picture a, Accelerator accelerator, uint frameIndex, int newWidth, int newHeight)
        {
            return a.Resize(newWidth, newHeight);
        }

        public static Picture RenderColorCorrection(Picture a, Accelerator accelerator, uint frameIndex, float brightness, float contrast, float saturation)
        {
            int pixels = a.Pixels;
            float[] r = new float[pixels];
            float[] g = new float[pixels];
            float[] b = new float[pixels];
            for (int i =0; i < pixels; i++)
            {
                r[i] = a.r[i] /65535f;
                g[i] = a.g[i] /65535f;
                b[i] = a.b[i] /65535f;
            }
            float[] br = Enumerable.Repeat(brightness, pixels).ToArray();
            float[] co = Enumerable.Repeat(contrast, pixels).ToArray();
            float[] sa = Enumerable.Repeat(saturation, pixels).ToArray();
            float[] zeros = new float[pixels];

            float[] or = ComputerSource.ColorCorrectionComputer.Compute(MyAcceleratorType.CPU,3, r, br, co, sa, zeros, zeros);
            float[] og = ComputerSource.ColorCorrectionComputer.Compute(MyAcceleratorType.CPU,3, g, br, co, sa, zeros, zeros);
            float[] ob = ComputerSource.ColorCorrectionComputer.Compute(MyAcceleratorType.CPU,3, b, br, co, sa, zeros, zeros);

            var result = new Picture(a)
            {
                r = new ushort[pixels],
                g = new ushort[pixels],
                b = new ushort[pixels],
                a = a.a,
                hasAlphaChannel = a.hasAlphaChannel,
                Width = a.Width,
                Height = a.Height
            };
            for (int i =0; i < pixels; i++)
            {
                result.r[i] = (ushort)Math.Clamp((int)Math.Round(or[i] *65535f),0,65535);
                result.g[i] = (ushort)Math.Clamp((int)Math.Round(og[i] *65535f),0,65535);
                result.b[i] = (ushort)Math.Clamp((int)Math.Round(ob[i] *65535f),0,65535);
            }
            return result;
        }

        private static MemoryBuffer1D<T, Stride1D.Dense> Allocate1D<T>(int pixels, Accelerator accelerator) where T : unmanaged
        {
            if (ForceSync)
            {
                lock (locker)
                {
                    return accelerator.Allocate1D<T>(pixels);
                }
            }
            else
            {
                return accelerator.Allocate1D<T>(pixels);
            }

        }
    }
}
