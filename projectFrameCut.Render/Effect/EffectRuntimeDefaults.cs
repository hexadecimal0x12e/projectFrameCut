using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;

namespace projectFrameCut.Render.Effect;

public sealed class EffectRuntimeContext : IDisposable
{
    private readonly ThreadLocal<Cache> caches = new(() => new(), true);
    private readonly Lock lifetime = new();
    private volatile bool disposed;

    private sealed class Cache
    {
        public readonly Lock Gate = new();
        public readonly Dictionary<string, IEffect> Effects = [];
        public string Configuration = "";

        public void Clear()
        {
            foreach (var effect in Effects.Values.OfType<IDisposable>()) effect.Dispose();
            Effects.Clear();
        }
    }

    public IDisposable Enter() => EffectRuntimeDefaults.Enter(this);

    internal T Execute<T>(string key, string typeName, EffectImplementType type, Dictionary<string, object> parameters, Func<IEffect, T> execute)
    {
        Cache cache;
        using (lifetime.EnterScope())
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cache = caches.Value!;
        }
        using var scope = cache.Gate.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        var registry = IPluginBase.EffectImplementations;
        var configuration = $"{registry.Revision}/{EffectHelper.ConfigurationRevision}/{EffectHelper.ForcePreferToType}/{ClassicOverlayMixture.EnableApproximatePath}/" +
            string.Join(";", registry.PreferredPlugins.OrderBy(x => x.Key.TypeName).ThenBy(x => x.Key.ImplementType).Select(x => $"{x.Key}:{x.Value}"));
        if (cache.Configuration != configuration)
        {
            cache.Clear();
            cache.Configuration = configuration;
        }
        if (!cache.Effects.TryGetValue(key, out var effect) || effect.ImplementType != type)
        {
            cache.Effects.Remove(key);
            if (effect is IDisposable d) d.Dispose();
            cache.Effects[key] = effect = registry.Create(typeName, type, type, parameters);
        }
        return execute(effect);
    }

    public void Dispose()
    {
        Cache[] all;
        using (lifetime.EnterScope())
        {
            if (disposed) return;
            disposed = true;
            all = caches.Values.ToArray();
        }
        foreach (var cache in all)
        {
            using var scope = cache.Gate.EnterScope();
            cache.Clear();
        }
        caches.Dispose();
    }
}

public static class EffectRuntimeDefaults
{
    private static readonly AsyncLocal<EffectRuntimeContext?> current = new();
    public static IMixture Mixture { get; } = new DefaultMixture();

    private sealed class Scope(EffectRuntimeContext? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }

    internal static IDisposable Enter(EffectRuntimeContext context)
    {
        var previous = current.Value;
        current.Value = context;
        return new Scope(previous);
    }

    internal static T Execute<T>(string key, string typeName, EffectImplementType defaultType, Dictionary<string, object> parameters, Func<IEffect, T> execute, EffectImplementType requestedType = EffectImplementType.NotSpecified)
    {
        var type = requestedType == EffectImplementType.NotSpecified
            ? EffectHelper.ForcePreferToType ?? EffectHelper.DefaultImplementsType.GetValueOrDefault($"{Plugin.InternalPluginBase.InternalPluginBaseID}.{typeName}", defaultType)
            : requestedType;
        if (type == EffectImplementType.NotSpecified) type = defaultType;
        key = $"{key}/{typeName}/{type}";
        if (current.Value is { } context) return context.Execute(key, typeName, type, parameters, execute);
        using var temporary = new EffectRuntimeContext();
        return temporary.Execute(key, typeName, type, parameters, execute);
    }

    public static IPicture Place(IPicture source, int width, int height, int x = 0, int y = 0) => Execute(
        "Place", "Place", EffectImplementType.HwAcceleration,
        new() { ["StartX"] = 0, ["StartY"] = 0 }, e =>
        {
            e.Parameters["StartX"] = x;
            e.Parameters["StartY"] = y;
            return ((INormalEffect)e).Render(source, width, height);
        });

    private sealed class DefaultMixture : ClassicOverlayMixture
    {
        public override IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB) => Execute(
            "Mixture", "ClassicOverlayMixture", EffectImplementType.HwAcceleration, [],
            e => ((IMixture)e).Mix(basePicture, topPicture, targetPPB));

        public override IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight) => Execute(
            "Mixture", "ClassicOverlayMixture", EffectImplementType.HwAcceleration, [],
            e => ((IMixture)e).Mix(basePicture, topPicture, targetPPB, topStartX, topStartY, targetWidth, targetHeight));
    }
}
