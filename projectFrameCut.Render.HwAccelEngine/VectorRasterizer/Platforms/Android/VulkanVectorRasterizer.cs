#if ANDROID
using System.Diagnostics;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;
using projectFrameCut.Shared;
using Silk.NET.Shaderc;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Render.HwAccelEngine.VectorRasterizer.Platforms.Android
{
    /// <summary>
    /// Vulkan compute-shader vector rasterizer.
    /// Converts flattened GPU primitives to a <see cref="Picture16bpp"/>
    /// via a per-pixel compute kernel dispatched through <see cref="VulkanComputeView"/>.
    /// </summary>
    internal static class VulkanVectorRasterizer
    {
        private const int FloatsPerPrimitive = 14;
        private const int WorkGroupSize = 256;

        /// <summary>Render primitives using Vulkan compute pipeline.</summary>
        public static IPicture Render(List<GpuPrimitive> primitives, int width, int height, bool transparentBg)
        {
            int pc = primitives.Count;
            int pixelCount = width * height;
            if (pixelCount <= 0 || pc == 0)
                throw new ArgumentException("Invalid render parameters: zero pixels or zero primitives.");

            Logger.LogDiagnostic(
                $"VulkanVectorRasterizer: {pc} primitives, {width}x{height} pixels.");

            var sw = Stopwatch.StartNew();

            int primInfoLen = pc * 2;
            int primDataLen = pc * FloatsPerPrimitive;
            int packedOutputLen = pixelCount * 4;

            var primInfoFloat = new float[primInfoLen];
            var primDataFloat = new float[primDataLen];

            for (int i = 0; i < pc; i++)
            {
                var p = primitives[i];
                primInfoFloat[i * 2 + 0] = p.Type;
                primInfoFloat[i * 2 + 1] = p.Layer;
                int db = i * FloatsPerPrimitive;
                primDataFloat[db + 0] = p.R;
                primDataFloat[db + 1] = p.G;
                primDataFloat[db + 2] = p.B;
                primDataFloat[db + 3] = p.A;
                primDataFloat[db + 4] = p.D0;
                primDataFloat[db + 5] = p.D1;
                primDataFloat[db + 6] = p.D2;
                primDataFloat[db + 7] = p.D3;
                primDataFloat[db + 8] = p.D4;
                primDataFloat[db + 9] = p.D5;
                primDataFloat[db + 10] = p.BBoxMinX;
                primDataFloat[db + 11] = p.BBoxMinY;
                primDataFloat[db + 12] = p.BBoxMaxX;
                primDataFloat[db + 13] = p.BBoxMaxY;
            }

            // VulkanComputeView validates all inputs have the same length.
            // Pad to the maximum needed size to avoid the constraint.
            int vkPad = Math.Max(packedOutputLen, Math.Max(primInfoLen, primDataLen));
            var dummyFirst = new float[vkPad];
            var padInfo = PadArray(primInfoFloat, vkPad);
            var padData = PadArray(primDataFloat, vkPad);

            int transparent = transparentBg ? 1 : 0;
            string shader = BuildShader(pc, width, height, transparent, pixelCount);

            float[] packedOutput;
            try
            {
                packedOutput = VulkanComputerRunner.EnqueueCompute(async () =>
                {
                    var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                        shader,
                        new float[][] { dummyFirst, padInfo, padData },
                        GLComputeView.OutputElementType.Float32);

                    var raw = (float[])await vkView.RunComputeAsync();
                    return new object[] { raw };
                }, "VulkanVectorRasterizer timed out after 60 seconds.")[0] as float[]
                    ?? throw new InvalidOperationException("VulkanVectorRasterizer returned null.");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "VulkanVectorRasterizer.Render");
                throw;
            }

            var rOut = new ushort[pixelCount];
            var gOut = new ushort[pixelCount];
            var bOut = new ushort[pixelCount];
            var aOut = new float[pixelCount];
            bool hasAlpha = false;

            for (int i = 0; i < pixelCount; i++)
            {
                rOut[i] = ClampUshort(packedOutput[i]);
                gOut[i] = ClampUshort(packedOutput[i + pixelCount]);
                bOut[i] = ClampUshort(packedOutput[i + pixelCount * 2]);
                aOut[i] = packedOutput[i + pixelCount * 3];
                if (aOut[i] < 1f) hasAlpha = true;
            }

            Logger.LogDiagnostic($"VulkanVectorRasterizer done in {sw.Elapsed}.");
            return new Picture16bpp(width, height)
            {
                r = rOut,
                g = gOut,
                b = bOut,
                a = hasAlpha ? aOut : null,
                HasAlphaChannel = hasAlpha,
            };
        }

        private static string BuildShader(int primCount, int width, int height, int transparentBg,
            int pixelCount)
        {
            return $$"""
#version 450
layout(local_size_x = {{WorkGroupSize}}) in;

layout(set = 0, binding = 0, std430) buffer _Dummy { float _d[]; };
layout(set = 0, binding = 1, std430) buffer PrimInfoBuf { float primInfo[]; };
layout(set = 0, binding = 2, std430) buffer PrimDataBuf { float primData[]; };
layout(set = 0, binding = 3, std430) buffer OutBuf { float outPacked[]; };

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint({{pixelCount}}))
        return;

    int x = int(i % uint({{width}}));
    int y = int(i / uint({{width}}));
    float cx = float(x) + 0.5;
    float cy = float(y) + 0.5;

    float pr, pg, pb, pa;
    #if {{transparentBg}}
        pr = 0.0; pg = 0.0; pb = 0.0; pa = 0.0;
    #else
        pr = 65535.0; pg = 65535.0; pb = 65535.0; pa = 1.0;
    #endif

    for (int p = 0; p < {{primCount}}; p++)
    {
        int type = int(primInfo[p * 2]);
        int dataBase = p * {{FloatsPerPrimitive}};

        float sr = primData[dataBase + 0];
        float sg = primData[dataBase + 1];
        float sb = primData[dataBase + 2];
        float sa = primData[dataBase + 3];
        if (sa <= 0.0)
            continue;

        float bbMinX = primData[dataBase + 10];
        float bbMinY = primData[dataBase + 11];
        float bbMaxX = primData[dataBase + 12];
        float bbMaxY = primData[dataBase + 13];
        if (cx < bbMinX || cx > bbMaxX || cy < bbMinY || cy > bbMaxY)
            continue;

        bool covered = false;

        if (type == 0)
        {
            float v0x = primData[dataBase + 4];
            float v0y = primData[dataBase + 5];
            float v1x = primData[dataBase + 6];
            float v1y = primData[dataBase + 7];
            float v2x = primData[dataBase + 8];
            float v2y = primData[dataBase + 9];

            float dX = cx - v0x, dY = cy - v0y;
            float dX1 = v1x - v0x, dY1 = v1y - v0y;
            float dX2 = v2x - v0x, dY2 = v2y - v0y;
            float dot00 = dX1 * dX1 + dY1 * dY1;
            float dot01 = dX1 * dX2 + dY1 * dY2;
            float dot02 = dX1 * dX + dY1 * dY;
            float dot11 = dX2 * dX2 + dY2 * dY2;
            float dot12 = dX2 * dX + dY2 * dY;
            float invDen = dot00 * dot11 - dot01 * dot01;
            if (invDen != 0.0)
            {
                float invDet = 1.0 / invDen;
                float u = (dot11 * dot02 - dot01 * dot12) * invDet;
                float v = (dot00 * dot12 - dot01 * dot02) * invDet;
                covered = u >= 0.0 && v >= 0.0 && u + v <= 1.0;
            }
        }
        else if (type == 1)
        {
            float x0 = primData[dataBase + 4];
            float y0 = primData[dataBase + 5];
            float x1 = primData[dataBase + 6];
            float y1 = primData[dataBase + 7];
            float thickness = primData[dataBase + 8];

            float dx = x1 - x0;
            float dy = y1 - y0;
            float lenSq = dx * dx + dy * dy;
            if (lenSq >= 1e-6)
            {
                float t = ((cx - x0) * dx + (cy - y0) * dy) / lenSq;
                t = clamp(t, 0.0, 1.0);
                float nx = x0 + t * dx;
                float ny = y0 + t * dy;
                float dist = sqrt((cx - nx) * (cx - nx) + (cy - ny) * (cy - ny));
                covered = dist <= thickness * 0.5;
            }
        }

        if (!covered)
            continue;

        float blendA = pa + sa * (1.0 - pa);
        if (blendA > 1e-6)
        {
            pr = (sr * sa + pr * pa * (1.0 - sa)) / blendA;
            pg = (sg * sa + pg * pa * (1.0 - sa)) / blendA;
            pb = (sb * sa + pb * pa * (1.0 - sa)) / blendA;
        }
        pa = blendA;

        if (pa >= 1.0 - 1e-6)
            break;
    }

    outPacked[i] = pr;
    outPacked[i + {{pixelCount}}] = pg;
    outPacked[i + {{pixelCount * 2}}] = pb;
    outPacked[i + {{pixelCount * 3}}] = pa;
}
""";
        }

        private static float[] PadArray(float[] src, int targetLen)
        {
            if (src.Length >= targetLen) return src;
            var padded = new float[targetLen];
            Array.Copy(src, 0, padded, 0, src.Length);
            return padded;
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
