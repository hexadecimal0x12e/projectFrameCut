using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.ApplicationPluginBase.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationPluginBase.VectorComponentHandler;


namespace projectFrameCut.ApplicationPluginBase
{
    internal class InternalApplicationPluginBase : InternalPluginBase, IApplicationPluginBase
    {
        /// <summary>
        /// Maps effect type names to their App-layer UI provider factories. The custom UI types (color pickers,
        /// keyframing, position tuples, ...) are registered explicitly; every other type falls back to the generic
        /// metadata-driven <see cref="EffectProviderUI"/> via <see cref="GetDefaultEffectProviderUIProvider(IEffectProvider)"/>.
        /// The render providers themselves are inherited from <see cref="InternalPluginBase.EffectProviderProvider"/>.
        /// </summary>
        public Dictionary<string, Func<IEffectProvider, IEffectProviderUIProvider>> EffectProviderUIProvider => new Dictionary<string, Func<IEffectProvider, IEffectProviderUIProvider>>
        {
            { "RemoveColor", p => new RemoveColorUI(p) },
            { "Movement", p => new MovementUI(p) },
            { "TextFadeIn", p => new TextFadeInUI(p) },
            { "ProgressPlacer", p => new ProgressPlacerUI(p) },
            { "ProgressCrop", p => new ProgressCropUI(p) },
            { "ColorAdjustment", p => new ColorAdjustmentUI(p) },
        };

        public IEffectProviderUIProvider? GetDefaultEffectProviderUIProvider(IEffectProvider source)
        {
            return new EffectProviderUI(source);
        }

        public Dictionary<string, Func<ITextClipStyleProvider>> TextClipStyleProvider => new Dictionary<string, Func<ITextClipStyleProvider>>
        {
            { "Basic", () => new BasicTextStyleProvider() },
            { "Title", () => new TitleTextStyleProvider() },
            { "Pinyin", () => new PinyinTextStyleProvider() },
            { "LlmTranslate", () => new LlmTranslateTextStyleProvider() },
        };

        public Dictionary<string, Func<IVectorComponentHandler>> VectorComponentHandlerProvider => new()
        {
            ["Rectangle"] = () => new RectangleHandler(),
            ["RoundedRectangle"] = () => new RoundedRectangleHandler(),
            ["Ellipse"] = () => new EllipseHandler(),
            ["Line"] = () => new LineHandler(),
            ["CubicBezier"] = () => new CubicBezierHandler(),
            ["QuadraticBezier"] = () => new QuadraticBezierHandler(),
            ["Arc"] = () => new ArcHandler(),
            ["Polygon"] = () => new PolygonHandler(),
            ["Polyline"] = () => new PolylineHandler(),
            ["Text"] = () => new TextComponentHandler(),
        };

        public int AppLevelPluginAPIVersion => IApplicationPluginBase.CurrentAppLevelPluginAPIVersion;

        public View? SettingPageProvider(ref IApplicationPluginBase instance)
        {
            return null;
        }

        internal string locateId = "en-US";

        void IApplicationPluginBase.OnApplicationPluginLoaded()
        {
            ApplicationAPIBase.Localize.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping().TryGetValue(locateId, out var loc) ? loc : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;

            Markdown2XAML.RegisterCodeBlockRenderer(new XAMLCodeblockRenderer());
            Markdown2XAML.RegisterCodeBlockRenderer(new HtmlCodeBlockRenderer());
            Markdown2XAML.RegisterCodeBlockRenderer(new MermaidCodeBlockRenderer());
            Markdown2XAML.RegisterCodeBlockRenderer(new SvgCodeBlockRenderer());
        }
    }
}
