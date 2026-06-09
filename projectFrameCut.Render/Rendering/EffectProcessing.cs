using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks;
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
                    break;
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


        public static IPicture ProcessAIWatermark(IPicture src, uint? frameIndex = null)
        {
            Stopwatch sw = Stopwatch.StartNew();

            // 定义文本内容和样式
            string watermarkText = "Generated by AI";
            float fontSize = 24;
            var fontFamily = TextClipFontRegistry.FallbackFamilyName ?? "Arial";
            float padding = 10; // 距离边缘的边距

            // 测量文本尺寸（通过 NormalTypesettingEngine）
            float measuredWidth = watermarkText.Length * fontSize * 0.6f;
            float measuredHeight = fontSize;
            if (TextClipFontRegistry.TryGetFont(fontFamily, out var fontFace) && fontFace is not null)
            {
                var measureEntry = new TextEntry
                {
                    Text = watermarkText,
                    FontName = fontFamily,
                    FontSize = fontSize / MathF.Min(src.Width, src.Height),
                    LineSpacing = 0f,
                };
                var engine = new NormalTypesettingEngine();
                var (w, h) = engine.Measure(measureEntry, fontFace);
                measuredWidth = w * MathF.Min(src.Width, src.Height);
                measuredHeight = h * MathF.Min(src.Width, src.Height);
            }

            var wtmkClip = new TextClip
            {
                Id = "",
                Name = "",
                LayerIndex = 0,
                TextEntries = new List<TextClipEntry>
                {
                    new TextClipEntry
                    {
                        text = watermarkText,
                        x = (int)(src.Width - measuredWidth - padding),
                        y = (int)(src.Height - measuredHeight - padding),
                        fontFamily = fontFamily,
                        fontSize = fontSize,
                        fontStyle = ClipFontStyle.Regular,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 0.5f
                    }
                },
            };
            var frame = wtmkClip.GetFrameRelativeToStartPointOfSource(0, src.Width, src.Height, true, 8);
            var result = ClassicOverlayMixture.Default.Mix(src, frame, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false), frame.BitPerPixel);
            result.ProcessStack = src.ProcessStack.Append(new PictureProcessStack { OperationDisplayName = "Add AI Watermark", Operator = typeof(EffectProcessing), ProcessingFuncStackTrace = new StackTrace(true), Elapsed = sw.Elapsed }).ToList();
            return result;
        }
    }
}
