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
            List<PictureProcessStack> procStack = new();
            var img = source.ToBitPerPixel(targetPPB).SaveToSixLaborsImage(targetPPB, true, false);
            List<Func<IImageProcessingContext, IImageProcessingContext>> processingContexts = new();
            foreach (var item in steps)
            {
                var step = item.GetSixLaborsImageSharpProcess();
                if (step is not null)
                {
                    var stack = item.GetProcessStack();
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
                    img = ProcessSixLaborsProcessingContexts(img, processingContexts);
                    processingContexts.Clear();
                    img = item.Process(img.ToPJFCPicture(targetPPB)).SaveToSixLaborsImage(targetPPB, true, false);
                    procStack.Add(item.GetProcessStack());
                }
            }
            if (processingContexts.ListAny())
            {
                img = ProcessSixLaborsProcessingContexts(img, processingContexts);
            }
            source.Dispose();
            var result = img.ToPJFCPicture(targetPPB);
            result.ProcessStack = source.ProcessStack.Concat(procStack).ToList();
            return result;
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
