using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Shared
{
    public interface IPictureProcessStep
    {
        /// <summary>
        /// Name of the step.
        /// </summary>
        string Name { get; }
        /// <summary>
        /// The properties for this step.
        /// </summary>
        public Dictionary<string, object?> Properties { get; set; }
        /// <summary>
        /// Process this picture.
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        [Obsolete("Implement GetSixLaborsImageSharpProcess if possible for better performance, or Implement INormalEffect.Process if the step cannot be represented as a SixLabors.ImageSharp process. This method will be removed in API v5.", false)]
        public IPicture Process(IPicture source);

        /// <summary>
        /// Get the SixLabors.ImageSharp processing function for this step. If this step cannot be represented as a SixLabors.ImageSharp process, return null.
        /// </summary>
        /// <returns></returns>
        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return null;
        }

        /// <summary>
        /// Get the process stack information of this step. This is used for generating <see cref="IPicture.ProcessStack"/>.
        /// </summary>
        /// <returns></returns>
        public PictureProcessStack GetProcessStack();

    }

    public class PictureProcessStack
    {
        public required string OperationDisplayName { get; set; }
        public required Type? Operator { get; set; }
        public required StackTrace? ProcessingFuncStackTrace { get; set; }
        public IPictureProcessStep? StepUsed { get; set; }
        public Dictionary<string, object>? Properties { get; set; }
        public TimeSpan? Elapsed { get; set; }
        public string? Tag { get; set; }

        private static JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static string FormatProcessStackForLog(IEnumerable<PictureProcessStack>? processStack, int maxFramesPerStep = 12)
        {
            if (processStack == null) return "(null)";

            // Materialize once to avoid multiple enumeration and to preserve ordering.
            var steps = processStack as IList<PictureProcessStack> ?? processStack.ToList();
            if (steps.Count == 0) return "(empty)";

            var sb = new StringBuilder(capacity: 512);
            sb.AppendLine($"Steps: {steps.Count}");

            for (int i = 0; i < steps.Count; i++)
            {
                AppendProcessStep(sb, steps[i], i + 1, maxFramesPerStep, indent: "");
            }
            return sb.ToString();
        }

        // Markdown-formatted variant of the process-stack formatter.
        public static string FormatProcessStackForLogMarkdown(IEnumerable<PictureProcessStack>? processStack, int maxFramesPerStep = 12)
        {
            if (processStack == null) return "(null)";

            var steps = processStack as IList<PictureProcessStack> ?? processStack.ToList();
            if (steps.Count == 0) return "(empty)";

            var sb = new StringBuilder(capacity: 1024);
            sb.AppendLine($"# Process Steps ({steps.Count})");
            sb.AppendLine();

            for (int i = 0; i < steps.Count; i++)
            {
                AppendProcessStepMarkdown(sb, steps[i], i + 1, maxFramesPerStep, 0);
            }

            return sb.ToString();
        }

        private static void AppendProcessStepMarkdown(StringBuilder sb, PictureProcessStack step, int index, int maxFramesPerStep, int indentLevel)
        {
            if (step == null)
            {
                sb.AppendLine($"## #{index} <null>");
                return;
            }

            int baseLevel = Math.Min(6, 2 + indentLevel);
            sb.AppendLine(new string('#', baseLevel) + " " + index + ". " + (step.OperationDisplayName ?? "(no name)"));
            sb.AppendLine();

            if (step.Operator != null)
            {
                sb.AppendLine("- **Operator:** " + step.Operator.FullName);
            }
            if (step.Elapsed != null)
            {
                sb.AppendLine("- **Elapsed:** " + step.Elapsed);
            }

            if (step.StepUsed != null)
            {
                var line = "- **Step:** " + step.StepUsed.GetType().FullName;
                if (!string.IsNullOrWhiteSpace(step.StepUsed.Name)) line += " (\"" + step.StepUsed.Name + "\")";
                sb.AppendLine(line);
            }

            if (step.Properties != null && step.Properties.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Properties:**");
                foreach (var kv in step.Properties.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    sb.AppendLine($"- **{kv.Key}:** {FormatPropertyValueForLog(kv.Value)}");
                }
            }

            if (step is OverlayedPictureProcessStack overlay)
            {
                if (overlay.TopSteps != null && overlay.TopSteps.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(new string('#', Math.Min(6, baseLevel + 1)) + " TopSteps:");
                    for (int i = 0; i < overlay.TopSteps.Count; i++)
                    {
                        AppendProcessStepMarkdown(sb, overlay.TopSteps[i], i + 1, maxFramesPerStep, indentLevel + 2);
                    }
                }
                if (overlay.BaseSteps != null && overlay.BaseSteps.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(new string('#', Math.Min(6, baseLevel + 1)) + " BaseSteps:");
                    for (int i = 0; i < overlay.BaseSteps.Count; i++)
                    {
                        AppendProcessStepMarkdown(sb, overlay.BaseSteps[i], i + 1, maxFramesPerStep, indentLevel + 2);
                    }
                }
            }

            AppendStackTraceForLogMarkdown(sb, step.ProcessingFuncStackTrace, maxFramesPerStep);

            sb.AppendLine();
        }

        private static void AppendStackTraceForLogMarkdown(StringBuilder sb, StackTrace? trace, int maxFrames)
        {
            if (trace == null) return;
            var frames = trace.GetFrames();
            if (frames == null || frames.Length == 0) return;

            sb.AppendLine();
            sb.AppendLine("**CallStack:**");
            sb.AppendLine();
            sb.AppendLine("```text");

            int take = Math.Min(frames.Length, Math.Max(0, maxFrames));
            for (int i = 0; i < take; i++)
            {
                var frame = frames[i];
                var method = frame.GetMethod();
                string methodName = method == null
                    ? "(unknown)"
                    : $"{method.DeclaringType?.FullName}.{method.Name}";

                string? file = frame.GetFileName();
                int line = frame.GetFileLineNumber();
                if (!string.IsNullOrWhiteSpace(file) && line > 0)
                {
                    sb.AppendLine($"{i + 1}. {methodName} ({System.IO.Path.GetFileName(file)}:{line})");
                }
                else
                {
                    sb.AppendLine($"{i + 1}. {methodName}");
                }
            }

            if (frames.Length > take)
            {
                sb.AppendLine($"... {frames.Length - take} more");
            }

            sb.AppendLine("```");
        }

        private static void AppendProcessStep(StringBuilder sb, PictureProcessStack step, int index, int maxFramesPerStep, string indent)
        {
            if (step == null)
            {
                sb.Append(indent).Append('#').Append(index).AppendLine(" <null>");
                return;
            }

            sb.Append(indent).Append('#').Append(index).Append(' ')
                .Append(step.OperationDisplayName ?? "(no name)");

            if (step.Operator != null)
            {
                sb.Append("  [Operator: ").Append(step.Operator.FullName).Append(']');
            }
            if (step.Elapsed != null)
            {
                sb.AppendLine();
                sb.Append(indent).Append("  Elapsed: ").Append(step.Elapsed);

            }
            sb.AppendLine();

            if (step.StepUsed != null)
            {
                sb.Append(indent).Append("  Step: ").Append(step.StepUsed.GetType().FullName);
                if (!string.IsNullOrWhiteSpace(step.StepUsed.Name)) sb.Append(" (\"").Append(step.StepUsed.Name).Append("\")");
                sb.AppendLine();
            }

            if (step.Properties != null && step.Properties.Count > 0)
            {
                sb.Append(indent).AppendLine("  Properties:");
                foreach (var kv in step.Properties.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    sb.Append(indent).Append("    - ").Append(kv.Key).Append(": ").AppendLine(FormatPropertyValueForLog(kv.Value));
                }
            }

            // Special-case overlay stacks to keep them readable.
            if (step is OverlayedPictureProcessStack overlay)
            {
                if (overlay.TopSteps != null && overlay.TopSteps.Count > 0)
                {
                    sb.Append(indent).AppendLine("  TopSteps:");
                    for (int i = 0; i < overlay.TopSteps.Count; i++)
                    {
                        AppendProcessStep(sb, overlay.TopSteps[i], i + 1, maxFramesPerStep, indent + "    ");
                    }
                }
                if (overlay.BaseSteps != null && overlay.BaseSteps.Count > 0)
                {
                    sb.Append(indent).AppendLine("  BaseSteps:");
                    for (int i = 0; i < overlay.BaseSteps.Count; i++)
                    {
                        AppendProcessStep(sb, overlay.BaseSteps[i], i + 1, maxFramesPerStep, indent + "    ");
                    }
                }
            }

            AppendStackTraceForLog(sb, step.ProcessingFuncStackTrace, maxFramesPerStep, indent);
        }

        private static void AppendStackTraceForLog(StringBuilder sb, StackTrace? trace, int maxFrames, string indent)
        {
            if (trace == null) return;
            var frames = trace.GetFrames();
            if (frames == null || frames.Length == 0) return;

            sb.Append(indent).AppendLine("  CallStack:");

            int take = Math.Min(frames.Length, Math.Max(0, maxFrames));
            for (int i = 0; i < take; i++)
            {
                var frame = frames[i];
                var method = frame.GetMethod();
                string methodName = method == null
                    ? "(unknown)"
                    : $"{method.DeclaringType?.FullName}.{method.Name}";

                string? file = frame.GetFileName();
                int line = frame.GetFileLineNumber();
                if (!string.IsNullOrWhiteSpace(file) && line > 0)
                {
                    sb.Append(indent).Append("    ").Append(i + 1).Append(". ").Append(methodName)
                        .Append(" (").Append(System.IO.Path.GetFileName(file)).Append(':').Append(line).Append(")")
                        .AppendLine();
                }
                else
                {
                    sb.Append(indent).Append("    ").Append(i + 1).Append(". ").Append(methodName).AppendLine();
                }
            }

            if (frames.Length > take)
            {
                sb.Append(indent).Append("    ... ").Append(frames.Length - take).AppendLine(" more");
            }
        }

        private static string FormatPropertyValueForLog(object? value)
        {
            if (value == null) return "(null)";
            if (value is string s) return s;
            if (value is Type t) return t.FullName ?? t.Name;
            if (value is StackTrace st) return st.ToString();
            if (value is Exception ex) return ex.ToString();

            // Avoid huge dumps for common collections; show count + a short preview.
            if (value is System.Collections.ICollection coll && value is not Array)
            {
                return $"{value.GetType().Name} (Count={coll.Count})";
            }

            try
            {
                // Best-effort JSON for anonymous/complex objects.
                if (value is not ValueType)
                {
                    return JsonSerializer.Serialize(value, options);
                }
            }
            catch
            {
                // ignore and fall back to ToString
            }

            return value.ToString() ?? value.GetType().FullName ?? "(unknown)";
        }
    }

    public class OverlayedPictureProcessStack : PictureProcessStack
    {
        public required List<PictureProcessStack> TopSteps { get; set; }
        public required List<PictureProcessStack> BaseSteps { get; set; }
    }

    public static class PictureProcesser
    {
        public static bool SaveDiagResult = false;
        public static string DiagResultPath = null!;
        public static bool EnableLogProcessStack = true;

        public static IPicture Process(List<IPictureProcessStep> steps, IPicture source, int targetPPB)
        {
            Guid SessionId = Guid.NewGuid();
            if (SaveDiagResult) source.SaveAsPng(Path.Combine(DiagResultPath, $"diag-before-{SessionId}.png"));
            var swTotal = Stopwatch.StartNew();
            List<PictureProcessStack> procStack = new(steps.Count);
            List<(Func<IImageProcessingContext, IImageProcessingContext> processer, IPictureProcessStep step)> processingContexts = new(steps.Count);
            var convertedSource = source.ToBitPerPixel(targetPPB);
            try
            {
                var img = convertedSource.SaveToSixLaborsImage(targetPPB, true, false);
                try
                {
                    foreach (var item in steps)
                    {
                        var step = item.GetSixLaborsImageSharpProcess();
                        if (step is not null)
                        {
                            var stack = item.GetProcessStack();
                            processingContexts.Add((step, item));
                        }
                        else
                        {
                            if (processingContexts.Count > 0)
                            {
                                img = ProcessSixLaborsProcessingContexts(img, processingContexts, ref procStack, SessionId);
                                processingContexts.Clear();
                            }
                            using var inputPicture = img.ToPJFCPicture(targetPPB);
                            var sw = Stopwatch.StartNew();
                            var outputPicture = item.Process(inputPicture);
                            sw.Stop();
                            try
                            {
                                img.Dispose();
                                img = outputPicture.SaveToSixLaborsImage(targetPPB, true, false);
                            }
                            finally
                            {
                                if (!ReferenceEquals(outputPicture, inputPicture))
                                {
                                    outputPicture.Dispose();
                                }
                            }
                            if (EnableLogProcessStack)
                            {
                                var stack = item.GetProcessStack();
                                stack.Elapsed = sw.Elapsed;
                                procStack.Add(stack);
                            }
                        }
                    }

                    if (processingContexts.Count > 0)
                    {
                        img = ProcessSixLaborsProcessingContexts(img, processingContexts, ref procStack, SessionId);
                    }
                    var result = img.ToPJFCPicture(targetPPB);
                    if (EnableLogProcessStack)
                    {
                        swTotal.Stop();
                        var dirtyTime = swTotal.Elapsed - procStack.Where(s => s.Elapsed.HasValue).Aggregate(TimeSpan.Zero, (a, b) => a + b.Elapsed!.Value);
                        procStack.Add(new PictureProcessStack
                        {
                            OperationDisplayName = "Dirty time spent on PictureProcesser",
                            ProcessingFuncStackTrace = null,
                            Operator = null,
                            Elapsed = dirtyTime,
                            StepUsed = null,
                        });
                        result.ProcessStack = source.ProcessStack.Concat(procStack).ToList();
                        if (SaveDiagResult) result.SaveAsPng(Path.Combine(DiagResultPath, $"diag-after-{SessionId}.png"));
                    }
                    return result;
                }
                finally
                {
                    img.Dispose();
                }
            }
            finally
            {
                if (!ReferenceEquals(convertedSource, source))
                {
                    convertedSource.Dispose();
                }
                source.Dispose();
            }
        }

        private static Image ProcessSixLaborsProcessingContexts(Image img, List<(Func<IImageProcessingContext, IImageProcessingContext> processer, IPictureProcessStep step)> processingContexts, ref List<PictureProcessStack> stacks, Guid SessionId)
        {
            foreach (var process in processingContexts)
            {
                var stack = process.step.GetProcessStack();
                var sw = Stopwatch.StartNew();
                img.Mutate((c) => process.processer(c));
                sw.Stop();
                stack.Elapsed = sw.Elapsed;
                stacks.Add(stack);
                if (SaveDiagResult)
                {
                    var swSaveDiag = Stopwatch.StartNew();
                    var opId = Guid.NewGuid();
                    img.SaveAsPng(Path.Combine(DiagResultPath, $"diag-{stack.OperationDisplayName}-{opId}.png"));
                    File.WriteAllText(Path.Combine(DiagResultPath, $"diag-{SessionId}-{stack.OperationDisplayName}-{opId}-stacks.txt"), PictureProcessStack.FormatProcessStackForLog(stacks, 50));
                    swSaveDiag.Stop();
                    stacks.Add(new PictureProcessStack
                    {
                        OperationDisplayName = $"Save diag result for {stack.OperationDisplayName}",
                        ProcessingFuncStackTrace = null,
                        Operator = typeof(PictureProcesser),
                        Elapsed = swSaveDiag.Elapsed,
                        StepUsed = null,
                    });
                }
            }

            return img;
        }
    }
}
