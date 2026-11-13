using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private enum CompositionPipeline { Mixture, LeftTransform, RightTransform }

    private const string CompositionPipelineKey = "__DraftEffectBindingView_CompositionPipeline__";
    private const string CompositionPipelineTabKey = "__EffectCompositionPipelineTab__";
    private CompositionPipeline _compositionPipeline;
    private HashSet<Guid> _compositionProviderIds = [];

    private bool IsTransformPipeline => _stage == EffectStage.Composition && _compositionPipeline != CompositionPipeline.Mixture;

    private void OnCompositionPipelineChanged(object? sender, EventArgs e)
    {
        if (_loadingStage || _clip is null || _stage != EffectStage.Composition
            || CompositionPipelinePicker.SelectedIndex < 0) return;
        SaveStageLayout();
        _clip.ExtraData[CompositionPipelineTabKey] = ((CompositionPipeline)CompositionPipelinePicker.SelectedIndex).ToString();
        LoadStage(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _stage);
    }

    private void LoadCompositionPipeline(ClipElementUI clip)
    {
        _compositionPipeline = TransformProcessing.ReadEnum<CompositionPipeline>(clip.ExtraData, CompositionPipelineTabKey);
        CompositionPipelinePicker.IsVisible = _stage == EffectStage.Composition;
        CompositionPipelinePicker.ItemsSource = new[]
        {
            Localized.EffectType_Mixture,
            $"{Localized.Transform_Left} {Localized.Transform_Tab}",
            $"{Localized.Transform_Right} {Localized.Transform_Tab}",
        };
        CompositionPipelinePicker.SelectedIndex = (int)_compositionPipeline;
    }

    private static CompositionPipeline ProviderPipeline(IEffectProvider provider) => provider.TypeOfEffect == EffectType.Transform
        ? TransformProcessing.ReadEnum<TransformSide>(provider.MetaData, TransformProcessing.SideKey) == TransformSide.Left
            ? CompositionPipeline.LeftTransform : CompositionPipeline.RightTransform
        : TransformProcessing.ReadEnum<CompositionPipeline>(provider.MetaData, CompositionPipelineKey);

    private void RefreshCompositionProviders()
    {
        _compositionProviderIds = [];
        if (_stage != EffectStage.Composition || _clip?.EffectProviders is not { } providers) return;
        var composition = providers.Values.Where(p => p.Stage == EffectStage.Composition).ToDictionary(p => p.Id);

        HashSet<Guid> Collect(IEnumerable<IEffectProvider> roots)
        {
            var ids = new HashSet<Guid>();
            void Visit(IEffectProvider p)
            {
                if (!ids.Add(p.Id)) return;
                foreach (var binding in p.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)))
                    if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out _)
                        && composition.TryGetValue(id, out var source)) Visit(source);
            }
            foreach (var p in roots) Visit(p);
            return ids;
        }

        var left = Collect(composition.Values.Where(p => ProviderPipeline(p) == CompositionPipeline.LeftTransform));
        var right = Collect(composition.Values.Where(p => ProviderPipeline(p) == CompositionPipeline.RightTransform));
        var mixture = Collect(composition.Values.Where(p => p.TypeOfEffect != EffectType.Transform
            && (p is MixtureSourceProvider || p.IsFinalOutputSource()
                || p.TypeOfEffect is EffectType.MixtureProvider or EffectType.SpeedVarianceProvider
                || ProviderPipeline(p) == CompositionPipeline.Mixture && !left.Contains(p.Id) && !right.Contains(p.Id))));
        foreach (var p in composition.Values.Where(p => p.TypeOfEffect != EffectType.Transform
            && !p.MetaData.ContainsKey(CompositionPipelineKey) && !mixture.Contains(p.Id)))
        {
            if (left.Contains(p.Id) && !right.Contains(p.Id)) p.MetaData[CompositionPipelineKey] = CompositionPipeline.LeftTransform.ToString();
            else if (right.Contains(p.Id) && !left.Contains(p.Id)) p.MetaData[CompositionPipelineKey] = CompositionPipeline.RightTransform.ToString();
        }
        _compositionProviderIds = _compositionPipeline switch
        {
            CompositionPipeline.LeftTransform => left,
            CompositionPipeline.RightTransform => right,
            _ => mixture,
        };
    }

    private bool IsCurrentPipelineProvider(IEffectProvider provider) => provider.Stage == _stage
        && (_stage != EffectStage.Composition || _compositionProviderIds.Contains(provider.Id));

    private static bool CanAddToPipeline(IEffectProvider provider, EffectStage stage, CompositionPipeline pipeline) =>
        stage != EffectStage.Composition || pipeline == CompositionPipeline.Mixture
        || (provider.Target.HasFlag(EffectTarget.ValueProvider) || provider.Target.HasFlag(EffectTarget.Pipeline))
            && provider.TypeOfEffect is not (EffectType.Transform or EffectType.MixtureProvider or EffectType.SpeedVarianceProvider);

    private void AssignCurrentPipeline(IEffectProvider provider)
    {
        if (_stage == EffectStage.Composition) provider.MetaData[CompositionPipelineKey] = _compositionPipeline.ToString();
    }

    private string PipelineLayoutPrefix => IsTransformPipeline ? _compositionPipeline + "_" : "";

    private string ProviderPositionKey(string axis) => ProviderPositionKey(axis, IsTransformPipeline ? _compositionPipeline : CompositionPipeline.Mixture);

    private static string ProviderPositionKey(string axis, CompositionPipeline pipeline) =>
        $"__DraftEffectBindingView_{(pipeline == CompositionPipeline.Mixture ? "" : pipeline + "_")}InteractiveEditor{axis}__";
}
