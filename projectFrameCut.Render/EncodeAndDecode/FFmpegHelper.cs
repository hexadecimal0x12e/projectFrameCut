using FFmpeg.AutoGen;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public static unsafe class FFmpegHelper
    {
        public const int INTERNAL_FFMPEG_ERRCODE_NOSTREAMFOUND = int.MaxValue - 1;
        public const int INTERNAL_FFMPEG_ERRCODE_UNSUPPORTFORMAT = int.MaxValue - 2;

        private static int _ffmpegLogHooked = 0;
        private static av_log_set_callback_callback? _ffmpegLogCallback;

        public static bool IsPointerAddressesValid<TPointerType>(TPointerType* ptr) where TPointerType : unmanaged => ptr != null;
        public static bool IsPointerAddressesNotValid<TPointerType>(TPointerType* ptr) where TPointerType : unmanaged => ptr == null;

        public static void SetupFFmpegLogging(int minLogLevel = ffmpeg.AV_LOG_INFO)
        {
            if (Interlocked.Exchange(ref _ffmpegLogHooked, 1) == 1) return;

            _ffmpegLogCallback = new av_log_set_callback_callback(OnFFmpegLog);
            ffmpeg.av_log_set_level(minLogLevel);
            ffmpeg.av_log_set_callback(_ffmpegLogCallback);
            Log($"FFmpeg log callback registered. minLevel={minLogLevel}", "info");
        }

        [DebuggerStepThrough()]
        private static void OnFFmpegLog(void* ptr, int level, string format, byte* vl)
        {
            if (level > ffmpeg.av_log_get_level()) return;

            try
            {
                const int lineBufferSize = 4096;
                byte* lineBuffer = stackalloc byte[lineBufferSize];
                int printPrefix = 1;
                ffmpeg.av_log_format_line2(ptr, level, format, vl, lineBuffer, lineBufferSize, &printPrefix);

                var raw = Marshal.PtrToStringAnsi((IntPtr)lineBuffer) ?? format ?? string.Empty;
                if (raw.Length == 0) return;

                // FFmpeg format strings can contain trailing newlines and placeholders.
                var msg = raw.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (msg.Length == 0) return;
#if DEBUG
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.log")))
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ffmpeg.log"), $"[{MapFFmpegLogLevel(level)}] {msg}\r\n");
                }
#endif
                Log(msg, MapFFmpegLogLevel(level));
            }
            catch
            {
                // Keep logging callback non-throwing, FFmpeg calls this from native context.
            }
        }
        [DebuggerStepThrough()]
        private static string MapFFmpegLogLevel(int level)
        {
            if (level <= ffmpeg.AV_LOG_PANIC || level <= ffmpeg.AV_LOG_FATAL || level <= ffmpeg.AV_LOG_ERROR)
                return "FFmpeg @ error";
            if (level <= ffmpeg.AV_LOG_WARNING)
                return "FFmpeg @ warning";
            if (level <= ffmpeg.AV_LOG_INFO)
                return "FFmpeg @ info";
            return "FFmpeg @ diag";
        }


        public static void Throw(int err, string api)
        {
            if (err >= 0) return;
            var msg = GetErrorString(err);
            throw new InvalidOperationException
            ($"'{api}' failed during writing the video,{(msg is not null ? $" probably because '{msg}'." : " but we don't know what thing it happens.")}\r\n(FFmpeg internal error code: 0x{err:x8})")
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

        public static AVInputFormat* FindInputFormatByName(string formatName)
        {
            if (string.IsNullOrWhiteSpace(formatName))
            {
                return null;
            }

            try
            {
                return ffmpeg.av_find_input_format(formatName);
            }
            catch (EntryPointNotFoundException)
            {
                // Some FFmpeg builds do not export av_find_input_format.
                // Fallback to iterating demuxers and matching short names.
            }

            void* opaque = null;
            AVInputFormat* current = null;
            while ((current = ffmpeg.av_demuxer_iterate(&opaque)) != null)
            {
                if (current->name == null)
                {
                    continue;
                }

                string names = Marshal.PtrToStringAnsi((IntPtr)current->name) ?? string.Empty;
                if (names.Length == 0)
                {
                    continue;
                }

                var segments = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var segment in segments)
                {
                    if (string.Equals(segment, formatName, StringComparison.OrdinalIgnoreCase))
                    {
                        return current;
                    }
                }
            }

            return null;
        }

        public static void DetectWhyCannotOpenVideo(string path, int averr)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                throw new FileNotFoundException($"The video file '{path}' doesn't exist.");
            }

            if (fi.Length <= 16)
            {
                throw new ArgumentNullException($"The video file '{path}' is too small, and doesn't seems like a video file.");
            }

            try
            {
                FileStream fs = new FileStream(path, FileMode.Open);
#pragma warning disable CA2022 // 避免使用 "Stream.Read" 进行不准确读取
                fs.Read(new byte[16]);
#pragma warning restore CA2022 // 避免使用 "Stream.Read" 进行不准确读取

                var errstr = FFmpegHelper.GetErrorString(averr);
                throw new InvalidDataException($"File '{path}' seems don't like a video file or it has an unsupported format by either FFmpeg or projectFrameCut. {Environment.NewLine}Try install the codec extension. If you continuously encountering this issue, use a tool try encode your video again to another format. {Environment.NewLine}(FFmpeg error '{errstr}', HResult: 0x{averr:x8})")
                {
                    HResult = averr
                };


            }
            catch (IOException ex)
            {
                throw new FileLoadException($"projectFrameCut can't read the video file '{path}', it's maybe because of a I/O error:'{ex.Message}'", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new FileLoadException($"projectFrameCut can't read the video file '{path}' because of no enough privileges. Try grant yourself with enough privileges to read the video.", ex);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException($"Failed to open the video file '{path}', it's maybe because of an error:'{ex.Message}'. Try restart render, or reboot your computer. If you continuously encountering this issue, try install ffmpeg toolkit on your computer, then run this command and observe whether there is any error message:\r\nffprobe {Path.GetFullPath(path)}");
            }
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

                    result.AppendLine($"Codec: {name,-20}  ClipType: {typeName,-12}  {codecType}");
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

        public static int GetAVPixelFormatBitsPerPixel(AVPixelFormat pixFmt)
        {
            try
            {
                AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(pixFmt);
                if (desc != null && desc->nb_components > 0)
                {
                    return desc->comp[0].depth;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // av_pix_fmt_desc_get not exported by this FFmpeg build.
            }
            return -1; // Unknown or unsupported pixel format
        }

        public static int DetectVideoBitDepth(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentNullException(nameof(path));

            AVFormatContext* fmt = null;

            try
            {
                fmt = ffmpeg.avformat_alloc_context();
                if (fmt == null)
                    throw new OutOfMemoryException("Failed to allocate AVFormatContext for bit depth detection.");

                int openRet = ffmpeg.avformat_open_input(&fmt, path, null, null);
                if (openRet != 0)
                    DetectWhyCannotOpenVideo(path, openRet);

                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new InvalidDataException($"Cannot probe stream info for '{path}'.");

                int videoStreamIndex = -1;
                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        videoStreamIndex = i;
                        break;
                    }
                }

                if (videoStreamIndex < 0)
                    throw new InvalidDataException($"No video stream found in '{path}'.");

                AVCodecParameters* par = fmt->streams[videoStreamIndex]->codecpar;

                if (par->bits_per_raw_sample > 0)
                    return par->bits_per_raw_sample;

                var pixFmt = (AVPixelFormat)par->format;
                if (pixFmt != AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    try
                    {
                        AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get(pixFmt);
                        if (desc != null && desc->nb_components > 0)
                        {
                            int depth = desc->comp[0].depth;
                            if (depth > 0)
                                return depth;
                        }
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // av_pix_fmt_desc_get not exported by this FFmpeg build.
                    }
                }

                if (par->bits_per_coded_sample > 0)
                    return par->bits_per_coded_sample;

                throw new InvalidDataException($"Cannot determine bit depth for video '{path}'. The codec may not expose this information.");
            }
            finally
            {
                if (fmt != null)
                {
                    AVFormatContext* tmp = fmt;
                    ffmpeg.avformat_close_input(&tmp);
                }
            }
        }

        public static class InputDeviceUtils
        {
            public record InputDeviceInfo(
                string Name,
                string LongName,
                string Kind,
                bool IsAudioInput,
                bool IsVideoInput,
                bool IsVirtualInput
            );

            private static int _deviceRegistered = 0;

            private static void EnsureDeviceRegistered()
            {
                if (Interlocked.Exchange(ref _deviceRegistered, 1) == 1) return;
                //ffmpeg.avdevice_register_all(); 
                //starting from FFmpeg 5.0, avdevice_register_all() is no longer needed, the devices are registered automatically.
            }

            public static List<InputDeviceInfo> GetAllInputDevices(bool includeVirtualInputs = true)
            {
                EnsureDeviceRegistered();

                var result = new List<InputDeviceInfo>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                EnumerateInputDevices(isVideo: true, result, visited);
                EnumerateInputDevices(isVideo: false, result, visited);

                if (includeVirtualInputs)
                {
                    AddVirtualInput("lavfi", "Libavfilter virtual input device", result, visited);
                }

                return result;
            }

            public static string EnumAllInputDevices(bool includeVirtualInputs = true)
            {
                var devices = GetAllInputDevices(includeVirtualInputs);
                StringBuilder result = new();

                foreach (var d in devices)
                {
                    result.AppendLine($"Input: {d.Name,-20}  Kind: {d.Kind,-13}  Virtual: {(d.IsVirtualInput ? "Yes" : "No")}");
                }

                return result.ToString();
            }

            private static void EnumerateInputDevices(bool isVideo, List<InputDeviceInfo> result, HashSet<string> visited)
            {
                AVInputFormat* format = null;
                while ((format = isVideo
                    ? ffmpeg.av_input_video_device_next(format)
                    : ffmpeg.av_input_audio_device_next(format)) != null)
                {
                    string name = Marshal.PtrToStringAnsi((IntPtr)format->name) ?? "Unknown";
                    string longName = Marshal.PtrToStringAnsi((IntPtr)format->long_name) ?? "";
                    if (!visited.Add(name)) continue;

                    result.Add(new InputDeviceInfo(
                        name,
                        longName,
                        isVideo ? "VideoDevice" : "AudioDevice",
                        IsAudioInput: !isVideo,
                        IsVideoInput: isVideo,
                        IsVirtualInput: false
                    ));
                }
            }

            private static void AddVirtualInput(string formatName, string fallbackLongName, List<InputDeviceInfo> result, HashSet<string> visited)
            {
                AVInputFormat* format = FindInputFormatByName(formatName);
                if (format == null) return;

                string name = Marshal.PtrToStringAnsi((IntPtr)format->name) ?? formatName;
                if (!visited.Add(name)) return;

                string longName = Marshal.PtrToStringAnsi((IntPtr)format->long_name) ?? fallbackLongName;
                result.Add(new InputDeviceInfo(
                    name,
                    longName,
                    "VirtualInput",
                    IsAudioInput: true,
                    IsVideoInput: true,
                    IsVirtualInput: true
                ));
            }
        }
    }
}

