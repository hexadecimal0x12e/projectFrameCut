using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.ColorSpaces;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public static unsafe class FFmpegHelper
    {
        public static void Throw(int err, string api)
        {
            if (err >= 0) return;
            var msg = GetErrorString(err);
            throw new InvalidOperationException
            ($"'{api}' failed during writing the video,{(msg is not null ? $" probably because '{msg}'." : " but we don't know what thing it happens.")}\r\n(FFmpeg internal error code: 0x{err:x2})")
            {
                HResult = err,
                Source = "FFmpeg"
            };
        }

        public static string? GetErrorString(int err)
        {
            const int AV_ERROR_MAX_STRING_SIZE = 1024;
            byte* buffer = stackalloc byte[AV_ERROR_MAX_STRING_SIZE];
            ffmpeg.av_strerror(err, buffer, (ulong)AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringAnsi((IntPtr)buffer);
        }


        public static class CodecUtils
        {
            public record CodecInfo(
                string Name,
                string LongName,
                AVMediaType Type,
                bool IsEncoder,
                bool IsDecoder,
                AVCodecID Id
            );

            public static List<CodecInfo> GetAllCodecs()
            {
                var codecs = new List<CodecInfo>();
                void* opaque = null;
                AVCodec* codec;

                while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
                {
                    string name = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "Unknown";
                    string longName = Marshal.PtrToStringAnsi((IntPtr)codec->long_name) ?? "";

                    bool isEncoder = ffmpeg.av_codec_is_encoder(codec) != 0;
                    bool isDecoder = ffmpeg.av_codec_is_decoder(codec) != 0;

                    codecs.Add(new CodecInfo(
                        name,
                        longName,
                        codec->type,
                        isEncoder,
                        isDecoder,
                        codec->id
                    ));
                }

                return codecs;
            }

            public static string EnumAllCodecs()
            {
                StringBuilder result = new();
                void* opaque = null;
                AVCodec* codec;

                while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
                {
                    string name = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "Unknown";
                    string typeName = codec->type switch
                    {
                        AVMediaType.AVMEDIA_TYPE_VIDEO => "Video",
                        AVMediaType.AVMEDIA_TYPE_AUDIO => "Audio",
                        AVMediaType.AVMEDIA_TYPE_SUBTITLE => "Subtitle",
                        AVMediaType.AVMEDIA_TYPE_DATA => "Data",
                        AVMediaType.AVMEDIA_TYPE_ATTACHMENT => "Attachment",
                        _ => "Unknown"
                    };

                    bool isEncoder = ffmpeg.av_codec_is_encoder(codec) != 0;
                    bool isDecoder = ffmpeg.av_codec_is_decoder(codec) != 0;

                    string codecType = (isEncoder, isDecoder) switch
                    {
                        (true, true) => "Encoder/Decoder",
                        (true, false) => "Encoder",
                        (false, true) => "Decoder",
                        _ => "Unknown"
                    };

                    result.AppendLine($"Codec: {name,-20}  Type: {typeName,-12}  {codecType}");
                }

                return result.ToString();
            }

            public static List<CodecInfo> GetCodecsByType(AVMediaType mediaType, bool? encoderOnly = null)
            {
                var codecs = new List<CodecInfo>();
                void* opaque = null;
                AVCodec* codec;

                while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
                {
                    if (codec->type != mediaType) continue;

                    bool isEncoder = ffmpeg.av_codec_is_encoder(codec) != 0;
                    bool isDecoder = ffmpeg.av_codec_is_decoder(codec) != 0;

                    // 如果指定了 encoderOnly 过滤条件
                    if (encoderOnly.HasValue)
                    {
                        if (encoderOnly.Value && !isEncoder) continue;
                        if (!encoderOnly.Value && !isDecoder) continue;
                    }

                    string name = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "Unknown";
                    string longName = Marshal.PtrToStringAnsi((IntPtr)codec->long_name) ?? "";

                    codecs.Add(new CodecInfo(
                        name,
                        longName,
                        codec->type,
                        isEncoder,
                        isDecoder,
                        codec->id
                    ));
                }

                return codecs;
            }

            public static AVCodec* FindCodecByName(string name, bool encoder = true)
            {
                return encoder
                    ? ffmpeg.avcodec_find_encoder_by_name(name)
                    : ffmpeg.avcodec_find_decoder_by_name(name);
            }

            public static AVCodec* FindCodecById(AVCodecID id, bool encoder = true)
            {
                return encoder
                    ? ffmpeg.avcodec_find_encoder(id)
                    : ffmpeg.avcodec_find_decoder(id);
            }
        }
    }
}

