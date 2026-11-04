namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public static class ClipInitializationMetadata
{
    public const string FailedKey = "__ClipInitializationFailed";
    public const string StageKey = "__ClipInitializationFailureStage";
    public const string MessageKey = "__ClipInitializationFailureMessage";
    public const string FailedEffectsKey = "__ClipInitializationFailedEffects";
    public const string FailedEffectProvidersKey = "__ClipInitializationFailedEffectProviders";

    public static void Clear(Dictionary<string, object>? data)
    {
        if (data is null) return;
        data.Remove(FailedKey);
        data.Remove(StageKey);
        data.Remove(MessageKey);
        data.Remove(FailedEffectsKey);
        data.Remove(FailedEffectProvidersKey);
    }

    public static Dictionary<string, object>? ForPersistence(Dictionary<string, object>? data)
    {
        if (data is null || !data.Keys.Any(key => key is FailedKey or StageKey or MessageKey
            or FailedEffectsKey or FailedEffectProvidersKey)) return data;
        var result = new Dictionary<string, object>(data, data.Comparer);
        Clear(result);
        return result;
    }
}
