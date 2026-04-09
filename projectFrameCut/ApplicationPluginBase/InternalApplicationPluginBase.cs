using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;


namespace projectFrameCut.ApplicationPluginBase
{
    internal class InternalApplicationPluginBase : InternalPluginBase, IApplicationPluginBase
    {
        public Dictionary<string, Func<IEffectBundle>> EffectBundleProvider => new Dictionary<string, Func<IEffectBundle>>
        {
            { "ZoomIn", () => new Effect.ZoominEffectBundle() },
            { "RemoveColor", () => new Effect.RemoveColorEffectBundle() },
            { "Jitter", () => new Effect.JitterEffectBundle() },
            { "Movement", () => new Effect.MovementEffectBundle()  },
            { "Blur", () => new Effect.BlurEffectBundle() },
            {"Crop", () => new Effect.CropEffectBundle() }
        };

        public int AppLevelPluginAPIVersion => IApplicationPluginBase.CurrentAppLevelPluginAPIVersion;

        public Dictionary<string, IClipDynamicPreviewProvider> ClipDynamicPreviewProvider => new Dictionary<string, IClipDynamicPreviewProvider>
        {
            { "VideoClip", new VideoClipDynamicPreviewProvider() },
            { "PhotoClip", new PhotoClipDynamicPreviewProvider() },
            { "SolidColorClip", new SolidColorClipDynamicPreviewProvider() },
            { "TextClip", new TextClipDynamicPreviewProvider() },
        };

        public Dictionary<string, IEffectDynamicPreviewProvider> EffectDynamicPreviewProvider => new Dictionary<string, IEffectDynamicPreviewProvider>
        {
            { "Blur", new BlurEffectDynamicPreviewProvider() },
            { "Crop", new CropEffectDynamicPreviewProvider() },
            { "Jitter", new JitterEffectDynamicPreviewProvider() },
            { "Place", new PlaceEffectDynamicPreviewProvider() },
            { "PointPlacer", new PointPlacerEffectDynamicPreviewProvider() },
            { "RemoveColor", new RemoveColorEffectDynamicPreviewProvider() },
            { "Resize", new ResizeEffectDynamicPreviewProvider() },
            { "Rotation", new RotationEffectDynamicPreviewProvider() },
            { "StraightLineMovementValueProducer", new StraightLineMovementValueProducerEffectDynamicPreviewProvider() },
            { "SubjectMattingMaskGenerator", new SubjectMattingMaskGeneratorEffectDynamicPreviewProvider() },
            { "ZoomIn", new ZoomInEffectDynamicPreviewProvider() },
        };

        public View? SettingPageProvider(ref IApplicationPluginBase instance)
        {
            return null;
        }

        internal string locateId = "en-US";

        void IApplicationPluginBase.OnApplicationPluginLoaded()
        {
            ApplicationAPIBase.LocalizedResources.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping().TryGetValue(locateId, out var loc) ? loc : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;
        }


    }
}
