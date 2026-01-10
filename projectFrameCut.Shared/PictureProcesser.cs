using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Text;

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
        public virtual string GetProcessStack() => $"Type:{Name}, Properties: {string.Join(", ", Properties.Select(c => $"{c.Key}:{c.Value}"))}";

    }
    public static class PictureProcesser
    {
        public static IPicture Process(List<IPictureProcessStep> steps, IPicture source, int targetPPB)
        {
            string procStack = "";
            var img = source.ToBitPerPixel(targetPPB).SaveToSixLaborsImage(targetPPB, true, false);
            List<Func<IImageProcessingContext, IImageProcessingContext>> processingContexts = new();
            foreach (var item in steps)
            {
                var step = item.GetSixLaborsImageSharpProcess();
                if (step is not null)
                {
                    processingContexts.Add(step);
                }
                else
                {
                    Logger.LogDiagnostic($"Step {item.Name} doesn't have a IImageProcessingContext. Process the picture and convert it...");
                    img = ProcessSixLaborsProcessingContexts(img, processingContexts);
                    processingContexts.Clear();
                    img = item.Process(img.ToPJFCPicture(targetPPB)).SaveToSixLaborsImage(targetPPB, true, false);
                }
                procStack += Environment.NewLine + item.GetProcessStack();
            }
            if(processingContexts.ListAny())
            {
                img = ProcessSixLaborsProcessingContexts(img, processingContexts);
            }
            return img.ToPJFCPicture(targetPPB);
        }

        private static Image ProcessSixLaborsProcessingContexts(Image img, List<Func<IImageProcessingContext, IImageProcessingContext>> processingContexts)
        {
            if (img is null)
            {
                throw new ArgumentException("Source picture does not have SixLabors image data.");
            }
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
