using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using projectFrameCut.Drawing.Base;

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
        /// Get the SixLabors.ImageSharp processing function for this step.
        /// </summary>
        /// <returns></returns>
        public Func<IImageProcessingContext, IImageProcessingContext> GetSixLaborsImageSharpProcess();

        /// <summary>
        /// Get the process stack information of this step. This is used for generating <see cref="IPicture.ProcessStack"/>.
        /// </summary>
        /// <returns></returns>
        public PictureProcessStack GetProcessStack();

    }


    public static class PictureProcesser
    {
        public static bool SaveDiagResult = false;
        public static string DiagResultPath = null!;
        public static bool EnableLogProcessStack = true;

        public static IPicture Process(List<IPictureProcessStep> steps, IPicture source, int targetPPB)
        {
            Guid SessionId = Guid.NewGuid();
            if (SaveDiagResult) source.SaveToDisk(Path.Combine(DiagResultPath, $"diag-before-{SessionId}.png"), global::projectFrameCut.Drawing.Base.PictureExtensions.SharedPngPictureEncoder);
            List<PictureProcessStack> procStack = new(steps.Count);
            var convertedSource = source.ToBitPerPixel(targetPPB);
            try
            {
                var img = convertedSource.SaveToSixLaborsImage(targetPPB, true, false);
                try
                {
                    ProcessSixLaborsProcessingContexts(ref img, steps, ref procStack, SessionId);
                    var result = img.ToPJFCPicture(targetPPB);
                    if (EnableLogProcessStack)
                    {
                        result.ProcessStack = source.ProcessStack.Concat(procStack).ToList();
                        if (SaveDiagResult) result.SaveToDisk(Path.Combine(DiagResultPath, $"diag-after-{SessionId}.png"), global::projectFrameCut.Drawing.Base.PictureExtensions.SharedPngPictureEncoder);
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

        private static void ProcessSixLaborsProcessingContexts(ref Image img, List<IPictureProcessStep> steps, ref List<PictureProcessStack> procStack, Guid sessionId)
        {
            foreach (var process in steps)
            {
                var stack = process.GetProcessStack();
                var sw = Stopwatch.StartNew();
                img.Mutate((c) => process.GetSixLaborsImageSharpProcess()(c));
                sw.Stop();
                stack.Elapsed = sw.Elapsed;
                procStack.Add(stack);
                if (SaveDiagResult)
                {
                    var swSaveDiag = Stopwatch.StartNew();
                    var opId = Guid.NewGuid();
                    img.SaveAsPng(Path.Combine(DiagResultPath, $"diag-{stack.OperationDisplayName}-{opId}.png"));
                    File.WriteAllText(Path.Combine(DiagResultPath, $"diag-{sessionId}-{stack.OperationDisplayName}-{opId}-stacks.txt"), PictureProcessStack.FormatProcessStackForLog(procStack, 50));
                    swSaveDiag.Stop();
                    procStack.Add(new PictureProcessStack
                    {
                        OperationDisplayName = $"Save diag result for {stack.OperationDisplayName}",
                        ProcessingFuncStackTrace = null,
                        Operator = typeof(PictureProcesser),
                        Elapsed = swSaveDiag.Elapsed,
                    });
                }
            }
        }
    }
}
