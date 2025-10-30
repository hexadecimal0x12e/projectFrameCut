using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public interface ITwoFrameRender
    {
        public int targetWidth { get; set; }
        public int targetHeight { get; set; }
        public static bool ParallelRender { get; set; } = true;    

        public abstract Task<ushort[]> Calculate(ushort[] A, ushort[] B, CancellationToken? ct = null);

        public abstract Task<float[]> CalculateAlpha(float[] A, float[] B, CancellationToken? ct = null);

        public virtual async Task<Picture> Render(Picture A, Picture B, CancellationToken? ct = null)
        {
            var resizedA = A.Resize(targetWidth, targetHeight, true);
            var resizedB = B.Resize(targetWidth, targetHeight, true);

            var r = Calculate(resizedA.r, resizedB.r, ct);
            var g = Calculate(resizedA.g, resizedB.g, ct);
            var b = Calculate(resizedA.b, resizedB.b, ct);
            var a = CalculateAlpha(resizedA.a, resizedB.a, ct);

            if(ParallelRender) await Task.WhenAll([r, g, b, a]);

            return new Picture(A)
            {
                r = await r,
                g = await g,
                b = await b,
                a = await a,
                filePath = null
            };
        }
    }

    public interface IOneFrameRender
    {
        public int targetWidth { get; set; }
        public int targetHeight { get; set; }
        public static bool ParallelRender { get; set; } = true;

        public abstract Task<ushort[]> Calculate(ushort[] channel,  CancellationToken? ct = null);

        public abstract Task<float[]> CalculateAlpha(float[] alpha,  CancellationToken? ct = null);

        public virtual async Task<Picture> Render(Picture src, CancellationToken? ct = null)
        {
            var resized = src.Resize(targetWidth, targetHeight, true);

            var r = Calculate(resized.r, ct);
            var g = Calculate(resized.g, ct);
            var b = Calculate(resized.b, ct);
            var a = CalculateAlpha(resized.a, ct);

            if (ParallelRender) await Task.WhenAll([r, g, b, a]);

            return new Picture(src)
            {
                r = await r,
                g = await g,
                b = await b,
                a = await a,
                filePath = null
            };
        }
    }
}
