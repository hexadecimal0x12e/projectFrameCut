using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using projectFrameCut.ApplicationAPIBase.Effect;
using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;

namespace projectFrameCut.Services;

public static class TransformServices
{
    public static Dictionary<string, Func<IEffectProvider>> GetAvailableTransforms(bool audio = false) =>
        EffectServices.GetAvailableEffectProviders().Where(p =>
        {
            var provider = p.Value();
            return provider.TypeOfEffect == EffectType.Transform && provider.Target.HasFlag(audio ? EffectTarget.Audio : EffectTarget.Video);
        }).ToDictionary();

    public static string GetTransformName(string typeName)
    {
        var name = EffectProviderDisplayDefaults.ResolveLocalized("DisplayName_Transform_" + typeName,
            PluginManager.GetLocalizationItem("DisplayName_Transform_" + typeName, typeName), PluginManager.CurrentLocale);
        return name.StartsWith("Unset localization item:", StringComparison.Ordinal) ? typeName : name;
    }

    public static TransformRenderOrder DefaultRenderOrder =>
        Enum.TryParse<TransformRenderOrder>(SettingsManager.GetSetting("Edit_DefaultTransformRenderOrder", nameof(TransformRenderOrder.AfterEffects)), out var order)
            && Enum.IsDefined(order) ? order : TransformRenderOrder.AfterEffects;

    public static ITransform Create(IEffectProvider provider)
    {
        var effects = provider.Build();
        try
        {
            if (effects.Length != 1 || effects[0] is not ITransform transform)
                throw new InvalidOperationException($"Provider {provider.TypeName} must build one transform effect.");
            transform.Initialize();
            return transform;
        }
        catch
        {
            foreach (var effect in effects) (effect as IDisposable)?.Dispose();
            throw;
        }
    }
}
