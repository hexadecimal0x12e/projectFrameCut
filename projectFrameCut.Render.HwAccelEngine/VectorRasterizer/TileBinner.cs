namespace projectFrameCut.Render.HwAccelEngine.VectorRasterizer
{
    /// <summary>
    /// Bins primitives into a uniform grid of tiles so that per-pixel GPU kernels
    /// only need to test the primitives that actually overlap the tile containing
    /// the pixel, instead of scanning every primitive in the scene on every pixel.
    /// This reduces the kernel's complexity from O(pixels * primitives) to
    /// roughly O(pixels * primitivesPerTile), which is what makes the CPU scanline
    /// rasterizer so much faster than a naive brute-force GPU kernel when a scene
    /// has many small, spatially-localized shapes.
    /// </summary>
    internal static class TileBinner
    {
        /// <summary>Default tile edge length in pixels. Small enough to keep per-tile
        /// primitive lists short, large enough to keep the tile count (and thus the
        /// binning overhead) reasonable for typical canvas sizes.</summary>
        public const int DefaultTileSize = 32;

        /// <summary>Compressed sparse row (CSR) representation of the tile → primitive-index mapping.</summary>
        public readonly struct TileBinResult
        {
            /// <summary>Offsets into <see cref="TileIndices"/>, length TilesX * TilesY + 1.</summary>
            public readonly int[] TileOffsets;

            /// <summary>Flat list of primitive indices per tile, in original (painter's-algorithm) order.</summary>
            public readonly int[] TileIndices;

            public readonly int TilesX;
            public readonly int TilesY;
            public readonly int TileSize;

            public TileBinResult(int[] tileOffsets, int[] tileIndices, int tilesX, int tilesY, int tileSize)
            {
                TileOffsets = tileOffsets;
                TileIndices = tileIndices;
                TilesX = tilesX;
                TilesY = tilesY;
                TileSize = tileSize;
            }
        }

        /// <summary>Bin the given primitives' bounding boxes into a tile grid covering width x height.</summary>
        public static TileBinResult Build(List<GpuPrimitive> primitives, int width, int height, int tileSize = DefaultTileSize)
        {
            int tilesX = Math.Max(1, (width + tileSize - 1) / tileSize);
            int tilesY = Math.Max(1, (height + tileSize - 1) / tileSize);
            int tileCount = tilesX * tilesY;
            int pc = primitives.Count;

            var minTx = new int[pc];
            var minTy = new int[pc];
            var maxTx = new int[pc];
            var maxTy = new int[pc];
            var counts = new int[tileCount];

            // Pass 1: for every primitive, compute the tile range its bounding
            // box overlaps and accumulate per-tile counts.
            for (int i = 0; i < pc; i++)
            {
                var p = primitives[i];
                int tx0 = Math.Clamp((int)MathF.Floor(p.BBoxMinX / tileSize), 0, tilesX - 1);
                int ty0 = Math.Clamp((int)MathF.Floor(p.BBoxMinY / tileSize), 0, tilesY - 1);
                int tx1 = Math.Clamp((int)MathF.Floor(p.BBoxMaxX / tileSize), 0, tilesX - 1);
                int ty1 = Math.Clamp((int)MathF.Floor(p.BBoxMaxY / tileSize), 0, tilesY - 1);

                minTx[i] = tx0; minTy[i] = ty0; maxTx[i] = tx1; maxTy[i] = ty1;

                for (int ty = ty0; ty <= ty1; ty++)
                {
                    int rowBase = ty * tilesX;
                    for (int tx = tx0; tx <= tx1; tx++)
                        counts[rowBase + tx]++;
                }
            }

            // Prefix-sum the per-tile counts into CSR offsets.
            var offsets = new int[tileCount + 1];
            for (int t = 0; t < tileCount; t++)
                offsets[t + 1] = offsets[t] + counts[t];

            var indices = new int[offsets[tileCount]];
            var cursor = (int[])offsets.Clone();

            // Pass 2: scatter primitive indices into their tile buckets.
            // Iterating i in ascending order preserves the original
            // (layer-sorted) primitive order within each tile bucket, which
            // is required for correct painter's-algorithm alpha blending.
            for (int i = 0; i < pc; i++)
            {
                int ty0 = minTy[i], ty1 = maxTy[i], tx0 = minTx[i], tx1 = maxTx[i];
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    int rowBase = ty * tilesX;
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        int t = rowBase + tx;
                        indices[cursor[t]++] = i;
                    }
                }
            }

            return new TileBinResult(offsets, indices, tilesX, tilesY, tileSize);
        }
    }
}
