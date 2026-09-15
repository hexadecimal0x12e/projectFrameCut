using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.Security.Cryptography;

namespace projectFrameCut.Services;

internal static class PluginIsolationFactory
{
    public static async ValueTask<IPluginBase> CreateExternalAsync(
        PluginPackageVerificationResult verification,
        string pluginRoot,
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        var launch = verification.Metadata.ExternalBackend?.GetCurrentPlatform()
            ?? throw new PlatformNotSupportedException("The external plugin package does not declare an entry point for this platform.");
        var session = await new ExternalPluginBackendPlatform(launch).StartAsync(new PluginIsolationLaunchContext
        {
            PluginId = verification.Metadata.PluginID,
            PluginRoot = pluginRoot,
            SessionRoot = Path.Combine(ExternalPluginBackendPlatform.SessionDirectory, Guid.NewGuid().ToString("N")),
            AuthenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            PluginEncryptionKey = string.Empty,
            InstancePackageName = ExternalPluginBackendPlatform.InstanceName,
            Transport = new() { TerminateOnRemoteError = true },
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            var client = new PluginIsolationClient(session);
            await client.LoadAsync(Render.Plugin.PluginManager.CurrentLocale, configuration, cancellationToken).ConfigureAwait(false);
            return new ExternalPluginProxy(verification.Metadata, client);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<IPluginBase> CreateAsync(IPluginBase local, PluginPackageVerificationResult verification, string pluginRoot, PluginIsolationMode mode, CancellationToken cancellationToken = default)
    {
        bool hasPictureProviders = local.EffectProviderProvider.Values.Any(x => x().TypeOfEffect is EffectType.NormalEffect or EffectType.ContinuousEffect or EffectType.MixtureProvider or EffectType.SourceReplacement);
        bool hasIsolatableCapabilities = hasPictureProviders || local.VideoSourceProvider.Count > 0 ||
            local.AudioSourceProvider.Count > 0 || local.VideoWriterProvider.Count > 0 ||
            local.TransformProvider.Count > 0 || local.ComputerProvider.Count > 0 ||
            local.SoundTrackProvider.Count > 0 ||
            HasCustomImplementation(local, nameof(IPluginBase.ClipCreator)) ||
            HasCustomImplementation(local, nameof(IPluginBase.VectComponentCreator));
        if (!hasIsolatableCapabilities || mode == PluginIsolationMode.None) return local;
        PluginIsolationClient client;
        switch (mode)
        {
            case PluginIsolationMode.Containerized:
#if WINDOWS
                client = await StartClientAsync(
                    new Platforms.Windows.WindowsPluginIsolationPlatform(),
                    local.PluginID,
                    local.Configuration,
                    verification,
                    pluginRoot,
                    Platforms.Windows.WindowsPluginIsolationPlatform.PackageFamilyName,
                    Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.SessionDirectory, Guid.NewGuid().ToString("N")),
                    cancellationToken);
                break;
#else
                return local;
#endif
            case PluginIsolationMode.ProcessIsolation:
                if (!DesktopPluginIsolationPlatform.IsSupported) return local;
                client = await StartClientAsync(
                    new DesktopPluginIsolationPlatform(),
                    local.PluginID,
                    local.Configuration,
                    verification,
                    pluginRoot,
                    DesktopPluginIsolationPlatform.InstanceName,
                    Path.Combine(DesktopPluginIsolationPlatform.SessionDirectory, Guid.NewGuid().ToString("N")),
                    cancellationToken);
                break;
            default:
                return local;
        }
        try
        {
            Logger.Log($"Plugin '{local.PluginID}' picture providers are running in {mode} isolation mode.");
            return local is IApplicationPluginBase app ? new IsolatedApplicationPluginProxy(app, client) : new IsolatedPluginProxy(local, client);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<IPluginBase> CreateProjectPluginAsync(
        PluginPackageVerificationResult verification,
        string pluginRoot,
        ProjectPluginDeclaration declaration,
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (verification.Metadata.IsAppLevelPlugin == true)
            throw new NotSupportedException("Application-level plugins cannot run as project plugins.");
        if (!PluginService.SupportsIsolationMode(PluginIsolationMode.Containerized, verification.Metadata.MaximumSupportedIsolationMode))
            throw new NotSupportedException($"Project plugins require {PluginIsolationMode.Containerized} isolation, which this plugin does not support.");
#if WINDOWS
        var client = await StartClientAsync(
            new Platforms.Windows.WindowsPluginIsolationPlatform(),
            verification.Metadata.PluginID,
            configuration,
            verification,
            pluginRoot,
            Platforms.Windows.WindowsPluginIsolationPlatform.PackageFamilyName,
            Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.SessionDirectory, Guid.NewGuid().ToString("N")),
            cancellationToken);
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

    private static async ValueTask<PluginIsolationClient> StartClientAsync(
        IPluginIsolationPlatform platform,
        string pluginId,
        Dictionary<string, string> configuration,
        PluginPackageVerificationResult verification,
        string pluginRoot,
        string instanceName,
        string sessionRoot,
        CancellationToken cancellationToken)
    {
        var session = await platform.StartAsync(new PluginIsolationLaunchContext
        {
            PluginId = pluginId,
            PluginRoot = pluginRoot,
            SessionRoot = sessionRoot,
            AuthenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            PluginEncryptionKey = PluginTrustValidator.DerivePluginEncryptionKey(verification.SigningCertificate),
            InstancePackageName = instanceName,
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

    private static bool HasCustomImplementation(IPluginBase plugin, string methodName) =>
        typeof(IPluginBase).GetMethod(methodName) is { } method &&
        plugin.GetType().GetInterfaceMap(typeof(IPluginBase)).TargetMethods
            .Any(x => x.Name == method.Name && x.DeclaringType != typeof(IPluginBase));
}
