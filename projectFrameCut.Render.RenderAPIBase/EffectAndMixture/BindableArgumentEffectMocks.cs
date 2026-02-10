using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public abstract class MockBindableEffectBase : IBindableArgumentEffect
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public string TypeName => this.GetType().Name;
        public EffectImplementType ImplementType => EffectImplementType.Custom1;
        public string Name { get; set; } = "Mock Effect";
        public Dictionary<string, object> Parameters { get; } = new();
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string? NeedComputer => null;
        public bool YieldProcessStep => false;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }
        
        public string? BindedArgumentProviderID { get; set; }
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public abstract BindableArgumentEffectType EffectRole { get; }

        public IEffect WithParameters(Dictionary<string, object> parameters) => this;
        public virtual void Initialize() { }
    }

    public class MockValueProvider : MockBindableEffectBase, IBindableArgumentEffectValueProvider
    {
        public override BindableArgumentEffectType EffectRole => BindableArgumentEffectType.ValueProvider;
        public bool GenerateOnce => false;
        public string OutputAnchorName => "Output";

        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            Logger.LogDiagnostic($"[MockValueProvider,{Id}] Providing value...");
            return $"Value from {Id} (Provider)";
        }
    }

    public class MockOneToOneProcessor : MockBindableEffectBase, IBindableArgumentEffectOneToOneValueProcesser
    {
        public override BindableArgumentEffectType EffectRole => BindableArgumentEffectType.OneInputValueProcessor;
        public string InputAnchorName => "Input";
        public string OutputAnchorName => "Output";

        public object ProcessValue(object source, IComputer? computer, int targetWidth, int targetHeight)
        {
            Logger.LogDiagnostic($"[MockValueProvider,{Id}] Processing {source}...");
            return $"{source} -> Processed by {Id}";
        }
    }

    public class MockManyToOneProcessor : MockBindableEffectBase, IBindableArgumentEffectManyToOneValueProcesser
    {
        public override BindableArgumentEffectType EffectRole => BindableArgumentEffectType.ManyInputValueProcessor;
        public bool GenerateOnce => false;
        
        public string[] BindedArgumentProviderIDs { get; set; } = Array.Empty<string>();
        public string[] InputAnchorDisplayNames => new[] { "Input1", "Input2" }; 
        public string OutputAnchorName => "Output";

        public object ProcessValues(object[] sources, IComputer? computer, int targetWidth, int targetHeight)
        {
            var inputs = sources != null ? string.Join(", ", sources) : "null";
            Logger.LogDiagnostic($"[MockValueProvider,{Id}] Combining {inputs}...");

            return $"[{inputs}] -> Processed by {Id}";
        }
    }

    public class MockOneInputResultGenerator : MockBindableEffectBase, IBindableArgumentEffectOneInputResultGenerator
    {
        public override BindableArgumentEffectType EffectRole => BindableArgumentEffectType.OneInputResultGenerator;
        public string InputAnchorName => "Input";
        public string OutputAnchorName => "Output";
        public bool IsContinuous { get; set; } = false;
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            Logger.LogDiagnostic($"[MockValueProvider,{Id}] Generating ({source})...");

            // We modify the frame's ProcessStack to record the trace if possible, or just return frame
            if (frame.ProcessStack != null)
             {
                frame.ProcessStack = frame.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = "Mock IBindableEffect series",
                    Operator = GetType(),
                    ProcessingFuncStackTrace = new System.Diagnostics.StackTrace(true),
                    Properties = new Dictionary<string, object> { { "Data", source } }
                }).ToList();
                 // Check if PictureProcessStack is accessible and constructible
                 // Assuming yes based on context
                 // We can't add easily if we don't know the constructor of PictureProcessStack
             }
             return frame;
        }

        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }
    }

    public class MockManyInputResultGenerator : MockBindableEffectBase, IBindableArgumentEffectManyInputResultGenerator
    {
        public override BindableArgumentEffectType EffectRole => BindableArgumentEffectType.ManyInputResultGenerator;
        public string[] BindedArgumentProviderIDs { get; set; } = Array.Empty<string>();
        public string[] InputAnchorDisplayNames => new[] { "Input1", "Input2" };
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            Logger.LogDiagnostic($"[MockValueProvider,{Id}] Generating ({source})...");

            return frame;
        }

        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight)
        {
             throw new NotImplementedException();
        }
    }
}
