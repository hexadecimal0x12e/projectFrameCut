using projectFrameCut.Drawing.Base;
using projectFrameCut.AIComponentContracts;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Services.AIComponent;

public interface IAIComponentClient : IAsyncDisposable
{
    bool IsSupported { get; }
    bool IsConnected { get; }
    IReadOnlyList<AICapabilityDescriptor> Capabilities { get; }

    Task<IReadOnlyList<AICapabilityDescriptor>> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<string> ExecuteTextAsync(string operation, string text, CancellationToken cancellationToken = default);
    Task<IPicture> ExecutePictureAsync(string operation, IPicture picture, CancellationToken cancellationToken = default);
    Task<IAudioSamples<float>> ExecuteAudioAsync(string operation, IAudioSamples<float> audio, CancellationToken cancellationToken = default);
}

public sealed class AIComponentUnavailableClient : IAIComponentClient
{
    public bool IsSupported => false;
    public bool IsConnected => false;
    public IReadOnlyList<AICapabilityDescriptor> Capabilities => Array.Empty<AICapabilityDescriptor>();

    public Task<IReadOnlyList<AICapabilityDescriptor>> ConnectAsync(CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<AICapabilityDescriptor>>(new PlatformNotSupportedException("The Windows System AI extension is only available on Windows."));

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<string> ExecuteTextAsync(string operation, string text, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new PlatformNotSupportedException("The Windows System AI extension is only available on Windows."));

    public Task<IPicture> ExecutePictureAsync(string operation, IPicture picture, CancellationToken cancellationToken = default)
        => Task.FromException<IPicture>(new PlatformNotSupportedException("The Windows System AI extension is only available on Windows."));

    public Task<IAudioSamples<float>> ExecuteAudioAsync(string operation, IAudioSamples<float> audio, CancellationToken cancellationToken = default)
        => Task.FromException<IAudioSamples<float>>(new PlatformNotSupportedException("The Windows System AI extension is only available on Windows."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class AIComponentClientException : IOException
{
    public AIComponentClientException(string message) : base(message) { }
    public AIComponentClientException(string message, Exception innerException) : base(message, innerException) { }
}
