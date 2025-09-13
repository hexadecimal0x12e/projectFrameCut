using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Render
{
    public class Effects
    {
        public Effect.EffectType Type { get; init; }
        public object[] Arguments { get; init; }
        public Effects(Effect.EffectType type, params object[] arguments)
        {
            Type = type;
            Arguments = arguments;
        }

        public override string ToString()
        {
            return $"Effect Type: {Type}, Arguments: [{string.Join(", ", Arguments.Select(arg => arg?.ToString() ?? "null"))}]";
        }
    }

    public class Effect
    {
        private static object locker = new();
        public static bool ForceSync { get; set; } = false;
        public static Picture RenderEffect(Picture source, EffectType mode, Accelerator accelerator, uint frameIndex, object[] arguments)
        {
            Log($"[#{frameIndex:d6}@Effect] start render effect '{mode}' with GPU...");

            switch (mode)
            {
                case EffectType.RemoveColor:
                    {
#pragma warning disable CS8600 
                        string colorString = arguments[0] is JsonElement je ? je.GetString() : (string)arguments[0];
#pragma warning restore CS8600 
                        int range = arguments[1] is JsonElement je2 ? je2.GetInt32() : (int)arguments[1];
                        ArgumentNullException.ThrowIfNull(colorString, nameof(colorString));
                        if (colorString.Length != 8) throw new ArgumentException("Color string must be a 6-character hexadecimal string representing ARGB color, e.g., '00FF00FF' for magenta.", nameof(colorString));

                        return RenderRemoveColor(
                            source,
                            accelerator,
                            frameIndex,
                            System.Drawing.Color.FromArgb(int.Parse(colorString, System.Globalization.NumberStyles.HexNumber)),
                            range
                        );

                    }

                default:
                    break;
            }



            var result = (typeof(Effect).GetMethod($"Render{mode}") ?? throw new KeyNotFoundException($"Effect type '{mode}' is not supported or not exist.")).Invoke(null, Enumerable.Concat([source, accelerator, frameIndex], arguments).ToArray());
            if(result is Picture pictureResult)
            {
                return pictureResult;
            } 
            throw new InvalidCastException($"The result of effect render method '{mode}' is not a Picture.");

        }

        public static Picture RenderCropAndResize(Picture a, Accelerator accelerator, uint frameIndex, int xStart, int yStart, int xEnd, int yEnd)
        {
            throw new NotImplementedException("RenderCropAndResize is not implemented yet.");
        }

        public static Picture RenderResize(Picture a, Accelerator accelerator, uint frameIndex, int newWidth, int newHeight)
        {
            throw new NotImplementedException("RenderResize is not implemented yet.");
        }

        public static Picture RenderColorCorrection(Picture a, Accelerator accelerator, uint frameIndex, float brightness, float contrast, float saturation)
        {
            throw new NotImplementedException("RenderColorCorrection is not implemented yet.");
        }

        public static Picture RenderRemoveColor(Picture a, Accelerator accelerator, uint frameIndex, System.Drawing.Color color, int range)
        {
            var kernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,                             //i 
                ArrayView1D<ushort, Stride1D.Dense>, //r
                ArrayView1D<ushort, Stride1D.Dense>, //lowR
                ArrayView1D<ushort, Stride1D.Dense>, //highR
                ArrayView1D<byte, Stride1D.Dense>   //c
                >(static
                (i, a, low, high, c) =>
                {
                    if (low[0] <= a[i] && a[i] <= high[0])
                    {
                        c[i] = 1;

                    }
                    else
                    {
                        c[i] = 0;

                    }

                });



            //Log($"[Render #{frameIndex:d6}] Loading data...");



            using MemoryBuffer1D<ushort, Stride1D.Dense> lowRed = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> lowGreen = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> lowBlue = accelerator.Allocate1D<ushort>(1);

            using MemoryBuffer1D<ushort, Stride1D.Dense> highRed = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> highGreen = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> highBlue = accelerator.Allocate1D<ushort>(1);

            using MemoryBuffer1D<byte, Stride1D.Dense> cAlphaR = accelerator.Allocate1D<byte>(a.Pixels);
            using MemoryBuffer1D<byte, Stride1D.Dense> cAlphaG = accelerator.Allocate1D<byte>(a.Pixels);
            using MemoryBuffer1D<byte, Stride1D.Dense> cAlphaB = accelerator.Allocate1D<byte>(a.Pixels);

            //把8bit颜色转换到16bit
            int lowR = (color.R * 257) - (ushort)range,
                lowG = (color.G * 257) - (ushort)range,
                lowB = (color.B * 257) - (ushort)range,
                highR = (color.R * 257) + (ushort)range,
                highG = (color.G * 257) + (ushort)range,
                highB = (color.B * 257) + (ushort)range;

            lowR = lowR < 0 ? 0 : lowR;
            lowG = lowG < 0 ? 0 : lowG;
            lowB = lowB < 0 ? 0 : lowB;
            highR = highR > 65535 ? 65535 : highR;
            highG = highG > 65535 ? 65535 : highG;
            highB = highB > 65535 ? 65535 : highB;

            checked
            {
                lowRed.CopyFromCPU([(ushort)lowR]);
                lowGreen.CopyFromCPU([(ushort)lowG]);
                lowBlue.CopyFromCPU([(ushort)lowB]);

                highRed.CopyFromCPU([(ushort)highR]);
                highGreen.CopyFromCPU([(ushort)highG]);
                highBlue.CopyFromCPU([(ushort)highB]);
            }


            cAlphaR.CopyFromCPU(new byte[a.Pixels]);
            cAlphaG.CopyFromCPU(new byte[a.Pixels]);
            cAlphaB.CopyFromCPU(new byte[a.Pixels]);

            using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = Allocate1D<ushort>(a.Pixels,accelerator);
            aRed.CopyFromCPU(a.r);
            LockRun(() => kernel(a.Pixels,
                            aRed.View,
                            lowRed.View,
                            highRed.View,
                            cAlphaR.View));
            if (ForceSync) accelerator.Synchronize();

            aRed.Dispose();
            lowRed.Dispose();
            highRed.Dispose();

            using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = Allocate1D<ushort>(a.Pixels,accelerator);
            aGreen.CopyFromCPU(a.g);
            LockRun(() => kernel(a.Pixels,
                        aGreen.View,
                        lowGreen.View,
                        highGreen.View,
                        cAlphaG.View));
            if (ForceSync) accelerator.Synchronize();

            aGreen.Dispose();
            highGreen.Dispose();
            lowGreen.Dispose();

            using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = Allocate1D<ushort>(a.Pixels,accelerator);
            aBlue.CopyFromCPU(a.b);
            LockRun(() => kernel(a.Pixels,
                        aBlue.View,
                        lowBlue.View,
                        highBlue.View,
                        cAlphaB.View));
            if (ForceSync) accelerator.Synchronize();

            aBlue.Dispose();
            highBlue.Dispose();
            lowBlue.Dispose();


            byte[] AlphaR = new byte[a.Pixels], AlphaG = new byte[a.Pixels], AlphaB = new byte[a.Pixels];
            float[] alpha = new float[a.Pixels];
            cAlphaR.CopyToCPU(AlphaR);
            cAlphaG.CopyToCPU(AlphaG);
            cAlphaB.CopyToCPU(AlphaB);

            a.EnsureAlpha();

            for (int i = 0; i < a.Pixels; i++)
            {
#pragma warning disable CS8602 // 这里已经保证有alpha通道了
                alpha[i] = AlphaR[i] == 1 && AlphaG[i] == 1 && AlphaB[i] == 1 ? 0f : a.a[i];
#pragma warning restore CS8602 
            }

            var result = new Picture(a)
            {
                r = a.r,
                g = a.g,
                b = a.b,
                a = alpha,
                hasAlphaChannel = true,
                frameIndex = frameIndex
            };

            ////Log($"{result.a.Count((f) => f <= 0.01f)} is almost transept, total {result.Pixels}, pctg:{result.a.Count((f) => f <= 0.01f) / result.Pixels:p2}");
            for (int i = 0; i < a.Pixels; i++)
            {
                if (result.a[i] == 0)
                {
                    result.r[i] = 0;
                    result.g[i] = 0;
                    result.b[i] = 0;
                    result.a[i] = 0f;
                }
            }

            result.Width = a.Width;
            result.Height = a.Height;
            //Log($"[Render #{frameIndex:d6}] Rendering RemoveColor done.");

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

        private static void LockRun(Action action)
        {
            if (ForceSync)
            {
                lock (locker)
                {
                    action();
                }

            }
            else
            {
                action();
            }
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

        public enum EffectType
        {
            Crop,
            Resize,
            RemoveColor,
            ReplaceAlpha,
            ColorCorrection,
        }
    }
}
