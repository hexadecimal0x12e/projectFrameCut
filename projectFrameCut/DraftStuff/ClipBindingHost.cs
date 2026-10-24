using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace projectFrameCut.DraftStuff
{
    /// <summary>
    /// A reusable <see cref="IEffectBindingHost"/> implementation that works against a <see cref="ClipElementUI"/>
    /// (not the node editor specifically). It is used to inject the bind button into property panels built outside
    /// the node binding view (e.g. the effect list tab).
    /// </summary>
    public class ClipBindingHost : IEffectBindingHost
    {
        private readonly ClipElementUI _clip;
        private readonly IEffectProvider _provider;
        private readonly DraftPage? _page;
        private readonly Action? _onChanged;
        private readonly Dictionary<string, int> _bindingVersions = new();

        public ClipBindingHost(ClipElementUI clip, IEffectProvider provider, DraftPage? page = null, Action? onChanged = null)
        {
            _clip = clip;
            _provider = provider;
            _page = page;
            _onChanged = onChanged;
        }

        public IReadOnlyList<ValueBindingSource> GetBindingSources()
        {
            var sources = new List<ValueBindingSource>
            {
                new(ValueProviderFrameContext.BuiltInFrameProviderId,
                    LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBindView_BoundSource_FrameIndex),
                new(ValueProviderFrameContext.BuiltInProgressProviderId,
                    LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBindView_BoundSource_Progress),
                new(IEffectProvider.InputAnchorGUID.ToString(),
                    LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBind_SourcePicture, "IPicture"),
            };
            if (ValueProviderFrameContext.SupportsAudioSample(_provider.Stage))
                sources.Add(new(ValueProviderFrameContext.BuiltInAudioSampleProviderId,
                    LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBind_FinalAudio, "AudioSample"));
            if (_clip.EffectProviders is { } bundles)
            {
                foreach (var (id, bundle) in bundles)
                {
                    if (id == _provider.Id || bundle.Stage != _provider.Stage) continue;
                    if (DynamicEffectBindings.CanBindOutput(bundle))
                    {
                        foreach (var output in bundle.GetOutputFields().Values)
                            sources.Add(new ValueBindingSource(bundle is IMultipleOutputEffectProvider
                                ? EffectProviderOutputExtensions.CreateOutputSourceId(id, output.Id) : id.ToString(),
                                bundle.Name ?? bundle.TypeName, bundle is IMultipleOutputEffectProvider ? output.Id : output.TypeName));
                    }
                }
            }
            return sources;
        }

        public string? GetSourceDisplayName(string sourceId)
        {
            switch (sourceId)
            {
                case ValueProviderFrameContext.BuiltInAudioSampleProviderId:
                    return LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBind_FinalAudio;
                case ValueProviderFrameContext.BuiltInFrameProviderId:
                    return LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBindView_BoundSource_FrameIndex;
                case ValueProviderFrameContext.BuiltInProgressProviderId:
                    return LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBindView_BoundSource_Progress;
                case var id when id == IEffectProvider.InputAnchorGUID.ToString():
                    return $"{LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBind_SourcePicture} (IPicture)";
            }
            if (!EffectProviderOutputExtensions.TryParseOutputSourceId(sourceId, out var providerId, out var outputId)
                || providerId == _provider.Id || _clip.EffectProviders?.GetValueOrDefault(providerId) is not { } provider
                || !DynamicEffectBindings.CanBindOutput(provider)
                || !provider.TryGetOutputField(outputId, out var output)) return null;
            var anchor = provider is IMultipleOutputEffectProvider ? output.Id : output.TypeName;
            return string.IsNullOrEmpty(anchor) ? provider.Name ?? provider.TypeName : $"{provider.Name ?? provider.TypeName} ({anchor})";
        }

        public string? AddValueProvider(string providerTypeName)
        {
            var factories = EffectServices.GetAvailableEffectProviders();
            if (!factories.TryGetValue(providerTypeName, out var factory)) return null;

            var instance = factory();
            instance.Id = Guid.NewGuid();
            instance.Enabled = true;
            instance.Name = providerTypeName;
            _clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
            _clip.EffectProviders[instance.Id] = instance;
            ClipInfoBuilder.RebuildAllEffects(_clip);
            _onChanged?.Invoke();
            return instance.Id.ToString();
        }

        public void ApplyBinding(string fieldId, string sourceId)
        {
            if (!_provider.Fields.ContainsKey(fieldId) && !_provider.InFields.ContainsKey(fieldId)) return;
            _bindingVersions[fieldId] = _bindingVersions.GetValueOrDefault(fieldId) + 1;
            ApplyBindingCore(fieldId, sourceId, _clip.EffectProviders is { } providers
                ? new EffectFieldTypeResolution(providers, BindingOverride(fieldId, sourceId)) : null);
        }

        private Dictionary<Guid, IReadOnlyDictionary<string, string>> BindingOverride(string fieldId, string sourceId) => new()
        {
            [_provider.Id] = new Dictionary<string, string>(_provider.AnchorsBindingState) { [fieldId] = sourceId },
        };

        private void ApplyBindingCore(string fieldId, string sourceId, EffectFieldTypeResolution? types)
        {
            if (types is not null && (!types.AreInputsCompatible(_provider) || types.Diagnostics.Count != 0))
                throw new InvalidOperationException($"Source '{sourceId}' is incompatible with '{fieldId}' on provider {_provider.Id}.");
            var state = new Dictionary<string, string>(_provider.AnchorsBindingState);
            try
            {
                _provider.SetFieldBinding(fieldId, sourceId);
                EffectGroupBindingGuard.Synchronize(_clip);
            }
            catch
            {
                _provider.AnchorsBindingState = state;
                throw;
            }
            EffectBindingHelper.MaterializeFields([_provider]);
            _onChanged?.Invoke();
        }

        private async Task ApplyBindingAsync(string fieldId, string sourceId)
        {
            if (!_provider.Fields.ContainsKey(fieldId) && !_provider.InFields.ContainsKey(fieldId)) return;
            int version = _bindingVersions[fieldId] = _bindingVersions.GetValueOrDefault(fieldId) + 1;
            try
            {
                EffectFieldTypeResolution? types = null;
                if (_clip.EffectProviders is { } providers)
                {
                    types = await EffectFieldTypeResolution.ResolveAsync(providers, bindingOverrides: BindingOverride(fieldId, sourceId));
                    if (!ReferenceEquals(_clip.EffectProviders, providers) || version != _bindingVersions[fieldId]) return;
                }
                ApplyBindingCore(fieldId, sourceId, types);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Bind '{fieldId}' on provider {_provider.Id}", this);
                _page?.SetStatusText(ex.Message);
            }
        }

        public void Unbind(string fieldId)
        {
            _bindingVersions[fieldId] = _bindingVersions.GetValueOrDefault(fieldId) + 1;
            _provider.ClearFieldBinding(fieldId);
            EffectBindingHelper.MaterializeFields([_provider]);
            _onChanged?.Invoke();
        }

        public async Task EditBinding(string fieldId)
        {
            if (_page is null) return;

            var options = new List<string>();
            var sourceByOption = new Dictionary<string, string>();
            void AddOption(string sourceId, string display)
            {
                if (sourceByOption.ContainsKey(display)) return;
                sourceByOption[display] = sourceId;
                options.Add(display);
            }

            AddOption(string.Empty, LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.EffectBindView_BoundSource_Disconnect);
            var sources = GetBindingSources();
            HashSet<string>? compatible = null;
            if (_clip.EffectProviders is { } providers)
            {
                var types = await EffectFieldTypeResolution.ResolveAsync(providers);
                compatible = await Task.Run(() =>
                {
                    var ids = new HashSet<string>();
                    foreach (var source in sources)
                    {
                        var candidate = types.ResolveBindingCandidate(_provider, fieldId, source.Id);
                        if (candidate is not null && candidate.AreInputsCompatible(_provider) && candidate.Diagnostics.Count == 0) ids.Add(source.Id);
                    }
                    return ids;
                });
            }
            foreach (var s in sources)
            {
                if (compatible is not null && !compatible.Contains(s.Id)) continue;
                var suffix = s.OutputAnchorName is { Length: > 0 } ? $" ({s.OutputAnchorName})" : string.Empty;
                AddOption(s.Id, $"{s.DisplayName}{suffix}");
            }

            var pick = await _page.DisplayActionSheetAsync($"Bind {fieldId}", "Cancel", null, options.ToArray());
            if (string.IsNullOrEmpty(pick) || pick == "Cancel") return;

            if (sourceByOption.TryGetValue(pick, out var chosen))
            {
                if (string.IsNullOrEmpty(chosen)) Unbind(fieldId);
                else await ApplyBindingAsync(fieldId, chosen);
            }
        }
    }
}
