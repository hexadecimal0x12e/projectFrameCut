using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Shared;
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
#pragma warning disable CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。
using ilgpu = global::ILGPU;
#pragma warning restore CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。

namespace projectFrameCut.Render.ILGpu
{
    public static class Render
    {


        public static Picture RenderOneFrame(Accelerator accelerator, RenderMode mode, Picture layerA, Picture layerB, ushort upperBond = ushort.MaxValue, bool allowOverflow = false, object? extend = null, uint frameIndex = 0)
        {
            try
            {
                //Console.WriteLine($"[Render #{frameIndex:d6}] Starting rendering with effect {mode}...");
                Console.WriteLine($"[#{frameIndex:d6}@Render] start rendering {mode} with GPU...");

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
                    case RenderMode.RemoveColor:
                        if (extend is System.Drawing.Color)
                            return RenderRemoveColor(layerA, (System.Drawing.Color)extend, upperBond, accelerator,frameIndex); //同上
                        throw new ArgumentException("extend must be System.Drawing.Color when mode is RemoveColor");
                    default:
                        throw new NotSupportedException();
                }


                using MemoryBuffer1D<ushort, Stride1D.Dense> bound = accelerator.Allocate1D<ushort>(2);
                bound.CopyFromCPU([upperBond, allowOverflow ? ushort.MinValue : ushort.MaxValue]);
                //0:上限，0代表不启用
                //1:允许溢出，0代表不启用

                using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = accelerator.Allocate1D<ushort>(layerB.Pixels);
                using MemoryBuffer1D<ushort, Stride1D.Dense> bRed = accelerator.Allocate1D<ushort>(layerB.Pixels);
                using MemoryBuffer1D<ushort, Stride1D.Dense> cRed = accelerator.Allocate1D<ushort>(layerB.Pixels);
                aRed.CopyFromCPU(layerA.r);
                bRed.CopyFromCPU(layerB.r);
                kernel(layerA.Pixels, aRed.View, bRed.View, cRed.View, bound.View);
                aRed.Dispose();
                bRed.Dispose();

                //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering G channel...");

                using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = accelerator.Allocate1D<ushort>(layerB.Pixels);
                using MemoryBuffer1D<ushort, Stride1D.Dense> bGreen = accelerator.Allocate1D<ushort>(layerB.Pixels);
                using MemoryBuffer1D<ushort, Stride1D.Dense> cGreen = accelerator.Allocate1D<ushort>(layerB.Pixels);
                aGreen.CopyFromCPU(layerA.g);
                bGreen.CopyFromCPU(layerB.g);
                kernel(layerA.Pixels, aGreen.View, bGreen.View, cGreen.View, bound.View);
                aGreen.Dispose();
                bGreen.Dispose();

                //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering B channel...");

                using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = accelerator.Allocate1D<ushort>(layerB.Pixels);
                using MemoryBuffer1D<ushort, Stride1D.Dense> bBlue = accelerator.Allocate1D<ushort>(layerB.Pixels);
                using MemoryBuffer1D<ushort, Stride1D.Dense> cBlue = accelerator.Allocate1D<ushort>(layerB.Pixels);
                aBlue.CopyFromCPU(layerA.b);
                bBlue.CopyFromCPU(layerB.b);
                kernel(layerA.Pixels, aBlue.View, bBlue.View, cBlue.View, bound.View);
                aBlue.Dispose();
                bBlue.Dispose();

                var result = new Picture(layerA)
                {
                    Width = layerA.Width,
                    Height = layerA.Height
                };

                cRed.CopyToCPU(result.r = new ushort[layerA.Pixels]);
                cGreen.CopyToCPU(result.g = new ushort[layerA.Pixels]);
                cBlue.CopyToCPU(result.b = new ushort[layerA.Pixels]);
                result.a = layerA.a; //保留原图的Alpha通道
                //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering done.");

                return result;
            }
            catch (ilgpu.Runtime.Cuda.CudaException cudaEx)
            {
                if(cudaEx.Message.Contains("out of memory", StringComparison.CurrentCultureIgnoreCase))
                {
                    throw new OutOfMemoryException("VRAM is not enough to render this frame. Try close all programs use GPU, reboot your device, use another render accelerator, or upgrade your GPU or use CPU to render (not recommend).", cudaEx);
                }
            }
            catch (ilgpu.Runtime.OpenCL.CLException ex)
            {
                throw new OutOfMemoryException("An error happens during render, probably the VRAM is not enough to render this frame. Try close all programs use GPU, reboot your device, use another render accelerator, or upgrade your GPU or use CPU to render (not recommend).", ex);
            }
            catch
            {
                throw;
            }


