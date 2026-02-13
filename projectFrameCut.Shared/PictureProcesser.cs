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
    }

    public class OverlayedPictureProcessStack : PictureProcessStack
    {
        public required List<PictureProcessStack> TopSteps { get; set; }
        public required List<PictureProcessStack> BaseSteps { get; set; }
    }

    public static class PictureProcesser
    {
        public static IPicture Process(List<IPictureProcessStep> steps, IPicture source, int targetPPB)
        {
            List<PictureProcessStack> procStack = new(steps.Count);
            List<Func<IImageProcessingContext, IImageProcessingContext>> processingContexts = new(steps.Count);
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
                            if (IPicture.DiagImagePath is not null) Logger.LogDiagnostic(PictureExtensions.FormatProcessStackForLog(procStack.Concat([stack])));
                            processingContexts.Add(ctx =>
                            {
                                var sw = Stopwatch.StartNew();
                                var res = step(ctx);
                                sw.Stop();
                                stack.Elapsed = sw.Elapsed;
                                return res;
                            });
                            procStack.Add(stack);
                        }
                        else
                        {
                            //Logger.LogDiagnostic($"Step {item.Name} doesn't have a IImageProcessingContext. Process the picture and convert it...");
                            if (processingContexts.Count > 0)
                            {
                                img = ProcessSixLaborsProcessingContexts(img, processingContexts);
                                processingContexts.Clear();
                            }

                            using var inputPicture = img.ToPJFCPicture(targetPPB);
                            var outputPicture = item.Process(inputPicture);
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

                            procStack.Add(item.GetProcessStack());
                        }
                    }

                    if (processingContexts.Count > 0)
                    {
                        img = ProcessSixLaborsProcessingContexts(img, processingContexts);
                    }

                    var result = img.ToPJFCPicture(targetPPB);
                    result.ProcessStack = source.ProcessStack.Concat(procStack).ToList();
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

        private static Image ProcessSixLaborsProcessingContexts(Image img, List<Func<IImageProcessingContext, IImageProcessingContext>> processingContexts)
        {
            img.Mutate(ctx =>
            {
                foreach (var process in processingContexts)
                {
                    ctx = process(ctx);
                }
            });
            return img;
        }
    }
}
