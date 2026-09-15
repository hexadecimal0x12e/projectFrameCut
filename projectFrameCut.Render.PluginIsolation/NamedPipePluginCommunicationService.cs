using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class NamedPipePluginCommunicationService(string pluginId, CancellationToken lifetime) : IPluginCommunicationService, IAsyncDisposable
{
    private readonly string _pluginId = pluginId;
    private readonly CancellationToken _lifetime = lifetime;
    private readonly ConcurrentDictionary<string, PluginChannelDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginChannelDescriptor> _listenersById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<PluginChannelRequest, CancellationToken, ValueTask<PluginChannelResponse>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _listeners = [];
    private int _disposed;

    public ValueTask<IPluginChannel> ConnectAsync(string targetPluginId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_descriptors.TryGetValue(targetPluginId, out var descriptor))
            throw new UnauthorizedAccessException($"No authorized plugin channel exists for '{targetPluginId}'.");
        Logger.Log($"Plugin '{_pluginId}' connecting to authorized plugin channel '{targetPluginId}'.");
        return ValueTask.FromResult<IPluginChannel>(new NamedPipePluginChannel(descriptor, _pluginId, cancellationToken));
    }

    public void RegisterHandler(string command, Func<PluginChannelRequest, CancellationToken, ValueTask<PluginChannelResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[command] = handler;
    }

    public void UnregisterHandler(string command) => _handlers.TryRemove(command, out _);

    public async ValueTask<PluginChannelDescriptor> CreateListenerAsync(string sourcePluginId, string targetPluginId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourcePluginId)) throw new ArgumentException("The channel source plugin ID is required.", nameof(sourcePluginId));
        if (!string.Equals(targetPluginId, _pluginId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("The channel target is not this plugin.");
        var descriptor = new PluginChannelDescriptor
        {
            ChannelId = Guid.NewGuid().ToString("N"),
            PipeName = $"projectFrameCut.PluginChannel.{Guid.NewGuid():N}",
            AuthenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            SourcePluginId = sourcePluginId,
            TargetPluginId = targetPluginId,
        };
        if (!_listenersById.TryAdd(descriptor.ChannelId, descriptor)) throw new InvalidOperationException("Failed to allocate a unique plugin channel.");
        _listeners.Add(RunListenerAsync(descriptor));
        Logger.Log($"Plugin '{_pluginId}' created channel listener for '{sourcePluginId}'.");
        await Task.Yield();
        return descriptor;
    }

    public void RegisterDescriptor(PluginChannelDescriptor descriptor)
    {
        if (!string.Equals(descriptor.SourcePluginId, _pluginId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("The channel source is not this plugin.");
        if (string.IsNullOrWhiteSpace(descriptor.ChannelId) || string.IsNullOrWhiteSpace(descriptor.PipeName) || descriptor.AuthenticationToken.Length < 32)
            throw new InvalidDataException("The plugin channel descriptor is invalid.");
        _descriptors[descriptor.TargetPluginId] = descriptor;
        Logger.Log($"Plugin '{_pluginId}' registered channel to '{descriptor.TargetPluginId}'.");
    }

    private async Task RunListenerAsync(PluginChannelDescriptor descriptor)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(descriptor.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_lifetime).ConfigureAwait(false);
                while (!_lifetime.IsCancellationRequested)
                {
                    var data = await IsolationFrame.ReadAsync(pipe, _lifetime).ConfigureAwait(false);
                    if (data is null) break;
                    var request = RenderRpcSerializer.Deserialize<PluginChannelWireRequest>(data);
                    var response = await DispatchAsync(request).ConfigureAwait(false);
                    await IsolationFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(response), _lifetime).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Logger.Log(ex, $"plugin channel listener '{descriptor.ChannelId}'", this); }
    }

    private async ValueTask<PluginChannelWireResponse> DispatchAsync(PluginChannelWireRequest request)
    {
        if (request.ProtocolVersion != PluginIsolationProtocol.CurrentVersion
            || !_listenersById.TryGetValue(request.ChannelId, out var descriptor)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.AuthenticationToken), Encoding.UTF8.GetBytes(descriptor.AuthenticationToken))
            || !string.Equals(request.SourcePluginId, descriptor.SourcePluginId, StringComparison.Ordinal)
            || !string.Equals(request.TargetPluginId, descriptor.TargetPluginId, StringComparison.Ordinal)
            || !string.Equals(request.TargetPluginId, _pluginId, StringComparison.Ordinal))
            return new() { Error = "Plugin channel authentication failed." };
        if (string.IsNullOrWhiteSpace(request.Command)) return new() { Error = "Plugin channel command is required." };
        if (!_handlers.TryGetValue(request.Command, out var handler)) return new() { Error = $"Plugin channel command '{request.Command}' was not registered." };
        try
        {
            var result = await handler(new() { Command = request.Command, JsonPayload = request.JsonPayload, BinaryPayload = request.BinaryPayload }, _lifetime).ConfigureAwait(false);
            return new() { Accepted = true, JsonPayload = result.JsonPayload, BinaryPayload = result.BinaryPayload };
        }
        catch (Exception ex) { return new() { Error = ex.Message }; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Task.WhenAll(_listeners).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
    }
}

internal sealed class NamedPipePluginChannel(PluginChannelDescriptor descriptor, string sourcePluginId, CancellationToken callerLifetime) : IPluginChannel
{
    public string TargetPluginId => descriptor.TargetPluginId;

    public async ValueTask<PluginChannelResponse> RequestAsync(PluginChannelRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerLifetime, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", descriptor.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await IsolationFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(new PluginChannelWireRequest
        {
            ChannelId = descriptor.ChannelId,
            AuthenticationToken = descriptor.AuthenticationToken,
            SourcePluginId = sourcePluginId,
            TargetPluginId = descriptor.TargetPluginId,
            Command = request.Command,
            JsonPayload = request.JsonPayload,
            BinaryPayload = request.BinaryPayload,
        }), timeout.Token).ConfigureAwait(false);
        var data = await IsolationFrame.ReadAsync(pipe, timeout.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("The plugin channel was closed.");
        var response = RenderRpcSerializer.Deserialize<PluginChannelWireResponse>(data);
        if (!response.Accepted) throw new RemoteRenderException(new RemoteError { Code = RenderErrorCode.Unauthorized, Message = response.Error });
        return new() { JsonPayload = response.JsonPayload, BinaryPayload = response.BinaryPayload };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