            return new Picture(layerA);

        }



        public static Picture MapAlphaAndUpperBond(this Picture picture, Accelerator accelerator, ushort upperBond = ushort.MaxValue)
        {
            using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = accelerator.Allocate1D<ushort>(picture.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = accelerator.Allocate1D<ushort>(picture.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = accelerator.Allocate1D<ushort>(picture.Pixels);
            using MemoryBuffer1D<float, Stride1D.Dense> aAlpha = accelerator.Allocate1D<float>(picture.Pixels);

            using MemoryBuffer1D<ushort, Stride1D.Dense> cRed = accelerator.Allocate1D<ushort>(picture.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cGreen = accelerator.Allocate1D<ushort>(picture.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cBlue = accelerator.Allocate1D<ushort>(picture.Pixels);

            using MemoryBuffer1D<ushort, Stride1D.Dense> bound = accelerator.Allocate1D<ushort>(1);

            var kernel = accelerator.LoadAutoGroupedStreamKernel<
                    Index1D, ArrayView<ushort>, ArrayView<float>, ArrayView<ushort>, ArrayView<ushort>>( static (i, a, alpha, c,b) =>
                    {
                        c[i] = a[i] > b[0] ? b[0] : (ushort)(a[i] * alpha[i]);
                    });

            aAlpha.CopyFromCPU(picture.a);
            bound.CopyFromCPU([upperBond]);

            aRed.CopyFromCPU(picture.r);
            kernel(picture.Pixels, aRed.View, aAlpha.View, cRed.View,bound.View);
            aRed.Dispose();

            aGreen.CopyFromCPU(picture.g);
            kernel(picture.Pixels, aGreen.View, aAlpha.View, cGreen.View, bound.View);
            aGreen.Dispose();   

            aBlue.CopyFromCPU(picture.b);
            kernel(picture.Pixels, aBlue.View, aAlpha.View, cBlue.View, bound.View);
            aBlue.Dispose();

            var result = new Picture(picture)
            {
                Width = picture.Width,
                Height = picture.Height,
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
            
            using MemoryBuffer1D<float, Stride1D.Dense> aAlpha = accelerator.Allocate1D<float>(a.Pixels);            
            using MemoryBuffer1D<float, Stride1D.Dense> bAlpha = accelerator.Allocate1D<float>(b.Pixels);           
            using MemoryBuffer1D<float, Stride1D.Dense> cAlpha = accelerator.Allocate1D<float>(a.Pixels);

            aAlpha.CopyFromCPU(a.a);
            bAlpha.CopyFromCPU(b.a);

            //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering R channel...");

            using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = accelerator.Allocate1D<ushort>(a.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> bRed = accelerator.Allocate1D<ushort>(b.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cRed = accelerator.Allocate1D<ushort>(a.Pixels);

            aRed.CopyFromCPU(a.r);
            bRed.CopyFromCPU(b.r);

            kernel(a.Pixels, aRed.View, aAlpha.View, bRed.View, bAlpha.View, cRed.View, cAlpha.View);

            aRed.Dispose();//避免炸显存
            bRed.Dispose();

            //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering G channel...");

            using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = accelerator.Allocate1D<ushort>(a.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> bGreen = accelerator.Allocate1D<ushort>(b.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cGreen = accelerator.Allocate1D<ushort>(a.Pixels);

            aGreen.CopyFromCPU(a.g);
            bGreen.CopyFromCPU(b.g);

            kernel(a.Pixels, aGreen.View, aAlpha.View, bGreen.View, bAlpha.View, cGreen.View, cAlpha.View);

            aGreen.Dispose();
            bGreen.Dispose();

            //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering B channel...");

            using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = accelerator.Allocate1D<ushort>(a.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> bBlue = accelerator.Allocate1D<ushort>(b.Pixels);
            using MemoryBuffer1D<ushort, Stride1D.Dense> cBlue = accelerator.Allocate1D<ushort>(a.Pixels);

            aBlue.CopyFromCPU(a.b);
            bBlue.CopyFromCPU(b.b);

            kernel(a.Pixels, aBlue.View, aAlpha.View, bBlue.View, bAlpha.View, cBlue.View, cAlpha.View);

            aBlue.Dispose();
            bBlue.Dispose();

            var result = new Picture(a)
            {
                Width = a.Width,
                Height = a.Height
            };

            cRed.CopyToCPU(result.r = new ushort[a.Pixels]);
            cGreen.CopyToCPU(result.g = new ushort[a.Pixels]);
            cBlue.CopyToCPU(result.b = new ushort[a.Pixels]);
            cAlpha.CopyToCPU(result.a = new float[a.Pixels]);
            //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering overlay done.");
            return result;


        }

        public static Picture RenderRemoveColor(Picture a, System.Drawing.Color color, ulong range, Accelerator accelerator, uint frameIndex)
        {

            var kernelR = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,                             //i 
                ArrayView1D<ushort, Stride1D.Dense>, //r
                ArrayView1D<float,Stride1D.Dense>,   //a
                ArrayView1D<ushort, Stride1D.Dense>, //lowR
                ArrayView1D<ushort, Stride1D.Dense>, //highR
                ArrayView1D<float, Stride1D.Dense>   //c
                >(static
                (i,r,a,lowR,highR,c) =>
                {
                    if (lowR[0]  <= r[i] && r[i] <= highR[0] )
                       
                            {
                                c[i] = 0f;
                                return;
                            }
                    c[i] = a[i];

                });

            var kernelG = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,                             //i 
                ArrayView1D<ushort, Stride1D.Dense>, //g
                ArrayView1D<float, Stride1D.Dense>,   //a
                ArrayView1D<ushort, Stride1D.Dense>, //lowG
                ArrayView1D<ushort, Stride1D.Dense>, //highG
                ArrayView1D<float, Stride1D.Dense>   //c
                >(static
                (i, g,  a,  lowG,  highG,  c) =>
                {
                        if (lowG[0] <= g[i] && g[i] <= highG[0])
                            {
                                c[i] = 0f;
                                return;
                            }
                    c[i] = a[i];

                });

            var kernelB = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,                             //i 
                ArrayView1D<ushort, Stride1D.Dense>, //b
                ArrayView1D<float, Stride1D.Dense>,   //a
                ArrayView1D<ushort, Stride1D.Dense>, //lowB
                ArrayView1D<ushort, Stride1D.Dense>, //highB
                ArrayView1D<float, Stride1D.Dense>   //c
                >(static
                (i,  b, a,  lowB, highB, c) =>
                {

                            if (lowB[0] <= b[i] && b[i] <= highB[0])
                            {
                                c[i] = 0f;
                                return;
                            }
                    c[i] = a[i];

                });

            //Console.WriteLine($"[Render #{frameIndex:d6}] Loading data...");

            

            using MemoryBuffer1D<ushort, Stride1D.Dense> lowRed = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> lowGreen = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> lowBlue = accelerator.Allocate1D<ushort>(1);

            using MemoryBuffer1D<ushort, Stride1D.Dense> highRed = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> highGreen = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> highBlue = accelerator.Allocate1D<ushort>(1);

            using MemoryBuffer1D<float, Stride1D.Dense> cAlphaR = accelerator.Allocate1D<float>(a.Pixels);
            using MemoryBuffer1D<float, Stride1D.Dense> cAlphaG = accelerator.Allocate1D<float>(a.Pixels);
            using MemoryBuffer1D<float, Stride1D.Dense> cAlphaB = accelerator.Allocate1D<float>(a.Pixels);

            lowRed.CopyFromCPU([(ushort)((color.R * 257) - (ushort)range)]); //把8bit颜色转换到16bit
            lowGreen.CopyFromCPU([(ushort)((color.G * 257) - (ushort)range)]);
            lowBlue.CopyFromCPU([(ushort)((color.B * 257) - (ushort)range)]);

            highRed.CopyFromCPU([(ushort)((color.R * 257) + (ushort)range)]);
            highGreen.CopyFromCPU([(ushort)((color.G * 257) + (ushort)range)]);
            highBlue.CopyFromCPU([(ushort)((color.B * 257) + (ushort)range)]);

            cAlphaR.CopyFromCPU(new float[a.Pixels]);
            cAlphaG.CopyFromCPU(new float[a.Pixels]);
            cAlphaB.CopyFromCPU(new float[a.Pixels]);

            using MemoryBuffer1D<float, Stride1D.Dense> aAlpha = accelerator.Allocate1D<float>(a.Pixels);
            aAlpha.CopyFromCPU(a.a);

            using MemoryBuffer1D<ushort, Stride1D.Dense> aRed = accelerator.Allocate1D<ushort>(a.Pixels);
            aRed.CopyFromCPU(a.r);
            kernelR(a.Pixels,
                   aRed.View,
                   aAlpha.View,
                   lowRed.View,
                   highRed.View,
                   cAlphaR.View);
            aRed.Dispose();
            lowRed.Dispose();
            highRed.Dispose();

            using MemoryBuffer1D<ushort, Stride1D.Dense> aGreen = accelerator.Allocate1D<ushort>(a.Pixels);
            aGreen.CopyFromCPU(a.g);
            kernelG(a.Pixels,
                   aGreen.View,
                   aAlpha.View,
                   lowGreen.View,
                   highGreen.View,
                   cAlphaG.View);
            aGreen.Dispose();
            highGreen.Dispose();
            lowGreen.Dispose();

            using MemoryBuffer1D<ushort, Stride1D.Dense> aBlue = accelerator.Allocate1D<ushort>(a.Pixels);
            aBlue.CopyFromCPU(a.b);
            kernelB(a.Pixels,
                   aBlue.View,
                   aAlpha.View,
                   lowBlue.View,
                   highBlue.View,
                   cAlphaB.View);
            aBlue.Dispose();
            highBlue.Dispose();
            lowBlue.Dispose();


            float[] AlphaR = new float[a.Pixels], AlphaG = new float[a.Pixels], AlphaB = new float[a.Pixels], alpha = new float[a.Pixels];
            cAlphaR.CopyToCPU(AlphaR);
            cAlphaG.CopyToCPU(AlphaG);
            cAlphaB.CopyToCPU(AlphaB);

            for (int i = 0; i < a.Pixels; i++)
            {
                alpha[i] = (AlphaR[i] + AlphaG[i] + AlphaB[i]) / 3;
            }

            var result = new Picture(a)
            {
                r = a.r,
                g = a.g,
                b = a.b,
                a = alpha,
                hasAlphaChannel = true
            };
            
            ////Console.WriteLine($"{result.a.Count((f) => f <= 0.01f)} is almost transept, total {result.Pixels}, pctg:{result.a.Count((f) => f <= 0.01f) / result.Pixels:p2}");
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
            //Console.WriteLine($"[Render #{frameIndex:d6}] Rendering RemoveColor done.");

            return result;
        }
    }

    

}
