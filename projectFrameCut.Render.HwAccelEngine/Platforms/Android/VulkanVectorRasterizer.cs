using System.Diagnostics;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.HwAccelEngine.VectorRasterizer;
using projectFrameCut.Shared;
using Silk.NET.Shaderc;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android
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
        public static IPicture Render(List<GpuPrimitive> primitives, List<float> polygonEdges, int width, int height, bool transparentBg)
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

            // Bin primitives into tiles so each pixel only tests the
            // primitives that overlap its tile instead of every primitive in
            // the scene (turns an O(pixels * primitives) kernel into roughly
            // O(pixels * primitivesPerTile)). Tile offsets/indices are ints,
            // but the compute-view buffers are float-typed, so encode them
            // as floats — values stay well within the 2^24 exact-integer
            // range of a 32-bit float.
            var bins = TileBinner.Build(primitives, width, height);
            var tileOffsetsFloat = ToFloatArray(bins.TileOffsets);
            var tileIndicesFloat = ToFloatArray(bins.TileIndices);
            int tileOffsetsLen = tileOffsetsFloat.Length;
            int tileIndicesLen = tileIndicesFloat.Length;

            // Edge buffer for PolygonFill primitives (4 floats per edge).
            var edgesFloat = polygonEdges.Count > 0 ? polygonEdges.ToArray() : new float[4];

            // VulkanComputeView validates all inputs have the same length.
            // Pad to the maximum needed size to avoid the constraint.
            int vkPad = Math.Max(packedOutputLen,
                Math.Max(primInfoLen, Math.Max(primDataLen,
                Math.Max(edgesFloat.Length, Math.Max(tileOffsetsLen, tileIndicesLen)))));
            var dummyFirst = new float[vkPad];
            var padInfo = PadArray(primInfoFloat, vkPad);
            var padData = PadArray(primDataFloat, vkPad);
            var padEdges = PadArray(edgesFloat, vkPad);
            var padTileOffsets = PadArray(tileOffsetsFloat, vkPad);
            var padTileIndices = PadArray(tileIndicesFloat, vkPad);

            int transparent = transparentBg ? 1 : 0;
            string shader = BuildShader(pc, width, height, transparent, pixelCount, bins.TilesX, bins.TileSize);

            float[] packedOutput;
            try
            {
                packedOutput = VulkanComputerRunner.EnqueueCompute(async () =>
                {
                    var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                        shader,
                        new float[][] { dummyFirst, padInfo, padData, padTileOffsets, padTileIndices, padEdges },
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

            // Parallelize the readback conversion — a single-threaded loop
            // here was a needless bottleneck on larger canvases.
            int alphaFlag = 0;
            Parallel.For(0, pixelCount, i =>
            {
                rOut[i] = ClampUshort(packedOutput[i]);
                gOut[i] = ClampUshort(packedOutput[i + pixelCount]);
                bOut[i] = ClampUshort(packedOutput[i + pixelCount * 2]);
                aOut[i] = packedOutput[i + pixelCount * 3];
                if (aOut[i] < 1f) Volatile.Write(ref alphaFlag, 1);
            });
            bool hasAlpha = alphaFlag != 0;

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
            int pixelCount, int tilesX, int tileSize)
        {
            return $$"""
{{'#'}}version 450
layout(local_size_x = {{WorkGroupSize}}) in;

layout(set = 0, binding = 0, std430) buffer _Dummy { float _d[]; };
layout(set = 0, binding = 1, std430) buffer PrimInfoBuf { float primInfo[]; };
layout(set = 0, binding = 2, std430) buffer PrimDataBuf { float primData[]; };
layout(set = 0, binding = 3, std430) buffer TileOffsetsBuf { float tileOffsets[]; };
layout(set = 0, binding = 4, std430) buffer TileIndicesBuf { float tileIndices[]; };
layout(set = 0, binding = 5, std430) buffer EdgesBuf { float edges[]; };
layout(set = 0, binding = 6, std430) buffer OutBuf { float outPacked[]; };

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint({{pixelCount}}))
        return;

    int x = int(i % uint({{width}}));
    int y = int(i / uint({{width}}));
    float cx = float(x) + 0.5;
    float cy = float(y) + 0.5;

    // Accumulated premultiplied colour + coverage.
    float accR = 0.0, accG = 0.0, accB = 0.0, accA = 0.0;

    // Only test primitives that overlap this pixel's tile instead of every
    // primitive in the scene.
    int tileX = x / {{tileSize}};
    int tileY = y / {{tileSize}};
    int tileIdx = tileY * {{tilesX}} + tileX;
    int tileStart = int(tileOffsets[tileIdx]);
    int tileEnd = int(tileOffsets[tileIdx + 1]);

    // Front-to-back traversal (tile lists are painter's-algorithm ordered,
    // so walk in reverse) with "under" compositing.
    for (int ti = tileEnd - 1; ti >= tileStart; ti--)
    {
        int p = int(tileIndices[ti]);
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

        if (type == 2)
        {
            // Polygon fill — non-zero winding over the edge buffer.
            int edgeStart = int(primData[dataBase + 4]);
            int edgeCount = int(primData[dataBase + 5]);
            int winding = 0;
            for (int e = 0; e < edgeCount; e++)
            {
                int eb = (edgeStart + e) * 4;
                float ex0 = edges[eb + 0];
                float ey0 = edges[eb + 1];
                float ex1 = edges[eb + 2];
                float ey1 = edges[eb + 3];

                if (ey0 < ey1)
                {
                    if (cy >= ey0 && cy < ey1)
                    {
                        float xInt = ex0 + (cy - ey0) * (ex1 - ex0) / (ey1 - ey0);
                        if (cx >= xInt) winding += 1;
                    }
                }
                else if (ey0 > ey1)
                {
                    if (cy >= ey1 && cy < ey0)
                    {
                        float xInt = ex1 + (cy - ey1) * (ex0 - ex1) / (ey0 - ey1);
                        if (cx >= xInt) winding -= 1;
                    }
                }
            }
            covered = winding != 0;
        }
        else if (type == 0)
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

        // "Under" compositing: this primitive is behind everything
        // accumulated so far.
        float contrib = sa * (1.0 - accA);
        accR += sr * contrib;
        accG += sg * contrib;
        accB += sb * contrib;
        accA += contrib;

        // Coverage saturated — nothing below can contribute.
        if (accA >= 1.0 - 1e-6)
            break;
    }

    float pr, pg, pb, pa;
    {{'#'}}if {{transparentBg}}
        if (accA > 1e-6)
        {
            pr = accR / accA; pg = accG / accA; pb = accB / accA;
        }
        else
        {
            pr = 0.0; pg = 0.0; pb = 0.0;
        }
        pa = accA;
    {{'#'}}else
        float rem = 1.0 - accA;
        pr = accR + 65535.0 * rem;
        pg = accG + 65535.0 * rem;
        pb = accB + 65535.0 * rem;
        pa = 1.0;
    {{'#'}}endif

    outPacked[i] = pr;
    outPacked[i + {{pixelCount}}] = pg;
    outPacked[i + {{pixelCount * 2}}] = pb;
    outPacked[i + {{pixelCount * 3}}] = pa;
}
""";
        }

        private static float[] ToFloatArray(int[] src)
        {
            var dst = new float[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = src[i];
            return dst;
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
