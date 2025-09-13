using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.OpenCL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render
{
    public static class Mixture
    {
        public static bool ForceSync = false;
        private static object locker = new();

        public static Picture MixtureFrames(Accelerator accelerator, RenderMode mode, Picture layerA, Picture layerB, ushort upperBond = ushort.MaxValue, bool allowOverflow = false, object? extend = null, uint frameIndex = 0)
        {
            Log($"[#{frameIndex:d6}@Mixer] start mixing 2 layer with mode {mode} with GPU...");
            if (mode == RenderMode.Overlay)
            {
                return RenderOverlay(layerA, layerB, accelerator, frameIndex); 
            }
            try
            {               
                Action<Index1D, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>>? kernel = null;
                switch (mode)
                {
                    case RenderMode.Add:
                        kernel = accelerator.LoadAutoGroupedStreamKernel<
                       Index1D, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>>(RenderAdd);
                        break;
                    case RenderMode.Minus:
                        kernel = accelerator.LoadAutoGroupedStreamKernel<
                       Index1D, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>>(RenderMinus);
                        break;
                    case RenderMode.Multiply:
                        kernel = accelerator.LoadAutoGroupedStreamKernel<
                       Index1D, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>>(RenderMultiply);
                        break;
                    case RenderMode.Overlay:
                        return RenderOverlay(layerA, layerB, accelerator,frameIndex); //这个处理太复杂了，不适合在这里搞                
                    default:
                        throw new NotSupportedException($"You defined an unsupported mixture mode {mode}.");
                }


                using MemoryBuffer1D<ushort, Stride1D.Dense> bound = Allocate1D<ushort>(2,accelerator);
                bound.CopyFromCPU([upperBond, allowOverflow ? ushort.MinValue : ushort.MaxValue]);
                //0:上限，0代表不启用
                //1:允许溢出，0代表不启用

                using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = Allocate1D<ushort>(layerA.Pixels, accelerator);
                using MemoryBuffer1D<ushort, Stride1D.Dense> bRed = Allocate1D<ushort>(layerA.Pixels, accelerator);
                using MemoryBuffer1D<ushort, Stride1D.Dense> cRed = Allocate1D<ushort>(layerA.Pixels, accelerator);
                aRed.CopyFromCPU(layerA.r);
                bRed.CopyFromCPU(layerB.r);
                LockRun(() => kernel(layerA.Pixels, aRed.View, bRed.View, cRed.View, bound.View));
                if(ForceSync) accelerator.Synchronize();
                aRed.Dispose();
                bRed.Dispose();

                //Log($"[Render #{frameIndex:d6}] Rendering G channel...");

                using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = Allocate1D<ushort>(layerA.Pixels, accelerator);
                using MemoryBuffer1D<ushort, Stride1D.Dense> bGreen = Allocate1D<ushort>(layerA.Pixels, accelerator);
                using MemoryBuffer1D<ushort, Stride1D.Dense> cGreen = Allocate1D<ushort>(layerA.Pixels, accelerator);
                aGreen.CopyFromCPU(layerA.g);
                bGreen.CopyFromCPU(layerB.g);
                LockRun(() => kernel(layerA.Pixels, aGreen.View, bGreen.View, cGreen.View, bound.View));
                if (ForceSync) accelerator.Synchronize();
                aGreen.Dispose();
                bGreen.Dispose();

                //Log($"[Render #{frameIndex:d6}] Rendering B channel...");

                using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = Allocate1D<ushort>(layerA.Pixels, accelerator);
                using MemoryBuffer1D<ushort, Stride1D.Dense> bBlue = Allocate1D<ushort>(layerA.Pixels, accelerator);
                using MemoryBuffer1D<ushort, Stride1D.Dense> cBlue = Allocate1D<ushort>(layerA.Pixels, accelerator);
                aBlue.CopyFromCPU(layerA.b);
                bBlue.CopyFromCPU(layerB.b);
                LockRun(() => kernel(layerA.Pixels, aBlue.View, bBlue.View, cBlue.View, bound.View));
                if (ForceSync) accelerator.Synchronize();
                aBlue.Dispose();
                bBlue.Dispose();

                var result = new Picture(layerA)
                {
                    Width = layerA.Width,
                    Height = layerA.Height,
                    frameIndex = frameIndex
                };

                cRed.CopyToCPU(result.r = new ushort[layerA.Pixels]);
                cGreen.CopyToCPU(result.g = new ushort[layerA.Pixels]);
                cBlue.CopyToCPU(result.b = new ushort[layerA.Pixels]);
                result.a = layerA.a; //保留原图的Alpha通道
                //Log($"[Render #{frameIndex:d6}] Rendering done.");

                return result;
            }
            catch (ILGPU.Runtime.Cuda.CudaException cudaEx)
            {
                if(cudaEx.Message.Contains("out of memory", StringComparison.CurrentCultureIgnoreCase))
                {
                    throw new OutOfMemoryException("VRAM is not enough to render this frame. Try close all programs use GPU, reboot your device, use another render accelerator, or upgrade your GPU or use CPU to render (not recommend).", cudaEx);
                }
            }
            catch (ILGPU.Runtime.OpenCL.CLException ex)
            {
                throw new OutOfMemoryException("An error happens during render, probably the VRAM is not enough to render this frame. Try close all programs use GPU, reboot your device, use another render accelerator, or upgrade your GPU or use CPU to render (not recommend).", ex);
            }
            catch
            {
                throw;
            }


            return new Picture(0,0);

        }



        public static Picture MapAlphaAndUpperBond(this Picture picture, Accelerator accelerator, ushort upperBond = ushort.MaxValue)
        {
            if (!picture.hasAlphaChannel) return picture;
            using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = Allocate1D<ushort>(picture.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = Allocate1D<ushort>(picture.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = Allocate1D<ushort>(picture.Pixels, accelerator);
            using MemoryBuffer1D<float, Stride1D.Dense> aAlpha = accelerator.Allocate1D<float>(picture.Pixels);

            using MemoryBuffer1D<ushort, Stride1D.Dense> cRed = Allocate1D<ushort>(picture.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cGreen = Allocate1D<ushort>(picture.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cBlue = Allocate1D<ushort>(picture.Pixels, accelerator);

            using MemoryBuffer1D<ushort, Stride1D.Dense> bound = accelerator.Allocate1D<ushort>(1);

            var kernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<ushort>, ArrayView<float>, ArrayView<ushort>, ArrayView<ushort>>( static (i, a, alpha, c,b) =>
                    {
                        c[i] = a[i] > b[0] ? b[0] : (ushort)(a[i] * alpha[i]);
                    });

            aAlpha.CopyFromCPU(picture.a);
            bound.CopyFromCPU([upperBond]);

            aRed.CopyFromCPU(picture.r);
            LockRun(() => kernel(picture.Pixels, aRed.View, aAlpha.View, cRed.View,bound.View));
            if (ForceSync) accelerator.Synchronize();
            aRed.Dispose();

            aGreen.CopyFromCPU(picture.g);
            LockRun(() => kernel(picture.Pixels, aGreen.View, aAlpha.View, cGreen.View, bound.View));
            if (ForceSync) accelerator.Synchronize();
            aGreen.Dispose();   

            aBlue.CopyFromCPU(picture.b);
            LockRun(() => kernel(picture.Pixels, aBlue.View, aAlpha.View, cBlue.View, bound.View));
            if (ForceSync) accelerator.Synchronize();
            aBlue.Dispose();

            var result = new Picture (picture.Width, picture.Height)
            {
                hasAlphaChannel = false,
                //a = Array.Empty<float>()
            };

            cRed.CopyToCPU(result.r = new ushort[picture.Pixels]);
            cGreen.CopyToCPU(result.g = new ushort[picture.Pixels]);
            cBlue.CopyToCPU(result.b = new ushort[picture.Pixels]);

            return result;
        }

        public static void RenderAdd(Index1D i, ArrayView<ushort> a, ArrayView<ushort> b, ArrayView<ushort> c, ArrayView<ushort> bound)
        {
            uint temp = (uint)(a[i] + b[i]);
            if (temp > ushort.MaxValue)
            {
                if (bound[1] == 0)
                    c[i] = ushort.MaxValue;
                else
                    c[i] = (ushort)(temp - ushort.MaxValue);
            }
            else
            {
                if (bound[0] > 0)
                    c[i] = (temp < bound[0]) ? (ushort)temp : bound[0];
                else
                    c[i] = (ushort)temp;
            }
        }

        public static void RenderMinus(Index1D i, ArrayView<ushort> a, ArrayView<ushort> b, ArrayView<ushort> c, ArrayView<ushort> bound)
        {
            int temp = a[i] - b[i];
            if (temp > ushort.MaxValue)
            {
                if (bound[1] == 0)
                    c[i] = ushort.MaxValue;
                else
                    c[i] = (ushort)(MathF.Abs(temp - ushort.MaxValue));
            }
            else
            {
                if (bound[0] > 0)
                    c[i] = (temp < bound[0]) ? (ushort)temp : bound[0];
                else
                    c[i] = (ushort)temp;
            }
        }

        public static void RenderMultiply(Index1D i, ArrayView<ushort> a, ArrayView<ushort> b, ArrayView<ushort> c, ArrayView<ushort> bound)
        {
            uint temp = (uint)(a[i] * b[i]);
            if (temp > ushort.MaxValue)
            {
                if (bound[1] == 0)
                    c[i] = ushort.MaxValue;
                else
                    c[i] = (ushort)(temp - ushort.MaxValue * (ushort.MaxValue / temp));
            }
            else
            {
                if (bound[0] > 0)
                    c[i] = (temp < bound[0]) ? (ushort)temp : bound[0];
                else
                    c[i] = (ushort)temp;
            }
        }

        public static Picture RenderOverlay(Picture a, Picture b, Accelerator accelerator, uint frameIndex)
        {
            if (!a.hasAlphaChannel) return a; //一个图层完全不透明，再去运算完全没有意义
            //Log($"[#{frameIndex:d6}@Mixer] start overlay 2 layer with GPU...");
            b.EnsureAlpha();
            a.EnsureAlpha();
            var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<ushort, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<ushort, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<ushort, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>>(
                (i, a, aAlpha, b, bAlpha, c, cAlpha) =>
                {
                    if (aAlpha[i] == 1f)
                    {
                        c[i] = a[i];
                        cAlpha[i] = 1f;
                    }
                    else if (aAlpha[i] <= 0.05f)
                    {
                        c[i] = b[i];
                        cAlpha[i] = bAlpha[i];
                    }
                    else
                    {
                        float aA = aAlpha[i];
                        float bA = bAlpha[i];
                        float outA = aA + bA * (1 - aA);
                        if (outA < 1e-6f)
                        {
                            c[i] = 0;
                            cAlpha[i] = 0f;
                        }
                        else
                        {
                            float aC = a[i] * aA / outA;
                            float bC = b[i] * bA * (1 - aA) / outA;
                            float outC = aC + bC;
                            if (outC < 0f) outC = 0f;
                            if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                            c[i] = (ushort)outC;
                            cAlpha[i] = outA;
                        }
                    }
                });                   
            
            using MemoryBuffer1D<float, Stride1D.Dense> aAlpha = Allocate1D<float>(a.Pixels,accelerator);            
            using MemoryBuffer1D<float, Stride1D.Dense> bAlpha = Allocate1D<float>(a.Pixels, accelerator);           
            using MemoryBuffer1D<float, Stride1D.Dense> cAlpha = Allocate1D<float>(a.Pixels, accelerator);

            aAlpha.CopyFromCPU(a.a);
            bAlpha.CopyFromCPU(b.a);

            //Log($"[Render #{frameIndex:d6}] Rendering R channel...");

            using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = Allocate1D<ushort>(a.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> bRed = Allocate1D<ushort>(a.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cRed = Allocate1D<ushort>(a.Pixels, accelerator);

            aRed.CopyFromCPU(a.r);
            bRed.CopyFromCPU(b.r);

            LockRun(() => kernel(a.Pixels, aRed.View, aAlpha.View, bRed.View, bAlpha.View, cRed.View, cAlpha.View));
            if (ForceSync) accelerator.Synchronize();

            aRed.Dispose();//避免炸显存
            bRed.Dispose();

            //Log($"[Render #{frameIndex:d6}] Rendering G channel...");

            using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = Allocate1D<ushort>(a.Pixels,accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> bGreen = Allocate1D<ushort>(a.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cGreen = Allocate1D<ushort>(a.Pixels, accelerator);

            aGreen.CopyFromCPU(a.g);
            bGreen.CopyFromCPU(b.g);

            LockRun(() => kernel(a.Pixels, aGreen.View, aAlpha.View, bGreen.View, bAlpha.View, cGreen.View, cAlpha.View));
            if (ForceSync) accelerator.Synchronize();

            aGreen.Dispose();
            bGreen.Dispose();

            //Log($"[Render #{frameIndex:d6}] Rendering B channel...");

            using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = Allocate1D<ushort>(a.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> bBlue = Allocate1D<ushort>(a.Pixels, accelerator);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cBlue = Allocate1D<ushort>(a.Pixels, accelerator);

            aBlue.CopyFromCPU(a.b);
            bBlue.CopyFromCPU(b.b);

            LockRun(() => kernel(a.Pixels, aBlue.View, aAlpha.View, bBlue.View, bAlpha.View, cBlue.View, cAlpha.View));
            if (ForceSync) accelerator.Synchronize();

            aBlue.Dispose();
            bBlue.Dispose();

            var result = new Picture(a)
            {
                Width = a.Width,
                Height = a.Height,
                frameIndex = frameIndex
            };

            cRed.CopyToCPU(result.r = new ushort[a.Pixels]);
            cGreen.CopyToCPU(result.g = new ushort[a.Pixels]);
            cBlue.CopyToCPU(result.b = new ushort[a.Pixels]);
            cAlpha.CopyToCPU(result.a = new float[a.Pixels]);
            //Log($"[Render #{frameIndex:d6}] Rendering overlay done.");
            return result;


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
