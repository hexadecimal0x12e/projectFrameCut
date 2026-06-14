#if WINDOWS
using ILGPU;
using ILGPU.Algorithms;
using ILGPU.Runtime;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Shared;
using System.Diagnostics;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Render.HwAccelEngine.VectorRasterizer.Windows
{
    /// <summary>
    /// ILGPU-based vector rasterizer. Converts flattened GPU primitives to a raster
    /// <see cref="Picture16bpp"/> via a per-pixel compute kernel.
    /// </summary>
    internal static class ILGpuVectorRasterizer
    {
        /// <summary>
        /// Floats per primitive in the flat GPU buffer:
        /// r,g,b,a, d0..d5, bboxMinX, bboxMinY, bboxMaxX, bboxMaxY.
        /// </summary>
        private const int FloatsPerPrimitive = 14;

        /// <summary>Render primitives using the first available ILGPU accelerator.</summary>
        public static IPicture Render(List<GpuPrimitive> primitives, int width, int height, bool transparentBg)
        {
            Logger.LogDiagnostic($"Ready to start GPU rasterization with {primitives.Count} primitives at {width}x{height} resolution.");
            var sw = Stopwatch.StartNew();
            var accel = PickAccelerator();
            if (accel == null)
                throw new InvalidOperationException("No ILGPU accelerator available for vector rasterization.");

            int pc = primitives.Count;
            int pixels = width * height;

            // --- Flatten primitives to GPU buffer ---
            // Layout per primitive: 2 ints (type, layer) + 14 floats
            // (r,g,b,a, d0..d5, bboxMinX, bboxMinY, bboxMaxX, bboxMaxY)
            var primInfo = new int[pc * 2];
            var primData = new float[pc * FloatsPerPrimitive];

            for (int i = 0; i < pc; i++)
            {
                var p = primitives[i];
                primInfo[i * 2 + 0] = p.Type;
                primInfo[i * 2 + 1] = p.Layer;
                primData[i * FloatsPerPrimitive + 0] = p.R;
                primData[i * FloatsPerPrimitive + 1] = p.G;
                primData[i * FloatsPerPrimitive + 2] = p.B;
                primData[i * FloatsPerPrimitive + 3] = p.A;
                primData[i * FloatsPerPrimitive + 4] = p.D0;
                primData[i * FloatsPerPrimitive + 5] = p.D1;
                primData[i * FloatsPerPrimitive + 6] = p.D2;
                primData[i * FloatsPerPrimitive + 7] = p.D3;
                primData[i * FloatsPerPrimitive + 8] = p.D4;
                primData[i * FloatsPerPrimitive + 9] = p.D5;
                primData[i * FloatsPerPrimitive + 10] = p.BBoxMinX;
                primData[i * FloatsPerPrimitive + 11] = p.BBoxMinY;
                primData[i * FloatsPerPrimitive + 12] = p.BBoxMaxX;
                primData[i * FloatsPerPrimitive + 13] = p.BBoxMaxY;
            }

            // --- Allocate GPU buffers ---
            using var dPrimInfo = accel.Allocate1D(primInfo);
            using var dPrimData = accel.Allocate1D(primData);

            using var dOutR = accel.Allocate1D<float>(pixels);
            using var dOutG = accel.Allocate1D<float>(pixels);
            using var dOutB = accel.Allocate1D<float>(pixels);
            using var dOutA = accel.Allocate1D<float>(pixels);

            // --- Load & launch kernel ---
            var kernel = GetOrCreateKernel(accel);
            int transparent = transparentBg ? 1 : 0;

            bool syncNeeded = accel.AcceleratorType == AcceleratorType.OpenCL;
            if (syncNeeded)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernel(pixels, dPrimInfo.View, dPrimData.View, pc,
                           width, height, transparent,
                           dOutR.View, dOutG.View, dOutB.View, dOutA.View);
                    accel.Synchronize();
                }
            }
            else
            {
                kernel(pixels, dPrimInfo.View, dPrimData.View, pc,
                       width, height, transparent,
                       dOutR.View, dOutG.View, dOutB.View, dOutA.View);
            }

            // --- Read back ---
            var rFloat = dOutR.GetAsArray1D();
            var gFloat = dOutG.GetAsArray1D();
            var bFloat = dOutB.GetAsArray1D();
            var aFloat = dOutA.GetAsArray1D();

            // --- Convert float[0..65535] back to ushort ---
            var rOut = new ushort[pixels];
            var gOut = new ushort[pixels];
            var bOut = new ushort[pixels];
            var aOut = new float[pixels];

            var hasAlpha = false;
            for (int i = 0; i < pixels; i++)
            {
                rOut[i] = ClampUshort(rFloat[i]);
                gOut[i] = ClampUshort(gFloat[i]);
                bOut[i] = ClampUshort(bFloat[i]);
                aOut[i] = aFloat[i];
                if (aOut[i] < 1f) hasAlpha = true;
            }
            Logger.LogDiagnostic($"Rasterization operation done within {sw.Elapsed}.");

            return new Picture16bpp(width, height)
            {
                r = rOut,
                g = gOut,
                b = bOut,
                a = hasAlpha ? aOut : null,
                HasAlphaChannel = hasAlpha,
            };
        }

        // ---------------------------------------------------------------
        // Kernel
        // ---------------------------------------------------------------

        /// <summary>
        /// Once a pixel accumulates this much opacity we stop processing
        /// further primitives for it. Anything drawn on top of an opaque
        /// pixel contributes nothing, so the work is wasted.
        /// </summary>
        private const float AlphaSaturatedEpsilon = 1e-6f;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator,
            Action<Index1D,
                ArrayView<int>, ArrayView<float>, int,
                int, int, int,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>>
            KernelCache = new();

        private static Action<Index1D,
            ArrayView<int>, ArrayView<float>, int,
            int, int, int,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>
        GetOrCreateKernel(Accelerator accel)
        {
            return KernelCache.GetOrAdd(accel, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<int> primInfo,   // 2 ints per primitive: type, layer
                    ArrayView<float> primData, // 14 floats per primitive: r,g,b,a, d0..d5, bbox
                    int primCount,
                    int w, int h,
                    int transparentBg,
                    ArrayView<float> outR,
                    ArrayView<float> outG,
                    ArrayView<float> outB,
                    ArrayView<float> outA) =>
                {
                    int x = i % w;
                    int y = i / w;
                    float cx = x + 0.5f;
                    float cy = y + 0.5f;

                    // Initialize background
                    float pr, pg, pb, pa;
                    if (transparentBg != 0)
                    {
                        pr = 0f; pg = 0f; pb = 0f; pa = 0f;
                    }
                    else
                    {
                        pr = 65535f; pg = 65535f; pb = 65535f; pa = 1f;
                    }

                    // Iterate over primitives (pre-sorted by layer on CPU)
                    for (int p = 0; p < primCount; p++)
                    {
                        int type = primInfo[p * 2 + 0];
                        // layer = primInfo[p * 2 + 1]; // not needed — pre-sorted

                        int dataBase = p * 14;

                        float sr = primData[dataBase + 0];
                        float sg = primData[dataBase + 1];
                        float sb = primData[dataBase + 2];
                        float sa = primData[dataBase + 3];

                        if (sa <= 0f) continue;

                        // Cheap per-primitive bounding-box cull. Primitives
                        // that don't cover the current pixel are skipped
                        // before we run the (much more expensive) full
                        // coverage test.
                        float bbMinX = primData[dataBase + 10];
                        float bbMinY = primData[dataBase + 11];
                        float bbMaxX = primData[dataBase + 12];
                        float bbMaxY = primData[dataBase + 13];
                        if (cx < bbMinX || cx > bbMaxX || cy < bbMinY || cy > bbMaxY)
                            continue;

                        bool covered = false;

                        if (type == 0)
                        {
                            // ---- Triangle fill ----
                            float v0x = primData[dataBase + 4];
                            float v0y = primData[dataBase + 5];
                            float v1x = primData[dataBase + 6];
                            float v1y = primData[dataBase + 7];
                            float v2x = primData[dataBase + 8];
                            float v2y = primData[dataBase + 9];

                            // Barycentric test (works for both CW and CCW)
                            float dX = cx - v0x, dY = cy - v0y;
                            float dX1 = v1x - v0x, dY1 = v1y - v0y;
                            float dX2 = v2x - v0x, dY2 = v2y - v0y;
                            float dot00 = dX1 * dX1 + dY1 * dY1;
                            float dot01 = dX1 * dX2 + dY1 * dY2;
                            float dot02 = dX1 * dX + dY1 * dY;
                            float dot11 = dX2 * dX2 + dY2 * dY2;
                            float dot12 = dX2 * dX + dY2 * dY;
                            float invDen = dot00 * dot11 - dot01 * dot01;
                            if (invDen != 0f)
                            {
                                float invDet = 1.0f / invDen;
                                float u = (dot11 * dot02 - dot01 * dot12) * invDet;
                                float v = (dot00 * dot12 - dot01 * dot02) * invDet;
                                covered = (u >= 0f && v >= 0f && u + v <= 1f);
                            }
                        }
                        else if (type == 1)
                        {
                            // ---- Stroke line ----
                            float x0 = primData[dataBase + 4];
                            float y0 = primData[dataBase + 5];
                            float x1 = primData[dataBase + 6];
                            float y1 = primData[dataBase + 7];
                            float thickness = primData[dataBase + 8];

                            float dx = x1 - x0;
                            float dy = y1 - y0;
                            float lenSq = dx * dx + dy * dy;
                            if (lenSq >= 1e-6f)
                            {
                                float t = ((cx - x0) * dx + (cy - y0) * dy) / lenSq;
                                if (t < 0f) t = 0f;
                                else if (t > 1f) t = 1f;

                                float nx = x0 + t * dx;
                                float ny = y0 + t * dy;
                                // Use ILGPU's intrinsic sqrt to avoid falling
                                // back to a host-side MathF.Sqrt call.
                                float dist = MathF.Sqrt((cx - nx) * (cx - nx) + (cy - ny) * (cy - ny));
                                covered = dist <= thickness * 0.5f;
                            }
                        }

                        if (!covered) continue;

                        // Alpha blend (over operator)
                        float blendA = pa + sa * (1f - pa);
                        if (blendA > 1e-6f)
                        {
                            pr = (sr * sa + pr * pa * (1f - sa)) / blendA;
                            pg = (sg * sa + pg * pa * (1f - sa)) / blendA;
                            pb = (sb * sa + pb * pa * (1f - sa)) / blendA;
                        }
                        pa = blendA;

                        // Early-out: once a pixel is fully opaque, no
                        // primitive drawn afterwards can change its colour.
                        if (pa >= 1f - AlphaSaturatedEpsilon) break;
                    }

                    outR[i] = pr;
                    outG[i] = pg;
                    outB[i] = pb;
                    outA[i] = pa;
                }));
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        /// <summary>Pick a non-CPU accelerator, preferring CUDA over others.</summary>
        private static Accelerator? PickAccelerator()
        {
            var all = HwAccelEnginePlugin.accelerators;
            if (all == null || all.Length == 0) return null;

            // Prefer CUDA, fall back to any non-CPU accelerator
            Accelerator? fallback = null;
            foreach (var a in all)
            {
                if (a.AcceleratorType == AcceleratorType.Cuda)
                    return a;
                if (a.AcceleratorType != AcceleratorType.CPU)
                    fallback = a;
            }
            return fallback;
        }

        private static ushort ClampUshort(float v)
        {
            if (v < 0f) return 0;
            if (v > 65535f) return 65535;
            return (ushort)v;
        }
    }
}
#endif
