using projectFrameCut.Drawing.Effect;
using projectFrameCut.Drawing.Processing.Cropping;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace projectFrameCut.Render.Effect
{
    public class ZoomInContinuousEffect : ZoomInContinuousEffect<IPicture> { }

    public class ZoomInContinuousEffect<TPicture> : IContinuousEffect<TPicture> where TPicture : IPicture
    {
        public IEffect WithPictureType<T>() where T : IPicture => new ZoomInContinuousEffect<T>();

        public bool IsDeterministic => true;

        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ZoomIn";
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; }


        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public bool IsScoped { get; set; }
        public int TargetX { get; init; }
        public int TargetY { get; init; }
        public Dictionary<string, object> Parameters { get; set; } = new();


        public IPicture Render(TPicture source, float progress, int targetWidth, int targetHeight)
        {
            double clampedProgress = Math.Clamp(progress, 0.0, 1.0);

            int zoomTargetX = DynamicParam.Resolve(Parameters.GetValueOrDefault("TargetX"), TargetX);
            int zoomTargetY = DynamicParam.Resolve(Parameters.GetValueOrDefault("TargetY"), TargetY);
            int currentWidth = (int)Math.Round(source.Width + (zoomTargetX - source.Width) * clampedProgress);
            int currentHeight = (int)Math.Round(source.Height + (zoomTargetY - source.Height) * progress);
            if (currentWidth < 1) currentWidth = 1;
            if (currentHeight < 1) currentHeight = 1;

            if (currentWidth > source.Width) currentWidth = source.Width;
            if (currentHeight > source.Height) currentHeight = source.Height;

            int startX = Math.Max(0, (source.Width - currentWidth) / 2);
            int startY = Math.Max(0, (source.Height - currentHeight) / 2);
            using var cropped = CropEffectShared.CropAndProcess(source, startX, startY, currentWidth, currentHeight, 0);
            return PictureCalculation<TPicture>.Resize((TPicture)cropped, targetWidth, targetHeight, false);
        }


        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            return new ZoomInContinuousEffect<TPicture>
            {
                Parameters = parameters,
                TargetX = DynamicParam.ToInt32(parameters.GetValueOrDefault("TargetX")),
                TargetY = DynamicParam.ToInt32(parameters.GetValueOrDefault("TargetY")),
                RelativeWidth = this.RelativeWidth,
                RelativeHeight = this.RelativeHeight,
                Name = this.Name,
                Index = this.Index,
                Enabled = this.Enabled,
                StartPoint = this.StartPoint,
                EndPoint = this.EndPoint,
                IsScoped = this.IsScoped,
            };
        }

        public void Initialize()
        {
        }
    }

    /// <summary>
    /// The Render-side provider of the ZoomIn continuous effect.
    /// </summary>
    public class ZoomInEffectProvider : EffectProviderBase
    {
        public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];

        public override bool IsDeterministic => true;

        public ZoomInEffectProvider()
        {
            Name = "ZoomIn";
            SetField("TargetX", 960);
            SetField("TargetY", 540);
        }

        public override string TypeName => "ZoomIn";

        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("TargetX", EffectArgumentFieldType.Integer, "960", min: "1"),
                Field("TargetY", EffectArgumentFieldType.Integer, "540", min: "1")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];


    }
}
