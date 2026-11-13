using System.Runtime.CompilerServices;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.DraftStuff;

internal static class EffectGroupBindingGuard
{
    private sealed class State
    {
        public Dictionary<Guid, Dictionary<string, string>> Bindings = [];
    }

    private static readonly ConditionalWeakTable<ClipElementUI, State> states = new();

    public static void Capture(ClipElementUI clip)
    {
        if (!clip.ExtraData.ContainsKey(EffectGroupHelper.MetadataKey))
        {
            states.Remove(clip);
            return;
        }
        states.GetValue(clip, _ => new State()).Bindings = clip.EffectProviders?
            .ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value.AnchorsBindingState)) ?? [];
    }

    public static void Synchronize(ClipElementUI clip)
    {
        if (clip.EffectProviders is not { } providers || !clip.ExtraData.ContainsKey(EffectGroupHelper.MetadataKey)) return;
        try
        {
            var groups = EffectGroupHelper.Read(clip.ExtraData);
            foreach (var g in groups)
            {
                g.ProviderIds.RemoveAll(id => !providers.ContainsKey(id));
                g.Ports.RemoveAll(p => !providers.ContainsKey(p.ProviderId));
            }
            while (groups.Any(g => g.ProviderIds.Count == 0 && g.ChildGroupIds.Count == 0))
            {
                var empty = groups.Where(g => g.ProviderIds.Count == 0 && g.ChildGroupIds.Count == 0).Select(g => g.Id).ToHashSet();
                groups.RemoveAll(g => empty.Contains(g.Id));
                foreach (var g in groups) g.ChildGroupIds.RemoveAll(empty.Contains);
            }
            EffectGroupHelper.EnsureBoundaryPorts(groups, providers);
            EffectGroupHelper.Validate(groups, providers);
            EffectGroupHelper.Write(clip.ExtraData, groups);
            Capture(clip);
        }
        catch (Exception ex)
        {
            if (states.TryGetValue(clip, out var state))
                foreach (var p in providers.Values)
                    if (state.Bindings.TryGetValue(p.Id, out var binding)) p.AnchorsBindingState = new(binding);
            EffectBindingHelper.MaterializeFields(providers.Values);
            Logger.Log(ex, $"Reject invalid effect group bindings on clip {clip.Id}", typeof(EffectGroupBindingGuard));
            throw;
        }
    }
}
