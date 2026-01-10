using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public class ResizeProcesser : IPictureProcessStep
    {
        public string Name => "Resize";

        public Dictionary<string, object?> Properties { get; set; } = new();

        public IPicture Process(IPicture source)
        {
            if(Properties.TryGetValue("TargetWidth", out var targetWidthObj) &&
               Properties.TryGetValue("TargetHeight", out var targetHeightObj) &&
               targetWidthObj is int targetWidth &&
               targetHeightObj is int targetHeight)
            {
                bool preserveAspect = true;
                if(Properties.TryGetValue("PreserveAspect", out var preserveAspectObj) &&
                   preserveAspectObj is bool pa)
                {
                    preserveAspect = pa;
                }
                return source.Resize(targetWidth, targetHeight, preserveAspect);
            }
            else
            {
                throw new ArgumentException("ResizeProcesser requires TargetWidth and TargetHeight properties.");
            }
        }
    }
}
