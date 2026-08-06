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
                new(ValueProviderFrameContext.BuiltInFrameProviderId, "Frame Index"),
                new(ValueProviderFrameContext.BuiltInProgressProviderId, "Clip Progress"),
            };
            if (_clip.EffectProviders is { } bundles)
            {
                foreach (var (id, bundle) in bundles)
                {
                    if (id == _provider.Id) continue;
                    if (bundle is IEffectProvider p && p.Target.HasFlag(EffectTarget.ValueProvider))
                    {
                        var outName = bundle.OutField?.TypeName;
                        sources.Add(new ValueBindingSource(id.ToString(), bundle.Name ?? bundle.TypeName, outName));
                    }
                }
            }
            return sources;
        }

        public string? GetSourceDisplayName(string sourceId)
        {
            foreach (var s in GetBindingSources())
            {
                if (s.Id == sourceId) return s.DisplayName;
            }
            return null;
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
            if (!_provider.Fields.ContainsKey(fieldId)) return;
            _provider.SetFieldBinding(fieldId, sourceId);
            EffectBindingHelper.MaterializeFields([_provider]);
            _onChanged?.Invoke();
        }

        public void Unbind(string fieldId)
        {
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

            AddOption(string.Empty, "Unbind / Static");
            foreach (var s in GetBindingSources())
            {
                var suffix = s.OutputAnchorName is { Length: > 0 } ? $" ({s.OutputAnchorName})" : string.Empty;
                AddOption(s.Id, $"{s.DisplayName}{suffix}");
            }

            const string addProviderLabel = "➕ Add Value Provider…";
            options.Add(addProviderLabel);

            var pick = await _page.DisplayActionSheetAsync($"Bind {fieldId}", "Cancel", null, options.ToArray());
            if (string.IsNullOrEmpty(pick) || pick == "Cancel") return;

            if (pick == addProviderLabel)
            {
                var providerTypes = EffectServices.GetAvailableEffectProviders()
                    .Where(kv => kv.Value().Target.HasFlag(EffectTarget.ValueProvider))
                    .Select(kv => kv.Key)
                    .ToList();
                if (providerTypes.Count == 0) return;
                var typePick = await _page.DisplayActionSheetAsync("Add Value Provider", "Cancel", null, providerTypes.ToArray());
                if (string.IsNullOrEmpty(typePick) || typePick == "Cancel") return;
                if (AddValueProvider(typePick) is { } newId) ApplyBinding(fieldId, newId);
                return;
            }

            if (sourceByOption.TryGetValue(pick, out var chosen))
            {
                if (string.IsNullOrEmpty(chosen)) Unbind(fieldId);
                else ApplyBinding(fieldId, chosen);
            }
        }
    }
}
