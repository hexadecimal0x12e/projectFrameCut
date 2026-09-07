using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.Security.Cryptography;

namespace projectFrameCut.Services;

internal static class PluginIsolationFactory
{
    public static async ValueTask<IPluginBase> CreateAsync(IPluginBase local, PluginPackageVerificationResult verification, string pluginRoot, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        bool hasPictureProviders = local.EffectProviderProvider.Values.Any(x => x().TypeOfEffect is EffectType.NormalEffect or EffectType.ContinuousEffect or EffectType.MixtureProvider or EffectType.SourceReplacement);
        if (!hasPictureProviders && local.VideoSourceProvider.Count == 0) return local;
        var client = await StartClientAsync(local.PluginID, local.Configuration, verification, pluginRoot, cancellationToken);
        try
        {
            Logger.Log($"Plugin '{local.PluginID}' picture providers are running in an AppContainer isolation session.");
            return local is IApplicationPluginBase app ? new IsolatedApplicationPluginProxy(app, client) : new IsolatedPluginProxy(local, client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
#else
        await Task.CompletedTask;
        return local;
#endif
    }

    public static async ValueTask<IPluginBase> CreateProjectPluginAsync(
        PluginPackageVerificationResult verification,
        string pluginRoot,
        ProjectPluginDeclaration declaration,
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
#if WINDOWS
        var client = await StartClientAsync(verification.Metadata.PluginID, configuration, verification, pluginRoot, cancellationToken);
        try
        {
            Logger.Log($"Project plugin '{verification.Metadata.PluginID}' is running in an AppContainer isolation session.");
            return new RemoteProjectPluginProxy(verification.Metadata, client, declaration, configuration);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Project plugins require the Windows AppContainer isolation runtime.");
#endif
    }

#if WINDOWS
    private static async ValueTask<PluginIsolationClient> StartClientAsync(
        string pluginId,
        Dictionary<string, string> configuration,
        PluginPackageVerificationResult verification,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        var familyName = Platforms.Windows.WindowsPluginIsolationPlatform.PackageFamilyName;
        var session = await new Platforms.Windows.WindowsPluginIsolationPlatform().StartAsync(new PluginIsolationLaunchContext
        {
            PluginId = pluginId,
            PluginRoot = pluginRoot,
            SessionRoot = Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.SessionDirectory, Guid.NewGuid().ToString("N")),
            AuthenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            PluginEncryptionKey = PluginTrustValidator.DerivePluginEncryptionKey(verification.SigningCertificate),
            InstancePackageName = familyName,
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            var client = new PluginIsolationClient(session);
            await client.LoadAsync(Render.Plugin.PluginManager.CurrentLocale, configuration, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
#endif
}
