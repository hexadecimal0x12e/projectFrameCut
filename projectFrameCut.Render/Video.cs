using FFmpeg.AutoGen;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.ColorSpaces;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render
{
    public class Video : IDisposable
    {
        private readonly string filePath;
        private bool is16Bit;

        public Video(string path, bool? Is16Bit = null)
        {
            is16Bit = Is16Bit ?? Path.GetExtension(path) == ".mkv"; //只有ffv1支持16bit

            filePath = path ?? throw new ArgumentNullException(nameof(path));
            decoders.AddOrUpdate(
                filePath,
                path => is16Bit ? new DecoderContext16Bit(path) : new DecoderContext8Bit(path),
                (path, existing) =>
                {
                    if (existing.Disposed) return is16Bit ? new DecoderContext16Bit(path) : new DecoderContext8Bit(path);
                    return existing;
                });
        }

        private static readonly ConcurrentDictionary<string, IDecoderContext> decoders = new();

        public IPicture ExtractFrame(uint targetFrame) => decoders.TryGetValue(filePath, out var value) ? value.GetFrame(targetFrame) : throw new NullReferenceException($"Video file '{filePath}''s decoder context is not exist.");

        public IDecoderContext Decoder => decoders.TryGetValue(filePath, out var value) ? value : throw new NullReferenceException($"Video file '{filePath}''s decoder context is not exist.");


        public static void ReleaseDecoder(string videoPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return;
            if (decoders.TryRemove(videoPath, out var dec))
            {
                dec.Dispose();
            }
        }

        
        public void Dispose()
        {
            ReleaseDecoder(filePath);
        }


    }

    public interface IDecoderContext : IDisposable
    {
        abstract void Initialize();
        abstract IPicture GetFrame(uint targetFrame, bool hasAlpha = false);
        public uint Index { get; set; }
        public bool Disposed { get; }
        public long TotalFrames { get; }   // -1 = 未知
        public double Fps { get; }
        public int Width { get; }
        public int Height { get; }
    }

    public static unsafe class FFmpegHelper
    {
        public static void Throw(int err, string api)
        {
            if (err >= 0) return;
            var msg = GetErrorString(err);
            throw new InvalidOperationException
            ($"'{api}' failed during writing the video,{(msg is not null ? $" probably because '{msg}'." : " but we don't know what thing it happens.")} (FFmpeg internal error code:{err})")
            {
                HResult = err
            };
        }

        public static string? GetErrorString(int err)
        {
            const int AV_ERROR_MAX_STRING_SIZE = 1024;
            byte* buffer = stackalloc byte[AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(err, buffer, (ulong)AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringAnsi((IntPtr)buffer);
        }
    }
}

