using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public interface IDecoderContext : IDisposable
    {
        abstract void Initialize();
        abstract Picture GetFrame(uint targetFrame, bool hasAlpha = false);
        public bool Disposed { get; }
        public long TotalFrames { get; }
        public double Fps { get; }
        public int Width { get; }
        public int Height { get; }
    }

}
