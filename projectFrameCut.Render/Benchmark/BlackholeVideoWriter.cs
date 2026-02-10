using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.Benchmark
{
    /// <summary>
    /// A video writer that just disposes the pictures appended to it without writing anything.
    /// </summary>
    public class BlackholeVideoWriter : IVideoWriter
    {
        private uint _index;

        public int Width { get; set; }
        public int Height { get; set; }
        public string OutputPath { get; set; }
        public int FramePerSecond { get; set; }
        public string CodecName { get; set; }
        public string PixelFormat { get; set; }

        public uint DurationWritten => _index;

        public event EventHandler<IPicture>? OnFrameWrite;

        public void Append(IPicture<ushort> picture)
        {
            _index++;
            if (OnFrameWrite is null)
            {
                picture.Dispose();
            }
            else
            {
                OnFrameWrite?.Invoke(this, picture);
            }
        }

        public void Append(IPicture<byte> picture)
        {
            _index++;
            if (OnFrameWrite is null)
            {
                picture.Dispose();
            }
            else
            {
                OnFrameWrite?.Invoke(this, picture);
            }
        }

        public void Dispose()
        {
        }

        public void Finish()
        {
        }

        public void Initialize()
        {

        }
        bool IVideoWriter.SupportCodec(string codecName) => codecName == "BlackHoleWriter";

    }
}
