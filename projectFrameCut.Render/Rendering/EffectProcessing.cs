using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Render.Rendering
{
    internal static class EffectProcessing
    {
        public static void ProcessEffect(ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IEffect item, IComputer? computer, int width, int height)
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
                c.EndPoint = (int)(c.StartPoint + clip.Duration * clip.SecondPerFrameRatio);
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

        public static void ProcessBindableArgsEffect(uint targetFrame, ref IPicture frame, ref Dictionary<string, object> resultCache, ref Dictionary<string, bool> producedValueTable, IClip clip, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IBindableArgumentEffect item, IComputer? computer, int width, int height)
        {
            switch (item.EffectRole)
            {
                case BindableArgumentEffectType.ValueProvider:
                    if (item is IBindableArgumentEffectValueProvider vp)
                    {
                        if (vp.GenerateOnce && producedValueTable.ContainsKey(item.Id)) break;
                        producedValueTable[item.Id] = true;
                        ArgumentNullException.ThrowIfNull(item.Id, "Id");
                        resultCache.Add(item.Id, vp.GenerateValue(frame, computer, width, height));
                    }
                    break;
                case BindableArgumentEffectType.ValueProcessor:
                    if (item is IBindableArgumentEffectValueProcesser vproc)
                    {
                        ArgumentNullException.ThrowIfNull(item.BindedArgumentProviderID, "BindedArgumentProviderID");
                        resultCache[item.BindedArgumentProviderID] = vproc.ProcessValue(resultCache[item.BindedArgumentProviderID], computer, width, height);
                    }
                    break;
                case BindableArgumentEffectType.MultipleInputValueProcessor:
                    if (item is IBindableArgumentEffectMultipleValueProcesser mvproc)
                    {
                        ArgumentNullException.ThrowIfNull(mvproc.Id, "Id");
                        ArgumentNullException.ThrowIfNull(mvproc.BindedArgumentProviderIDs, "BindedArgumentProviderIDs");
                        object[] sources = new object[mvproc.BindedArgumentProviderIDs.Length];
                        for (int i = 0; i < mvproc.BindedArgumentProviderIDs.Length; i++)
                        {
                            sources[i] = resultCache[mvproc.BindedArgumentProviderIDs[i]];
                        }
                        resultCache.Add(mvproc.Id, mvproc.ProcessValues(sources, computer, width, height));
                    }
                    break;
                case BindableArgumentEffectType.ResultGenerator:
                    if (item is IBindableArgumentEffectNormalResultGenerator rg)
                    {
                        ArgumentNullException.ThrowIfNull(item.BindedArgumentProviderID, "BindedArgumentProviderID");
                        if (item.YieldProcessStep)
                        {
                            lastIsProcessStep = true;
                            try
                            {
                                var step = rg.GenerateResultStep(resultCache[item.BindedArgumentProviderID], width, height);
                                steps.Add(step);
                                if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                            }
                            catch (Exception ex)
                            {
                                Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                                lastIsProcessStep = false;
                                frame = rg.GenerateResult(resultCache[item.BindedArgumentProviderID], frame, computer, width, height);
                            }
                        }
                        else
                        {
                            frame = rg.GenerateResult(resultCache[item.BindedArgumentProviderID], frame, computer, width, height);
                        }
                    }
                    break;
                case BindableArgumentEffectType.ContinuousResultGenerator:
                    if (item is IBindableArgumentEffectContinuesResultGenerator crg)
                    {
                        ArgumentNullException.ThrowIfNull(item.BindedArgumentProviderID, "BindedArgumentProviderID");
                        if (item.YieldProcessStep)
                        {
                            lastIsProcessStep = true;
                            try
                            {
                                if (crg.EndPoint == 0 && crg.EndPoint == 0)
                                {
                                    crg.StartPoint = (int)(clip.StartFrame);
                                    crg.EndPoint = (int)(crg.StartPoint + clip.Duration * clip.SecondPerFrameRatio);
                                }
                                var step = crg.GenerateResultStep(resultCache[item.BindedArgumentProviderID], targetFrame, width, height);
                                steps.Add(step);
                                if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                            }
                            catch (Exception ex)
                            {
                                Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                                lastIsProcessStep = false;
                                frame = crg.GenerateResult(resultCache[item.BindedArgumentProviderID], targetFrame, frame, computer, width, height);
                            }
                        }
                        else
                        {
                            frame = crg.GenerateResult(resultCache[item.BindedArgumentProviderID], targetFrame, frame, computer, width, height);
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported BindableArgumentEffectType {item.EffectRole} in IBindableArgumentEffect {item.Name}.");
            }
        }
    }
}
