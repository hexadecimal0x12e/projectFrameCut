using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase
{
    public interface IVideoSource : IDisposable
    {
        public abstract void Initialize();
        public bool TryInitialize()
        {
            try
            {
                Initialize();
                return true;

            }
            catch
            {
                return false;
            }
        }
        abstract IPicture GetFrame(uint targetFrame, bool hasAlpha = false);
        public string[] PreferredExtension { get; }
        public uint Index { get; set; }
        public bool Disposed { get; }
        public long TotalFrames { get; }   // -1 = 未知
        public double Fps { get; }
        public int Width { get; }
        public int Height { get; }
    }
}
