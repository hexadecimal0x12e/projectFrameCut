using System.Collections.Concurrent;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public readonly record struct EffectImplementationKey(string TypeName, EffectImplementType ImplementType);

public sealed class EffectImplementationRegistry
{
    public static EffectImplementationRegistry Shared { get; } = new();

    private readonly object _sync = new();
    private Dictionary<EffectImplementationKey, Dictionary<string, Func<IEffect>>> _factories = [];
    private long revision;

    public long Revision => Volatile.Read(ref revision);

    public ConcurrentDictionary<EffectImplementationKey, string> PreferredPlugins { get; } = new();

    public EffectImplementType? ForcePreferToType { get; set; }

    public void Register(string pluginId, IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> factories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(factories);

        foreach (var (key, factory) in factories)
        {
            if (string.IsNullOrWhiteSpace(key.TypeName))
                throw new ArgumentException("Effect implementation TypeName cannot be empty.", nameof(factories));
            ArgumentNullException.ThrowIfNull(factory);
        }

        lock (_sync)
        {
            var next = CloneWithoutPlugin(pluginId);
            foreach (var (key, factory) in factories)
            {
                if (!next.TryGetValue(key, out var candidates))
                    next[key] = candidates = new(StringComparer.Ordinal);
                candidates[pluginId] = factory;
            }
            Volatile.Write(ref _factories, next);
            Interlocked.Increment(ref revision);
        }
    }

    public void Unregister(string pluginId)
    {
        lock (_sync)
        {
            Volatile.Write(ref _factories, CloneWithoutPlugin(pluginId));
            Interlocked.Increment(ref revision);
        }
    }

    public EffectImplementType[] GetImplementTypes(string typeName) => Volatile.Read(ref _factories).Keys
        .Where(x => x.TypeName == ResolveTypeName(typeName, null))
        .Select(x => x.ImplementType)
        .Distinct()
        .Order()
        .ToArray();

    public IEffect Create(string typeName, EffectImplementType requestedType, EffectImplementType defaultType, Dictionary<string, object> parameters)
        => CreateCore(typeName, requestedType, defaultType, parameters, null);

    public IEffect Create<TPicture>(string typeName, EffectImplementType requestedType, EffectImplementType defaultType, Dictionary<string, object> parameters)
        where TPicture : projectFrameCut.Drawing.Base.IPicture
    {
        var effect = CreateCore(typeName, requestedType, defaultType, parameters, blank => blank.WithPictureType<TPicture>());
        Logger.LogDiagnostic($"Created '{effect.GetType().Name}' for '{typeName}/{effect.ImplementType}', picture '{typeof(TPicture).Name}'.");
        return effect;
    }

    private IEffect CreateCore(string typeName, EffectImplementType requestedType, EffectImplementType defaultType,
        Dictionary<string, object> parameters, Func<IEffect, IEffect>? specialize)
    {
        typeName = ResolveTypeName(typeName, parameters);
        var type = requestedType == EffectImplementType.NotSpecified ? defaultType : requestedType;
        if (!TryGetFactory(new(typeName, type), out var factory) && type == ForcePreferToType && type != defaultType)
        {
            type = defaultType;
            if (TryGetFactory(new(typeName, type), out factory))
                Logger.Log($"Effect '{typeName}' has no implementation for forced type '{requestedType}'; using default '{type}'.", "warn");
        }
        if (factory is null)
            throw new NotSupportedException($"No effect implementation is registered for '{typeName}' with type '{type}'.");

        var blank = factory() ?? throw new InvalidOperationException($"Effect implementation factory returned null for '{typeName}/{type}'.");
        IEffect? effect = null;
        try
        {
            if (specialize is not null)
            {
                var typed = specialize(blank);
                if (!ReferenceEquals(typed, blank) && (typed is not TypedPictureEffect wrapper || !wrapper.Owns(blank)) && blank is IDisposable disposable) disposable.Dispose();
                blank = typed;
            }
            Validate(blank, typeName, type);
            effect = blank.WithParameters(parameters) ?? throw new InvalidOperationException($"WithParameters returned null for '{typeName}/{type}'.");
            Validate(effect, typeName, type);
            if (effect is IContinuousEffect c)
            {
                if (parameters.TryGetValue(nameof(IContinuousEffect.StartPoint), out var start) && EffectParamConvert.TryConvertToInt(start, out var startPoint))
                    c.StartPoint = startPoint;
                if (parameters.TryGetValue(nameof(IContinuousEffect.EndPoint), out var end) && EffectParamConvert.TryConvertToInt(end, out var endPoint))
                    c.EndPoint = endPoint;
                if (parameters.TryGetValue(nameof(IContinuousEffect.IsScoped), out var scoped) && EffectParamConvert.TryConvertToBool(scoped, out var isScoped))
                    c.IsScoped = isScoped;
            }
            return effect;
        }
        catch
        {
            if (effect is IDisposable d) d.Dispose();
            throw;
        }
        finally
        {
            if (!ReferenceEquals(blank, effect) && blank is IDisposable d) d.Dispose();
        }
    }

    private static void Validate(IEffect effect, string typeName, EffectImplementType type)
    {
        if (effect.TypeName != typeName || effect.ImplementType != type)
            throw new InvalidOperationException($"Effect implementation returned '{effect.TypeName}/{effect.ImplementType}' for '{typeName}/{type}'.");
    }

    private static string ResolveTypeName(string typeName, Dictionary<string, object>? parameters)
    {
        if (typeName is not ("BlendModeMixture" or "OverlayBlend")) return typeName;
        var mode = parameters?.GetValueOrDefault("MixtureType")?.ToString() ?? "OverlayBlend";
        return mode is "Add" or "Subtract" or "Multiply" or "Screen" or "OverlayBlend" or "Darken" or "Lighten" or "Difference"
            ? mode + "Mixture"
            : throw new NotSupportedException($"Unsupported mixture type '{mode}'.");
    }

    private bool TryGetFactory(EffectImplementationKey key, out Func<IEffect>? factory)
    {
        factory = null;
        if (!Volatile.Read(ref _factories).TryGetValue(key, out var factories) || factories.Count == 0) return false;
        if (factories.Count == 1)
        {
            factory = factories.Values.First();
            return true;
        }
        if (!PreferredPlugins.TryGetValue(key, out var pluginId))
            throw new InvalidOperationException($"Multiple plugins provide '{key.TypeName}/{key.ImplementType}', but no preferred plugin is configured.");
        if (!factories.TryGetValue(pluginId, out factory))
            throw new InvalidOperationException($"Preferred plugin '{pluginId}' does not provide '{key.TypeName}/{key.ImplementType}'.");
        return true;
    }

    private Dictionary<EffectImplementationKey, Dictionary<string, Func<IEffect>>> CloneWithoutPlugin(string pluginId)
    {
        var result = new Dictionary<EffectImplementationKey, Dictionary<string, Func<IEffect>>>();
        foreach (var (key, candidates) in _factories)
        {
            var copy = new Dictionary<string, Func<IEffect>>(candidates, StringComparer.Ordinal);
            copy.Remove(pluginId);
            if (copy.Count > 0) result[key] = copy;
        }
        return result;
    }
}
