using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.Fonts;
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

        public static void ProcessEffect(ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, INormalEffect item, IComputer? computer, int width, int height)
        {
            if (item.YieldProcessStep)
            {
                lastIsProcessStep = true;
                try
                {
                    var step = item.GetStep(frame, width, height);
                    steps.Add(step);
                    if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                }
                catch (Exception ex)
                {
                    Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                    lastIsProcessStep = false;
                    frame = item.Render(frame, computer, width, height);
                }
            }
            else
            {
                frame = item.Render(frame, computer, width, height);
            }
        }

        public static void ProcessContinuousEffect(uint targetFrame, IClip clip, IComputer? computer, ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IEffect item, IContinuousEffect c, int width, int height)
        {
            if (c.EndPoint == 0 && c.EndPoint == 0)
            {
                c.StartPoint = (int)(clip.StartFrame);
                c.EndPoint = (int)(c.StartPoint + clip.GetEffectiveDuration());
            }
            if (c.YieldProcessStep)
            {
                lastIsProcessStep = true;
                try
                {
                    var step = c.GetStep(frame, targetFrame, width, height);
                    steps.Add(step);
                    if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {c.Name}({c.TypeName}) : {step.GetProcessStack()}");

                }
                catch (Exception ex)
                {
                    Log($"[Render] WARN: Failed to get process steps for continuous effect {c.Name}: {ex}");
                    lastIsProcessStep = false;
                    frame = c.Render(frame, targetFrame, computer, width, height);
                }

            }
            else
            {
                frame = c.Render(frame, targetFrame, computer, width, height);
            }
        }

        public static bool ProcessBindableArgsEffect(uint targetFrame, ref IPicture frame, ref ConcurrentDictionary<string, object> globalResultCache, Dictionary<string, object> frameLocalCache, IClip clip, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IBindableArgumentEffect item, IComputer? computer, int width, int height)
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
                //case BindableArgumentEffectType.ResultGenerator:
                //    if (item is IBindableArgumentEffectOneInputResultGenerator rg)
                //    {
                //        ArgumentNullException.ThrowIfNull(item.BindedArgumentProviderID, "BindedArgumentProviderID");
                //        var cachedValue = GetCachedValue(item.BindedArgumentProviderID, frameLocalCache, globalResultCache);
                //        if (item.YieldProcessStep)
                //        {
                //            lastIsProcessStep = true;
                //            try
                //            {
                //                var step = rg.GenerateResultStep(cachedValue, width, height);
                //                steps.Add(step);
                //                if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                //            }
                //            catch (Exception ex)
                //            {
                //                Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                //                lastIsProcessStep = false;
                //                frame = rg.GenerateResult(cachedValue, frame, computer, width, height);
                //            }
                //        }
                //        else
                //        {
                //            frame = rg.GenerateResult(cachedValue, frame, computer, width, height);
                //        }
                //    }
                //    break;
                case BindableArgumentEffectType.OneInputResultGenerator:
                    if (item is not IBindableArgumentEffectOneInputResultGenerator crg) throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
                    {
                        ArgumentNullException.ThrowIfNull(crg.BindedArgumentProviderID, "BindedArgumentProviderID");
                        var cachedValue = GetCachedValue(crg.BindedArgumentProviderID, frameLocalCache, globalResultCache);
                        if (item.YieldProcessStep)
                        {
                            lastIsProcessStep = true;
                            try
                            {
                                if (crg.IsContinuous && (crg.EndPoint == 0 && crg.EndPoint == 0))
                                {
                                    crg.StartPoint = (int)(clip.StartFrame);
                                    crg.EndPoint = (int)(crg.StartPoint + clip.GetEffectiveDuration());
                                }
                                var step = crg.GenerateResultStep(cachedValue, targetFrame, width, height);
                                steps.Add(step);
                                if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                            }
                            catch (Exception ex)
                            {
                                Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                                lastIsProcessStep = false;
                                frame = crg.GenerateResult(cachedValue, targetFrame, frame, computer, width, height);
                            }
                        }
                        else
                        {
                            frame = crg.GenerateResult(cachedValue, targetFrame, frame, computer, width, height);
                        }
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
            string fontFamily = "Arial";
            float padding = 10; // 距离边缘的边距

            // 测量文本尺寸
            var font = SystemFonts.CreateFont(fontFamily, fontSize, FontStyle.Regular);
            var textOptions = new TextOptions(font);
            var textSize = TextMeasurer.MeasureSize(watermarkText, textOptions);

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
                        x = (int)(src.Width - textSize.Width - padding),
                        y = (int)(src.Height - textSize.Height - padding),
                        fontFamily = fontFamily,
                        fontSize = fontSize,
                        fontStyle = FontStyle.Regular,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 0.5f
                    }
                },
            };
            var frame = wtmkClip.GetFrameRelativeToStartPointOfSource(0, src.Width, src.Height, true, 8);
            var result = ClassicOverlayMixture.Default.Mix(src, frame, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId, false), frame.bitPerPixel);
            result.ProcessStack = src.ProcessStack.Append(new PictureProcessStack { OperationDisplayName = "Add AI Watermark", Operator = typeof(EffectProcessing), ProcessingFuncStackTrace = new StackTrace(true), Elapsed = sw.Elapsed }).ToList();
            return result;
        }
    }
}
