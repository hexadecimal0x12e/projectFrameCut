using FFmpeg.AutoGen;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Runtime.InteropServices;
using static projectFrameCut.Render.EncodeAndDecode.FFmpegHelper;

namespace projectFrameCut.ScriptEngine
{
    // ═══════════════════════════════════════════════════════════════════
    //  Get-MediaInfo — 探测多媒体文件元信息
    //  用法: Get-MediaInfo -FilePath "video.mp4"
    //  输出: PSObject 包含容器、视频流、音频流、字幕流详细元数据
    // ═══════════════════════════════════════════════════════════════════
    [Cmdlet(VerbsCommon.Get, "MediaInfo")]
    public sealed class GetMediaInfoCommand : PSCmdlet
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
        [ValidateNotNullOrEmpty]
        public string FilePath { get; set; } = string.Empty;

        protected override void ProcessRecord()
        {
            string? resolvedPath = null;
            try
            {
                resolvedPath = GetUnresolvedProviderPathFromPSPath(FilePath);
                if (!File.Exists(resolvedPath))
                {
                    WriteError(new ErrorRecord(
                        new FileNotFoundException($"File not found: '{resolvedPath}'."),
                        "MediaFileNotFound",
                        ErrorCategory.ObjectNotFound,
                        resolvedPath));
                    return;
                }

                var result = ProbeMediaFile(resolvedPath);
                WriteObject(result);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "MediaInfoProbeFailed", ErrorCategory.ReadError, FilePath));
            }
        }

        private static unsafe PSObject ProbeMediaFile(string path)
        {
            AVFormatContext* fmt = null;
            AVDictionaryEntry* tag = null;

            try
            {
                fmt = ffmpeg.avformat_alloc_context();
                if (fmt == null)
                    throw new InvalidOperationException("Failed to allocate FFmpeg format context.");

                int ret = ffmpeg.avformat_open_input(&fmt, path, null, null);
                if (ret != 0)
                    FFmpegHelper.DetectWhyCannotOpenVideo(path, ret);

                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new InvalidDataException("Failed to retrieve stream info from media file.");

                // ── 容器信息 ────────────────────────────────────────
                var fi = new FileInfo(path);
                string containerName = fmt->iformat != null
                    ? Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->name) ?? "unknown"
                    : "unknown";
                string containerLongName = fmt->iformat != null
                    ? Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->long_name) ?? ""
                    : "";

                var obj = new PSObject();
                obj.Properties.Add(new PSNoteProperty("FileName", fi.Name));
                obj.Properties.Add(new PSNoteProperty("FullPath", fi.FullName));
                obj.Properties.Add(new PSNoteProperty("FileSize", fi.Length));
                obj.Properties.Add(new PSNoteProperty("FileSizeHuman", FormatFileSize(fi.Length)));
                obj.Properties.Add(new PSNoteProperty("ContainerFormat", containerName));
                obj.Properties.Add(new PSNoteProperty("ContainerFormatLong", containerLongName));

                // fmt->duration 以 AV_TIME_BASE (1000000 = 1 µs tick) 为单位
                const long AV_TIME_BASE = 1000000;
                double durationSeconds = fmt->duration != ffmpeg.AV_NOPTS_VALUE
                    ? (double)fmt->duration / AV_TIME_BASE
                    : 0.0;
                obj.Properties.Add(new PSNoteProperty("DurationSeconds", Math.Round(durationSeconds, 3)));
                obj.Properties.Add(new PSNoteProperty("Duration", FormatDuration(durationSeconds)));
                obj.Properties.Add(new PSNoteProperty("BitRate", fmt->bit_rate > 0 ? (long)fmt->bit_rate : 0));
                obj.Properties.Add(new PSNoteProperty("StartTimeSeconds", fmt->start_time != ffmpeg.AV_NOPTS_VALUE
                    ? (double)fmt->start_time / AV_TIME_BASE
                    : 0.0));
                obj.Properties.Add(new PSNoteProperty("StreamCount", fmt->nb_streams));

                // ── 容器标签(元数据) ──────────────────────────────
                var metadata = new Dictionary<string, string>();
                while ((tag = ffmpeg.av_dict_get(fmt->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
                {
                    string key = Marshal.PtrToStringAnsi((IntPtr)tag->key) ?? "";
                    string val = Marshal.PtrToStringAnsi((IntPtr)tag->value) ?? "";
                    if (!string.IsNullOrEmpty(key))
                        metadata[key] = val;
                }
                obj.Properties.Add(new PSNoteProperty("Metadata", metadata));

                // ── 遍历各流 ──────────────────────────────────────
                var videoStreams = new List<PSObject>();
                var audioStreams = new List<PSObject>();
                var subtitleStreams = new List<PSObject>();
                var otherStreams = new List<PSObject>();

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var st = fmt->streams[i];
                    if (st == null) continue;

                    AVCodecParameters* par = st->codecpar;
                    if (par == null) continue;

                    AVCodecID codecId = par->codec_id;
                    AVCodec* codec = ffmpeg.avcodec_find_decoder(codecId);
                    string codecName = codec != null
                        ? Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown"
                        : ffmpeg.avcodec_get_name(codecId) ?? "unknown";
                    string codecLongName = codec != null
                        ? Marshal.PtrToStringAnsi((IntPtr)codec->long_name) ?? ""
                        : "";
                    string codecTypeStr = par->codec_type switch
                    {
                        AVMediaType.AVMEDIA_TYPE_VIDEO => "Video",
                        AVMediaType.AVMEDIA_TYPE_AUDIO => "Audio",
                        AVMediaType.AVMEDIA_TYPE_SUBTITLE => "Subtitle",
                        AVMediaType.AVMEDIA_TYPE_DATA => "Data",
                        AVMediaType.AVMEDIA_TYPE_ATTACHMENT => "Attachment",
                        _ => "Unknown"
                    };

                    // 读取该流的元数据标签
                    var streamMeta = new Dictionary<string, string>();
                    AVDictionaryEntry* stTag = null;
                    while ((stTag = ffmpeg.av_dict_get(st->metadata, "", stTag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
                    {
                        string k = Marshal.PtrToStringAnsi((IntPtr)stTag->key) ?? "";
                        string v = Marshal.PtrToStringAnsi((IntPtr)stTag->value) ?? "";
                        if (!string.IsNullOrEmpty(k))
                            streamMeta[k] = v;
                    }

                    switch (par->codec_type)
                    {
                        case AVMediaType.AVMEDIA_TYPE_VIDEO:
                            videoStreams.Add(BuildVideoStreamObject(i, st, par, codecName, codecLongName, streamMeta));
                            break;
                        case AVMediaType.AVMEDIA_TYPE_AUDIO:
                            audioStreams.Add(BuildAudioStreamObject(i, st, par, codecName, codecLongName, streamMeta));
                            break;
                        case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                            subtitleStreams.Add(BuildSubtitleStreamObject(i, st, par, codecName, codecLongName, streamMeta));
                            break;
                        default:
                            otherStreams.Add(BuildOtherStreamObject(i, codecTypeStr, codecName, codecLongName, streamMeta));
                            break;
                    }
                }

                obj.Properties.Add(new PSNoteProperty("VideoStreams", videoStreams.ToArray()));
                obj.Properties.Add(new PSNoteProperty("AudioStreams", audioStreams.ToArray()));
                obj.Properties.Add(new PSNoteProperty("SubtitleStreams", subtitleStreams.ToArray()));
                obj.Properties.Add(new PSNoteProperty("OtherStreams", otherStreams.ToArray()));

                // ── 便捷摘要 ──────────────────────────────────────
                var summary = new Dictionary<string, object>
                {
                    ["VideoCount"] = videoStreams.Count,
                    ["AudioCount"] = audioStreams.Count,
                    ["SubtitleCount"] = subtitleStreams.Count,
                    ["HasVideo"] = videoStreams.Count > 0,
                    ["HasAudio"] = audioStreams.Count > 0,
                };
                if (videoStreams.Count > 0)
                {
                    var firstVideo = videoStreams[0];
                    summary["Width"] = firstVideo.Properties["Width"]?.Value;
                    summary["Height"] = firstVideo.Properties["Height"]?.Value;
                    summary["Fps"] = firstVideo.Properties["Fps"]?.Value;
                    summary["TotalFrames"] = firstVideo.Properties["TotalFrames"]?.Value;
                    summary["IsHdr"] = firstVideo.Properties["IsHdr"]?.Value;
                }
                obj.Properties.Add(new PSNoteProperty("Summary", summary));

                return obj;
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

        private static unsafe PSObject BuildVideoStreamObject(int index, AVStream* st, AVCodecParameters* par,
            string codecName, string codecLongName, Dictionary<string, string> metadata)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Index", index));
            obj.Properties.Add(new PSNoteProperty("Codec", codecName));
            obj.Properties.Add(new PSNoteProperty("CodecLongName", codecLongName));
            obj.Properties.Add(new PSNoteProperty("CodecId", par->codec_id.ToString()));
            obj.Properties.Add(new PSNoteProperty("Width", par->width));
            obj.Properties.Add(new PSNoteProperty("Height", par->height));

            // 像素格式
            string pixFmt = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)par->format);
            obj.Properties.Add(new PSNoteProperty("PixelFormat", pixFmt ?? "unknown"));

            // 帧率
            AVRational avgFps = st->avg_frame_rate;
            AVRational realFps = st->r_frame_rate;
            double fps = 0.0;
            if (avgFps.den > 0 && avgFps.num > 0)
                fps = ffmpeg.av_q2d(avgFps);
            else if (realFps.den > 0 && realFps.num > 0)
                fps = ffmpeg.av_q2d(realFps);
            obj.Properties.Add(new PSNoteProperty("Fps", Math.Round(fps, 3)));

            // 总帧数
            long nbFrames = (long)st->nb_frames;
            if (nbFrames <= 0 && fps > 0)
            {
                double dur = st->duration > 0
                    ? st->duration * ffmpeg.av_q2d(st->time_base)
                    : 0;
                if (dur > 0)
                    nbFrames = (long)Math.Round(dur * fps);
            }
            obj.Properties.Add(new PSNoteProperty("TotalFrames", nbFrames > 0 ? nbFrames : -1));

            // 比特率
            obj.Properties.Add(new PSNoteProperty("BitRate", par->bit_rate > 0 ? (long)par->bit_rate : 0));

            // 颜色/ HDR 信息
            bool isHdr = IsHdrTransferCharacteristic(par->color_trc)
                || (par->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020
                    && par->color_space == AVColorSpace.AVCOL_SPC_BT2020_NCL);
            obj.Properties.Add(new PSNoteProperty("IsHdr", isHdr));
            obj.Properties.Add(new PSNoteProperty("ColorTransfer", par->color_trc.ToString()));
            obj.Properties.Add(new PSNoteProperty("ColorPrimaries", par->color_primaries.ToString()));
            obj.Properties.Add(new PSNoteProperty("ColorSpace", par->color_space.ToString()));
            obj.Properties.Add(new PSNoteProperty("ColorRange", par->color_range.ToString()));

            // 编码器配置（profile / level）
            obj.Properties.Add(new PSNoteProperty("Profile", par->profile.ToString()));
            obj.Properties.Add(new PSNoteProperty("Level", par->level));

            // 宽高比
            if (st->sample_aspect_ratio.num != 0 && st->sample_aspect_ratio.den != 0)
            {
                double sar = ffmpeg.av_q2d(st->sample_aspect_ratio);
                obj.Properties.Add(new PSNoteProperty("SampleAspectRatio", Math.Round(sar, 6)));
                obj.Properties.Add(new PSNoteProperty("DisplayAspectRatio",
                    $"{par->width * st->sample_aspect_ratio.num / st->sample_aspect_ratio.den}:{par->height}"));
            }
            else
            {
                obj.Properties.Add(new PSNoteProperty("SampleAspectRatio", 1.0));
                obj.Properties.Add(new PSNoteProperty("DisplayAspectRatio", $"{par->width}:{par->height}"));
            }

            obj.Properties.Add(new PSNoteProperty("Metadata", metadata));
            return obj;
        }

        private static unsafe PSObject BuildAudioStreamObject(int index, AVStream* st, AVCodecParameters* par,
            string codecName, string codecLongName, Dictionary<string, string> metadata)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Index", index));
            obj.Properties.Add(new PSNoteProperty("Codec", codecName));
            obj.Properties.Add(new PSNoteProperty("CodecLongName", codecLongName));
            obj.Properties.Add(new PSNoteProperty("CodecId", par->codec_id.ToString()));
            obj.Properties.Add(new PSNoteProperty("SampleRate", par->sample_rate));
            obj.Properties.Add(new PSNoteProperty("Channels", par->ch_layout.nb_channels));

            // 通道布局名称
            var chLayout = par->ch_layout;
            if (chLayout.nb_channels > 0)
            {
                byte* chBuf = stackalloc byte[256];
                ffmpeg.av_channel_layout_describe(&chLayout, chBuf, 256);
                string chStr = Marshal.PtrToStringAnsi((IntPtr)chBuf) ?? "unknown";
                obj.Properties.Add(new PSNoteProperty("ChannelLayout", chStr));
            }
            else
            {
                obj.Properties.Add(new PSNoteProperty("ChannelLayout", "unknown"));
            }

            obj.Properties.Add(new PSNoteProperty("BitRate", par->bit_rate > 0 ? (long)par->bit_rate : 0));
            obj.Properties.Add(new PSNoteProperty("BitPerSample", par->bits_per_raw_sample > 0 ? par->bits_per_raw_sample : 16));

            // 语言
            string lang = metadata.TryGetValue("language", out var l) ? l : "";
            obj.Properties.Add(new PSNoteProperty("Language", lang));

            obj.Properties.Add(new PSNoteProperty("Metadata", metadata));
            return obj;
        }

        private static unsafe PSObject BuildSubtitleStreamObject(int index, AVStream* st, AVCodecParameters* par,
            string codecName, string codecLongName, Dictionary<string, string> metadata)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Index", index));
            obj.Properties.Add(new PSNoteProperty("Codec", codecName));
            obj.Properties.Add(new PSNoteProperty("CodecLongName", codecLongName));
            obj.Properties.Add(new PSNoteProperty("CodecId", par->codec_id.ToString()));

            string lang = metadata.TryGetValue("language", out var l) ? l : "";
            obj.Properties.Add(new PSNoteProperty("Language", lang));

            string title = metadata.TryGetValue("title", out var t) ? t : "";
            obj.Properties.Add(new PSNoteProperty("Title", title));

            obj.Properties.Add(new PSNoteProperty("Metadata", metadata));
            return obj;
        }

        private static PSObject BuildOtherStreamObject(int index, string type, string codecName, string codecLongName,
            Dictionary<string, string> metadata)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Index", index));
            obj.Properties.Add(new PSNoteProperty("Type", type));
            obj.Properties.Add(new PSNoteProperty("Codec", codecName));
            obj.Properties.Add(new PSNoteProperty("CodecLongName", codecLongName));
            obj.Properties.Add(new PSNoteProperty("Metadata", metadata));
            return obj;
        }

        /// <summary>检查是否为 HDR 传输特性。</summary>
        private static bool IsHdrTransferCharacteristic(AVColorTransferCharacteristic trc)
        {
            return trc == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
                || trc == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            int unitIndex = 0;
            double size = bytes;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F2} {units[unitIndex]}";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "00:00:00.000";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Get-MediaFrame — 从视频中提取指定帧并保存为图片
    //  用法:
    //    Get-MediaFrame -FilePath "video.mp4" -Frame 120
    //    Get-MediaFrame -FilePath "video.mp4" -Frame 120 -OutputPath "frame.png"
    //    Get-MediaFrame -FilePath "video.mp4" -Frame 120 -Decoder Auto
    //  输出: PSObject 包含输出路径和帧信息
    // ═══════════════════════════════════════════════════════════════════
    [Cmdlet(VerbsCommon.Get, "MediaFrame")]
    public sealed class GetMediaFrameCommand : PSCmdlet
    {
        /// <summary>解码器选择模式</summary>
        public enum DecoderSelectionMode
        {
            Auto,
            Prefer8Bit,
            Prefer16Bit,
            HDR
        }

        [Parameter(Mandatory = true, Position = 0)]
        [ValidateNotNullOrEmpty]
        public string FilePath { get; set; } = string.Empty;

        [Parameter(Mandatory = false, Position = 1)]
        public uint Frame { get; set; } = 0;

        [Parameter(Mandatory = false)]
        public string? OutputPath { get; set; }

        [Parameter(Mandatory = false)]
        [ValidateSet("png")]
        public string Format { get; set; } = "png";

        [Parameter(Mandatory = false)]
        public DecoderSelectionMode Decoder { get; set; } = DecoderSelectionMode.Auto;

        [Parameter(Mandatory = false)]
        public SwitchParameter Force { get; set; }

        /// <summary>放置提取帧的临时目录基路径</summary>
        internal static readonly string DefaultFrameOutputDir = Path.Combine(FileSystem.CacheDirectory, "ScriptWorkspace", "extracted-frames");

        protected override void ProcessRecord()
        {
            try
            {
                string resolvedPath = GetUnresolvedProviderPathFromPSPath(FilePath);
                if (!File.Exists(resolvedPath))
                {
                    WriteError(new ErrorRecord(
                        new FileNotFoundException($"File not found: '{resolvedPath}'."),
                        "MediaFileNotFound",
                        ErrorCategory.ObjectNotFound,
                        resolvedPath));
                    return;
                }

                // 确定输出路径
                string outputPath = ResolveOutputPath(resolvedPath);

                // 若已存在且未要求覆盖则跳过
                if (File.Exists(outputPath) && !Force)
                {
                    WriteWarning($"Output file already exists: '{outputPath}'. Use -Force to overwrite.");
                    var existing = CreateFrameResultPSObject(outputPath, Frame, resolvedPath);
                    WriteObject(existing);
                    return;
                }

                // 确保输出目录存在
                string? outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);

                // 提取帧 → 保存
                var result = ExtractAndSaveFrame(resolvedPath, Frame, outputPath, Decoder);
                WriteObject(result);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "ExtractFrameFailed", ErrorCategory.ReadError, FilePath));
            }
        }

        private string ResolveOutputPath(string videoPath)
        {
            if (!string.IsNullOrWhiteSpace(OutputPath))
            {
                // 用户指定了输出路径（可能是相对路径）
                string resolved = GetUnresolvedProviderPathFromPSPath(OutputPath);

                // 如果解析后是目录，追加默认文件名
                if (Directory.Exists(resolved) || resolved.EndsWith(Path.DirectorySeparatorChar) || resolved.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    string baseName = Path.GetFileNameWithoutExtension(videoPath);
                    return Path.Combine(resolved, $"{baseName}_frame{Frame}.{Format}");
                }

                return resolved;
            }

            // 自动生成到临时目录
            string videoName = Path.GetFileNameWithoutExtension(videoPath);
            string dateStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            return Path.Combine(DefaultFrameOutputDir, $"{videoName}_frame{Frame}_{dateStamp}.{Format}");
        }

        private static PSObject CreateFrameResultPSObject(string outputPath, uint frame, string videoPath)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("OutputPath", outputPath));
            obj.Properties.Add(new PSNoteProperty("Frame", frame));
            obj.Properties.Add(new PSNoteProperty("SourceFile", videoPath));
            obj.Properties.Add(new PSNoteProperty("Width", 0));
            obj.Properties.Add(new PSNoteProperty("Height", 0));
            obj.Properties.Add(new PSNoteProperty("DecoderType", ""));
            obj.Properties.Add(new PSNoteProperty("IsNewlyExtracted", false));

            if (File.Exists(outputPath))
            {
                var fi = new FileInfo(outputPath);
                obj.Properties.Add(new PSNoteProperty("FileSize", fi.Length));
            }

            return obj;
        }

        /// <summary>
        /// 核心提取逻辑：尝试选择合适的解码器，提取帧并保存为 PNG。
        /// </summary>
        private static PSObject ExtractAndSaveFrame(string videoPath, uint frame, string outputPath, DecoderSelectionMode decoderMode)
        {
            string decoderType = "";
            int width = 0, height = 0;
            bool newlyExtracted = false;

            switch (decoderMode)
            {
                case DecoderSelectionMode.Auto:
                    // 自动：尝试 8bit → 16bit → HDR
                    if (TryExtractWith8Bit(videoPath, frame, outputPath, out var r8, out var d8, out var w8, out var h8)
                        || TryExtractWith16Bit(videoPath, frame, outputPath, out r8, out d8, out w8, out h8)
                        || TryExtractWithHDR(videoPath, frame, outputPath, out r8, out d8, out w8, out h8))
                    {
                        decoderType = d8; width = w8; height = h8; newlyExtracted = r8;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"All decoders failed to extract frame #{frame} from '{videoPath}'. " +
                            "The file may be corrupted or in an unsupported format.");
                    }
                    break;

                case DecoderSelectionMode.Prefer8Bit:
                    TryExtractWith8Bit(videoPath, frame, outputPath, out var r8b, out var d8b, out var w8b, out var h8b);
                    decoderType = d8b; width = w8b; height = h8b; newlyExtracted = r8b;
                    break;

                case DecoderSelectionMode.Prefer16Bit:
                    TryExtractWith16Bit(videoPath, frame, outputPath, out var r16, out var d16, out var w16, out var h16);
                    decoderType = d16; width = w16; height = h16; newlyExtracted = r16;
                    break;

                case DecoderSelectionMode.HDR:
                    TryExtractWithHDR(videoPath, frame, outputPath, out var rH, out var dH, out var wH, out var hH);
                    decoderType = dH; width = wH; height = hH; newlyExtracted = rH;
                    break;
            }

            if (!newlyExtracted || width <= 0)
            {
                throw new InvalidOperationException(
                    $"Failed to extract frame #{frame} from '{videoPath}'. No suitable decoder could process this file.");
            }

            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("OutputPath", Path.GetFullPath(outputPath)));
            obj.Properties.Add(new PSNoteProperty("Frame", frame));
            obj.Properties.Add(new PSNoteProperty("SourceFile", videoPath));
            obj.Properties.Add(new PSNoteProperty("Width", width));
            obj.Properties.Add(new PSNoteProperty("Height", height));
            obj.Properties.Add(new PSNoteProperty("DecoderType", decoderType));
            obj.Properties.Add(new PSNoteProperty("IsNewlyExtracted", true));

            if (File.Exists(outputPath))
            {
                var fi = new FileInfo(outputPath);
                obj.Properties.Add(new PSNoteProperty("FileSize", fi.Length));
            }

            return obj;
        }

        private static bool TryExtractWith8Bit(string videoPath, uint frame, string outputPath,
            out bool success, out string decoderType, out int width, out int height)
        {
            success = false; decoderType = "DecoderContext8Bit"; width = 0; height = 0;
            try
            {
                using var decoder = new DecoderContext8Bit(videoPath);
                if (!decoder.Initialized)
                    return false;

                var picture = decoder.GetFrame(frame);
                if (picture == null || picture.Disposed)
                    return false;

                width = picture.Width;
                height = picture.Height;

                picture.SaveToPng(outputPath);
                picture.Dispose();
                success = true;
                Logger.Log($"[GetMediaFrame] 8-bit decoder extracted frame {frame} -> {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetMediaFrame] 8-bit decoder failed for frame {frame}: {ex.Message}");
                return false;
            }
        }

        private static bool TryExtractWith16Bit(string videoPath, uint frame, string outputPath,
            out bool success, out string decoderType, out int width, out int height)
        {
            success = false; decoderType = "DecoderContext16Bit"; width = 0; height = 0;
            try
            {
                using var decoder = new DecoderContext16Bit(videoPath);
                if (!decoder.Initialized)
                    return false;

                var picture = decoder.GetFrame(frame);
                if (picture == null || picture.Disposed)
                    return false;

                width = picture.Width;
                height = picture.Height;

                // 16bit → 8bit 转换后保存为 PNG
                picture.ToBitPerPixel(8).SaveToPng(outputPath);
                picture.Dispose();
                success = true;
                Logger.Log($"[GetMediaFrame] 16-bit decoder extracted frame {frame} -> {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetMediaFrame] 16-bit decoder failed for frame {frame}: {ex.Message}");
                return false;
            }
        }

        private static bool TryExtractWithHDR(string videoPath, uint frame, string outputPath,
            out bool success, out string decoderType, out int width, out int height)
        {
            success = false; decoderType = "HDRDecoderContext"; width = 0; height = 0;
            try
            {
                using var decoder = new HDRDecoderContext(videoPath);
                if (!decoder.Initialized)
                    return false;

                var hdrFrame = decoder.GetHDRFrame(frame, hasAlpha: false);
                if (hdrFrame == null || hdrFrame.Disposed)
                    return false;

                width = hdrFrame.Width;
                height = hdrFrame.Height;

                // HDR → SDR 后保存为 PNG
                var sdr = hdrFrame.DegradeToSDR();
                sdr.ToBitPerPixel(8).SaveToPng(outputPath);
                sdr.Dispose();
                hdrFrame.Dispose();
                success = true;
                Logger.Log($"[GetMediaFrame] HDR decoder extracted frame {frame} -> {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetMediaFrame] HDR decoder failed for frame {frame}: {ex.Message}");
                return false;
            }
        }
    }
}
