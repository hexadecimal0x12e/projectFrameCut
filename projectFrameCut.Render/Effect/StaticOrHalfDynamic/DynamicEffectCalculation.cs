using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.Plugins;

namespace projectFrameCut.Render.Effect;

public static class DynamicEffectCalculation
{
    public const string ModeKey = "EffectCalculationMode";
    public const string EnabledSetting = "render_dynamicEffectCalculation";
    public const string StepsSetting = "render_dynamicEffectMaxSteps";
    public static string Mode(IReadOnlyDictionary<string, object>? data) => data?.GetValueOrDefault(ModeKey)?.ToString() ?? "global";
    private static string? Setting(string key) => GlobalPluginHelper.MessagingService?.Call("projectFrameCut.Program", "GetSetting", [key])?.ToString();
    public static bool Enabled(IReadOnlyDictionary<string, object>? data) => Mode(data) switch
    {
        "dynamic" => true,
        "original" => false,
        _ => bool.TryParse(Setting(EnabledSetting), out var enabled) && enabled,
    };
    public static bool HasTypeErrors(EffectFieldTypeResolution types, IReadOnlyDictionary<string, object>? data) =>
        types.Diagnostics.Any(d => d.Code != "BindingCycle" || !Enabled(data));
    public static int MaxSteps => int.TryParse(Setting(StepsSetting), out int steps) && steps > 0 ? steps : 10000;
    internal static bool Supports(EffectStage stage) => stage is EffectStage.Render or EffectStage.VectorContent or EffectStage.Composition;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IClip, object> budgetKeys = new();
    private sealed record BudgetKey(Guid Clip, uint Frame);
    private sealed class Budget { public long Steps; }
    internal static Action<Guid> CreateCounter(IClip? clip, uint frame, EffectStage stage, int limit)
    {
        var key = new BudgetKey(clip?.Id ?? Guid.Empty, frame);
        var context = ClipValueFrameContext.Current;
        Budget budget;
        if (context?.FrameIndex == frame)
        {
            if (!context.TryGetComputed<Budget>(key, out var stored)) context.SetComputed(key, stored = new Budget());
            budget = stored!;
        }
        else budget = ValueProviderFrameContext.GetOrCompute(clip is null ? key : budgetKeys.GetValue(clip, _ => new object()), () => new Budget());
        return id =>
        {
            if(limit <= 0) return;
            if (++budget.Steps > limit)
                throw new DynamicCalculationLimitException(limit, frame, id);
        };
    }
}

public sealed class DynamicCalculationLimitException : InvalidOperationException
{
    public DynamicCalculationLimitException(int limit, uint frame, Guid provider) : base(Describe(limit, frame, provider)) { }

    private static string Describe(int limit, uint frame, Guid provider)
    {
        var text = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources?
            .ClipFallback_DynamicCalculationLimitDetails(limit, frame.ToString(), provider.ToString());
        return string.IsNullOrEmpty(text) || text.StartsWith("Unset localization item:", StringComparison.Ordinal)
            ? ISimpleLocalizerBase_PropertyPanel.GetMapping()["zh-CN"].ClipFallback_DynamicCalculationLimitDetails(limit, frame.ToString(), provider.ToString())
            : text;
    }
}
