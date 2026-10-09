using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class RenderTestRuntime
{
    private static EffectImplementType? previousPreference;

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        previousPreference = EffectHelper.ForcePreferToType;
        EffectImplementationRegistry.Shared.Register(InternalPluginBase.InternalPluginBaseID,
            new InternalPluginBase().EffectImplementationProvider);
        EffectHelper.ForcePreferToType = EffectImplementType.IPicture;
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        EffectHelper.ForcePreferToType = previousPreference;
        EffectImplementationRegistry.Shared.Unregister(InternalPluginBase.InternalPluginBaseID);
    }
}
