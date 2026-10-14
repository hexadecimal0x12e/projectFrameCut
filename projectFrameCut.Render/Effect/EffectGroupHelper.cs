using System.Text.Json;
using System.Security.Cryptography;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public static class EffectGroupHelper
{
    public const string MetadataKey = "__EffectGroups_v1__";

    public static string JsonHash(ReadOnlySpan<byte> json) => Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();

    public static List<EffectGroup> Read(Dictionary<string, object> metadata) =>
        metadata.TryGetValue(MetadataKey, out var value)
            ? JsonSerializer.Deserialize<List<EffectGroup>>(value is string s ? s : JsonSerializer.Serialize(value)) ?? [] : [];

    public static void Write(Dictionary<string, object> metadata, List<EffectGroup> groups)
    {
        if (groups.Count == 0) metadata.Remove(MetadataKey);
        else metadata[MetadataKey] = JsonSerializer.SerializeToElement(groups);
    }

    public static HashSet<Guid> Members(EffectGroup group, IReadOnlyList<EffectGroup> groups)
    {
        var result = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();
        void Visit(EffectGroup g)
        {
            if (!visiting.Add(g.Id)) throw new InvalidOperationException("Effect group nesting cycle.");
            result.UnionWith(g.ProviderIds);
            foreach (var id in g.ChildGroupIds)
                Visit(groups.SingleOrDefault(c => c.Id == id) ?? throw new InvalidOperationException("Missing child effect group."));
            visiting.Remove(g.Id);
        }
        Visit(group);
        return result;
    }

    public static bool CanGroup(IEffectProvider p, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        if (p is ClipArgumentProvider or IClipValueEndpointProvider || p.TypeOfEffect == EffectType.Transform) return false;
        foreach (var target in providers.Values)
            foreach (var binding in target.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)))
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out _)
                    && providers.TryGetValue(id, out var source)
                    && (target.Id == p.Id && source is IClipValueEndpointProvider
                        || source.Id == p.Id && target is IClipValueEndpointProvider)) return false;
        return true;
    }

    public static List<EffectGroupPort> RequiredPorts(EffectGroup group, IReadOnlyList<EffectGroup> groups,
        IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        var members = Members(group, groups);
        var result = new List<EffectGroupPort>();
        void Add(Guid id, string key, bool input)
        {
            if (result.Any(p => p.ProviderId == id && p.FieldId == key && p.IsInput == input)) return;
            if (!providers.TryGetValue(id, out var provider)) throw new InvalidOperationException("Missing group provider.");
            result.Add(new() { ProviderId = id, FieldId = key, IsInput = input, Name = $"{provider.Name}: {key}" });
        }
        foreach (var target in providers.Values)
        {
            foreach (var binding in target.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)))
            {
                bool inside = members.Contains(target.Id);
                bool parsed = EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var output);
                if (binding.Value == IEffectProvider.NoConnectionGUID.ToString()) continue;
                if (inside && (!parsed || !members.Contains(id))) Add(target.Id, binding.Key, true);
                if (!inside && parsed && members.Contains(id))
                {
                    if (!providers[id].TryGetOutputField(output, out var field)) throw new InvalidOperationException("Invalid output port.");
                    Add(id, field.Id, false);
                }
            }
            if (members.Contains(target.Id) && target.IsFinalOutputSource())
            {
                if (!target.TryGetOutputField(target.GetFinalOutputFieldId(), out var field)) throw new InvalidOperationException("Invalid final output.");
                Add(target.Id, field.Id, false);
            }
        }
        return result;
    }

    public static void EnsureBoundaryPorts(List<EffectGroup> groups, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        foreach (var port in groups.SelectMany(g => g.Ports))
            if (port.IsInput && port.FieldId == EffectProviderAnchorExtensions.InputKey
                && providers.GetValueOrDefault(port.ProviderId) is IfConditionNodeEffectProvider)
            {
                port.FieldId = IfConditionNodeEffectProvider.ConditionKey;
                Logger.LogDiagnostic($"Migrated IfConditionNode group port on provider {port.ProviderId} to '{port.FieldId}'.");
            }
        // Leaf ports are promoted through every enclosing group.
        foreach (var group in groups.OrderBy(g => Members(g, groups).Count))
            foreach (var port in RequiredPorts(group, groups, providers))
                if (!group.Ports.Any(p => SamePort(p, port))) group.Ports.Add(port);
        void Promote(EffectGroup group, EffectGroupPort port)
        {
            foreach (var childId in group.ChildGroupIds)
            {
                var child = groups.Single(g => g.Id == childId);
                if (!Members(child, groups).Contains(port.ProviderId)) continue;
                if (!child.Ports.Any(p => SamePort(p, port)))
                    child.Ports.Add(new() { ProviderId = port.ProviderId, FieldId = port.FieldId, Name = port.Name, IsInput = port.IsInput });
                Promote(child, port);
            }
        }
        foreach (var group in groups)
            foreach (var port in group.Ports.ToArray()) Promote(group, port);
    }

    public static bool SamePort(EffectGroupPort a, EffectGroupPort b) =>
        a.ProviderId == b.ProviderId && a.FieldId == b.FieldId && a.IsInput == b.IsInput;

    public static void Validate(List<EffectGroup> groups, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        if (groups.Select(g => g.Id).Distinct().Count() != groups.Count
            || groups.Any(g => g.Id == Guid.Empty || providers.ContainsKey(g.Id)
                || g.Id == IEffectProvider.InputAnchorGUID || g.Id == IEffectProvider.OutputAnchorGUID
                || g.Id == IEffectProvider.NoConnectionGUID || !double.IsFinite(g.X) || !double.IsFinite(g.Y)))
            throw new InvalidOperationException("Invalid effect group IDs or positions.");
        var ownedProviders = new HashSet<Guid>();
        var ownedGroups = new HashSet<Guid>();
        foreach (var g in groups)
        {
            foreach (var id in g.ProviderIds)
                if (!ownedProviders.Add(id)) throw new InvalidOperationException("Provider belongs to multiple groups.");
            foreach (var id in g.ChildGroupIds)
                if (!ownedGroups.Add(id)) throw new InvalidOperationException("Group has multiple parents.");
            var members = Members(g, groups);
            if (members.Count == 0 || members.Any(id => !providers.ContainsKey(id) || !CanGroup(providers[id], providers)))
                throw new InvalidOperationException("Effect groups cannot contain Clip properties or cross-Clip bindings.");
            if (members.Select(id => providers[id]).Where(p => !p.Target.HasFlag(EffectTarget.ValueProvider)
                    || !DynamicEffectBindings.CanInlineValue(p, providers))
                .Select(p => p.GetPipeline()).Distinct().Count() > 1)
                throw new InvalidOperationException("Effect group members must use the same pipeline.");
            if (g.Ports.Select(p => (p.ProviderId, p.FieldId, p.IsInput)).Distinct().Count() != g.Ports.Count)
                throw new InvalidOperationException("Duplicate effect group ports.");
            foreach (var port in g.Ports)
            {
                if (!members.Contains(port.ProviderId) || string.IsNullOrWhiteSpace(port.Name)) throw new InvalidOperationException("Invalid group port.");
                var p = providers[port.ProviderId];
                if (port.IsInput ? !DynamicEffectBindings.InputFields(p).ContainsKey(port.FieldId)
                    : !p.GetOutputFields().ContainsKey(port.FieldId)) throw new InvalidOperationException("Missing group port field.");
                if (port.IsInput && p.AnchorsBindingState.TryGetValue(port.FieldId, out var source)
                    && EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _) && members.Contains(id))
                    throw new InvalidOperationException("An internally connected input cannot be exposed.");
                foreach (var childId in g.ChildGroupIds)
                {
                    var child = groups.Single(c => c.Id == childId);
                    if (Members(child, groups).Contains(port.ProviderId) && !child.Ports.Any(p => SamePort(p, port)))
                        throw new InvalidOperationException("Missing nested group port mapping.");
                }
            }
            if (RequiredPorts(g, groups, providers).Any(p => !g.Ports.Any(c => SamePort(p, c))))
                throw new InvalidOperationException("Every external binding must have a public group port.");
        }
    }

    public static EffectGroupTemplate Export(EffectGroup root, List<EffectGroup> groups, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        Validate(groups, providers);
        var members = Members(root, groups);
        var selected = new List<EffectGroup>();
        void Visit(EffectGroup group)
        {
            selected.Add(group);
            foreach (var id in group.ChildGroupIds) Visit(groups.Single(g => g.Id == id));
        }
        Visit(root);
        var template = Copy(new EffectGroupTemplate { RootGroupId = root.Id, Groups = selected,
            Providers = members.Select(id => EffectBindingHelper.SerializeProvider(providers[id])).ToArray() });
        foreach (var p in template.Providers)
        {
            foreach (var key in p.AnchorsBindingState.Keys.ToArray())
            {
                var source = p.AnchorsBindingState[key];
                if (EffectProviderAnchorExtensions.IsOutputBindingKey(key)
                    || source != ValueProviderFrameContext.BuiltInFrameProviderId && source != ValueProviderFrameContext.BuiltInProgressProviderId
                        && (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _) || !members.Contains(id)))
                    p.AnchorsBindingState.Remove(key);
            }
            if (!p.AnchorsBindingState.ContainsKey(EffectProviderAnchorExtensions.InputKey))
                p.AnchorsBindingState[EffectProviderAnchorExtensions.InputKey] = IEffectProvider.NoConnectionGUID.ToString();
        }
        return template;
    }

    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static EffectGroupTemplate Instantiate(EffectGroupTemplate source)
    {
        var t = Copy(source);
        ValidateTemplate(t);
        var ids = t.Providers.Select(p => p.Id).Concat(t.Groups.Select(g => g.Id)).ToDictionary(id => id, _ => Guid.NewGuid());
        foreach (var p in t.Providers)
        {
            p.Id = ids[p.Id];
            foreach (var key in p.AnchorsBindingState.Keys.ToArray())
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(p.AnchorsBindingState[key], out var id, out var output) && ids.TryGetValue(id, out var mapped))
                    p.AnchorsBindingState[key] = output is null ? mapped.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(mapped, output);
        }
        foreach (var g in t.Groups)
        {
            g.Id = ids[g.Id];
            g.ProviderIds = g.ProviderIds.Select(id => ids[id]).ToList();
            g.ChildGroupIds = g.ChildGroupIds.Select(id => ids[id]).ToList();
            foreach (var p in g.Ports) p.ProviderId = ids[p.ProviderId];
        }
        t.RootGroupId = ids[t.RootGroupId];
        return t;
    }

    public static void ValidateTemplate(EffectGroupTemplate t)
    {
        if (t.Groups is null || t.Providers is null
            || t.Groups.Any(g => g is null || g.ProviderIds is null || g.ChildGroupIds is null || g.Ports is null || g.Ports.Any(p => p is null))
            || t.Providers.Any(p => p is null || string.IsNullOrWhiteSpace(p.TypeName) || p.AnchorsBindingState is null))
            throw new InvalidOperationException("Invalid effect group template data.");
        if (t.Version != 1 || t.Groups.Count == 0 || t.Providers.Length == 0
            || t.Providers.Select(p => p.Id).Distinct().Count() != t.Providers.Length
            || t.Providers.Any(p => p.Id == Guid.Empty || p.Id == IEffectProvider.OutputAnchorGUID || p.Id == IEffectProvider.NoConnectionGUID
                || p.TypeName is "ClipArgumentProvider" or "ClipArguments" or "ClipValueInput" or "ClipValueOutput")
            || t.Groups.Select(g => g.Id).Distinct().Count() != t.Groups.Count)
            throw new InvalidOperationException("Invalid effect group template or version.");
        var root = t.Groups.SingleOrDefault(g => g.Id == t.RootGroupId) ?? throw new InvalidOperationException("Missing root group.");
        var members = Members(root, t.Groups);
        var visited = new HashSet<Guid>();
        void Visit(EffectGroup g)
        {
            if (!visited.Add(g.Id)) throw new InvalidOperationException("Repeated child group.");
            foreach (var id in g.ChildGroupIds) Visit(t.Groups.Single(c => c.Id == id));
        }
        Visit(root);
        if (visited.Count != t.Groups.Count || !members.SetEquals(t.Providers.Select(p => p.Id))
            || t.Groups.SelectMany(g => g.ProviderIds).Distinct().Count() != t.Groups.Sum(g => g.ProviderIds.Count))
            throw new InvalidOperationException("Invalid group membership.");
        foreach (var p in t.Providers)
            foreach (var b in p.AnchorsBindingState)
                if (EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)
                    || b.Value != ValueProviderFrameContext.BuiltInFrameProviderId && b.Value != ValueProviderFrameContext.BuiltInProgressProviderId
                        && b.Value != IEffectProvider.NoConnectionGUID.ToString()
                        && (!EffectProviderOutputExtensions.TryParseOutputSourceId(b.Value, out var id, out _) || !members.Contains(id)))
                    throw new InvalidOperationException("Template contains a project-specific binding.");
        // Structural validation also runs when a plugin is unavailable.
        foreach (var g in t.Groups)
        {
            var ids = Members(g, t.Groups);
            if (ids.Count == 0 || !double.IsFinite(g.X) || !double.IsFinite(g.Y)
                || t.Providers.Any(p => p.Id == g.Id) || g.Id == Guid.Empty
                || g.Id == IEffectProvider.OutputAnchorGUID || g.Id == IEffectProvider.NoConnectionGUID
                || g.Ports.Select(p => (p.ProviderId, p.FieldId, p.IsInput)).Distinct().Count() != g.Ports.Count
                || g.Ports.Any(p => !ids.Contains(p.ProviderId) || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.FieldId)))
                throw new InvalidOperationException("Invalid group definition.");
            foreach (var childId in g.ChildGroupIds)
            {
                var child = t.Groups.Single(c => c.Id == childId);
                var childMembers = Members(child, t.Groups);
                if (g.Ports.Any(p => childMembers.Contains(p.ProviderId) && !child.Ports.Any(c => SamePort(p, c))))
                    throw new InvalidOperationException("Missing nested interface.");
            }
            foreach (var p in t.Providers)
                foreach (var b in p.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)))
                {
                    if (b.Value == IEffectProvider.NoConnectionGUID.ToString()) continue;
                    bool parsed = EffectProviderOutputExtensions.TryParseOutputSourceId(b.Value, out var source, out var output);
                    if (ids.Contains(p.Id) && (!parsed || !ids.Contains(source))
                        && !g.Ports.Any(port => port.IsInput && port.ProviderId == p.Id && port.FieldId == b.Key))
                        throw new InvalidOperationException("Missing public input for an external binding.");
                    if (!ids.Contains(p.Id) && parsed && ids.Contains(source)
                        && !g.Ports.Any(port => !port.IsInput && port.ProviderId == source && (output is null || port.FieldId == output)))
                        throw new InvalidOperationException("Missing public output for an external binding.");
                    if (ids.Contains(p.Id) && parsed && ids.Contains(source)
                        && g.Ports.Any(port => port.IsInput && port.ProviderId == p.Id && port.FieldId == b.Key))
                        throw new InvalidOperationException("Internally connected input is public.");
                }
        }
    }
}
