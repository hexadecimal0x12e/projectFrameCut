using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Render.Rendering
{
    public static class EffectProcessing
    {
        /// <summary>
        /// 从缓存中获取值，优先从帧缓存获取，然后从全局缓存获取
        /// </summary>
        private static object GetCachedValue(string key, Dictionary<string, object> frameLocalCache, ConcurrentDictionary<string, object> globalResultCache)
        {
            if (frameLocalCache.TryGetValue(key, out var value))
            {
                return value;
            }
            if (globalResultCache.TryGetValue(key, out value))
            {
                return value;
            }
            throw new KeyNotFoundException($"Cached value with key '{key}' not found in either frame-local or global cache.");
        }

        public static bool ProcessBindableArgsEffect(uint targetFrame, ref IPicture frame, ref ConcurrentDictionary<string, object> globalResultCache, Dictionary<string, object> frameLocalCache, IClip clip, IBindableArgumentEffect item, IComputer? computer, int width, int height)
        {
            switch (item.EffectRole)
            {
                case BindableArgumentEffectType.ValueProvider:
                    if (item is not IBindableArgumentEffectValueProvider vp) throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
                    {
                        ArgumentNullException.ThrowIfNull(vp.Id, "Id");
                        var value = vp.GenerateValue(frame, computer, width, height);
                        // 根据 GenerateOnce 决定存储位置
                        if (vp.GenerateOnce)
                        {
                            globalResultCache[item.Id] = value; // 存储到全局缓存
                        }
                        else
                        {
                            frameLocalCache[item.Id] = value; // 存储到帧缓存
                        }
                        return vp.GenerateOnce;
                    }
                case BindableArgumentEffectType.NoInputValueProvider:
                    if (item is not IBindableArgumentEffectNoInputValueProvider nip) throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
                    {
                        ArgumentNullException.ThrowIfNull(nip.Id, "Id");
                        var value = nip.GenerateValue(computer, width, height);
                        // 根据 GenerateOnce 决定存储位置
                        if (nip.GenerateOnce)
                        {
                            globalResultCache[item.Id] = value; // 存储到全局缓存
                        }
                        else
                        {
                            frameLocalCache[item.Id] = value; // 存储到帧缓存
                        }
                        return nip.GenerateOnce;
                    }
                case BindableArgumentEffectType.OneInputValueProcessor:
                    if (item is not IBindableArgumentEffectOneToOneValueProcesser vproc) throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
                    {
                        ArgumentNullException.ThrowIfNull(vproc.BindedArgumentProviderID, "BindedArgumentProviderID");
                        var inputValue = GetCachedValue(vproc.BindedArgumentProviderID, frameLocalCache, globalResultCache);
                        var processedValue = vproc.ProcessValue(inputValue, computer, width, height);
                        // 处理后的值存储到原值所在的位置
                        if (frameLocalCache.ContainsKey(vproc.BindedArgumentProviderID))
                        {
                            frameLocalCache[item.BindedArgumentProviderID] = processedValue;
                        }
                        else
                        {
                            globalResultCache[item.BindedArgumentProviderID] = processedValue;
                        }
                    }
                    break;
                case BindableArgumentEffectType.ManyInputValueProcessor:
                    if (item is not IBindableArgumentEffectManyToOneValueProcesser mvproc) throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
                    {
                        ArgumentNullException.ThrowIfNull(mvproc.Id, "Id");
                        ArgumentNullException.ThrowIfNull(mvproc.BindedArgumentProviderIDs, "BindedArgumentProviderIDs");
                        object[] sources = new object[mvproc.BindedArgumentProviderIDs.Length];
                        for (int i = 0; i < mvproc.BindedArgumentProviderIDs.Length; i++)
                        {
                            sources[i] = GetCachedValue(mvproc.BindedArgumentProviderIDs[i], frameLocalCache, globalResultCache);
                        }
                        var result = mvproc.ProcessValues(sources, computer, width, height);
                        if (mvproc.GenerateOnce)
                        {
                            globalResultCache[item.Id] = result;
                        }
                        else
                        {
                            frameLocalCache[item.Id] = result;
                        }
                        return mvproc.GenerateOnce;
                    }
                    
                case BindableArgumentEffectType.OneInputResultGenerator:
                    if (item is not IBindableArgumentEffectOneInputResultGenerator crg) throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
                    {
                        ArgumentNullException.ThrowIfNull(crg.BindedArgumentProviderID, "BindedArgumentProviderID");
                        var cachedValue = GetCachedValue(crg.BindedArgumentProviderID, frameLocalCache, globalResultCache);
                        if (crg.IsContinuous && (crg.EndPoint == 0 && crg.EndPoint == 0))
                        {
                            crg.StartPoint = (int)(clip.StartFrame);
                            crg.EndPoint = (int)(crg.StartPoint + clip.GetEffectiveDuration());
                        }
                        frame = crg.GenerateResult(cachedValue, targetFrame, frame, computer, width, height);
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
            }
            return false; // 默认情况下不删除
        }


        // ---- 水印缓存（按源尺寸缓存不同分辨率的渲染结果） ----
        private static readonly Dictionary<(int Width, int Height), IPicture> _watermarkCache = new();
        private static readonly List<(int, int)> _watermarkKeys = new(); // FIFO 淘汰顺序
        private const int WatermarkCacheMaxSize = 8;
        private static float _measuredTextPixelW, _measuredTextPixelH;
        private static bool _haveTextMeasure;
        private static readonly object _watermarkSync = new();

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static IPicture ProcessAIWatermark(IPicture src, uint? frameIndex = null)
        {
            var sw = Stopwatch.StartNew();
            var key = (src.Width, src.Height);

            IPicture? overlay;
            lock (_watermarkSync)
            {
                if (!_watermarkCache.TryGetValue(key, out overlay))
                {
                    if (_watermarkCache.Count >= WatermarkCacheMaxSize)
                    {
                        var staleKey = _watermarkKeys[0];
                        _watermarkKeys.RemoveAt(0);
                        if (_watermarkCache.Remove(staleKey, out var staleFrame))
                            staleFrame.Dispose(true);
                    }
                    overlay = BuildWatermarkOverlay(src.Width, src.Height);
                    overlay.CanBeDisposed = false;
                    _watermarkCache[key] = overlay;
                    _watermarkKeys.Add(key);
                }
            }

            var computer = PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false);
            var result = ClassicOverlayMixture.Default.Mix(src, overlay, computer, overlay.BitPerPixel);
            sw.Stop();
            result.ProcessStack = new List<PictureProcessStack>(src.ProcessStack)
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "Add AI Watermark",
                    Operator = typeof(EffectProcessing),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Elapsed = sw.Elapsed
                }
            };
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static IPicture BuildWatermarkOverlay(int width, int height)
        {
            const string text = "Generated by AI";
            const float fontSize = 24;
            var fontFamily = TextClipFontRegistry.FallbackFamilyName ?? "Arial";
            const float padding = 10;

            // 只测量一次文本像素尺寸（文字和字体不会在运行时改变）
            float measuredW, measuredH;
            if (!_haveTextMeasure)
            {
                if (TextClipFontRegistry.TryGetFont(fontFamily, out var fontFace) && fontFace is not null)
                {
                    var entry = new TextEntry
                    {
                        Text = text,
                        FontName = fontFamily,
                        FontSize = fontSize / MathF.Min(width, height),
                        LineSpacing = 0f,
                    };
                    var engine = new NormalTypesettingEngine();
                    (var w, var h) = engine.Measure(entry, fontFace);
                    _measuredTextPixelW = w * MathF.Min(width, height);
                    _measuredTextPixelH = h * MathF.Min(width, height);
                    _haveTextMeasure = true;
                }
            }
            measuredW = _haveTextMeasure ? _measuredTextPixelW : text.Length * fontSize * 0.6f;
            measuredH = _haveTextMeasure ? _measuredTextPixelH : fontSize;

            var clip = new TextClip
            {
                Id = Guid.Empty, Name = "", LayerIndex = 0,
                TextEntries = new List<TextEntry>
                {
                    new TextEntry
                    {
                        Text = text,
                        X = (int)(width - measuredW - padding),
                        Y = (int)(height - measuredH - padding),
                        FontName = fontFamily,
                        FontSize = fontSize,
                        FillR = 65535, FillG = 65535, FillB = 65535, FillA = 0.5f
                    }
                },
            };
            return clip.GetFrameRelativeToStartPointOfSource(0, width, height, true, 8);
        }
    }
}
