using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Shared
{
    public sealed class PluginChannelRequest
    {
        public string Command { get; init; } = string.Empty;
        public string JsonPayload { get; init; } = "{}";
        public byte[] BinaryPayload { get; init; } = [];
    }

    public sealed class PluginChannelResponse
    {
        public string JsonPayload { get; init; } = "{}";
        public byte[] BinaryPayload { get; init; } = [];
    }

    public sealed class PluginCommandParameter
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "string";
        public string Description { get; init; } = string.Empty;
        public bool Required { get; init; }
        public string? DefaultJson { get; init; }
    }

    public sealed class PluginCommandDescriptor
    {
        public string Command { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public IReadOnlyList<PluginCommandParameter> Parameters { get; init; } = [];
    }

    public interface IPluginChannel : IAsyncDisposable
    {
        string TargetPluginId { get; }
        ValueTask<IReadOnlyList<PluginCommandDescriptor>> DiscoverCommandsAsync(CancellationToken cancellationToken = default);
        ValueTask<PluginChannelResponse> RequestAsync(PluginChannelRequest request, CancellationToken cancellationToken = default);
    }

    public interface IPluginCommunicationService
    {
        ValueTask<IPluginChannel> ConnectAsync(string targetPluginId, CancellationToken cancellationToken = default);
        void RegisterHandler(string command, Func<PluginChannelRequest, CancellationToken, ValueTask<PluginChannelResponse>> handler);
        void RegisterHandler(PluginCommandDescriptor command, Func<PluginChannelRequest, CancellationToken, ValueTask<PluginChannelResponse>> handler);
        void UnregisterHandler(string command);
    }

    public interface IMessagingService
    {
        /// <summary>
        /// Call the program with specific command and obtain the result from program.
        /// </summary>
        public object? Call(string targetId, string command, object[] args);
        public IReadOnlyList<PluginCommandDescriptor> Discover(string targetId);
        /// <summary>
        /// Register a callback.
        /// </summary>
        /// <remarks>
        /// the <paramref name="method"/> will be invoked when a message has been sent to MessageServer.
        /// </remarks>
        public void RegisterCallBack(string callbackId, string callbackCommand, Func<object[], object?> method);
        public void RegisterCallBack(string callbackId, PluginCommandDescriptor command, Func<object[], object?> method);
        public void UnRegisterCallBack(string callbackId);
        public void UnRegisterCallBack(string callbackId, string callbackCommand);

    }
}
