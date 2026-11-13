using projectFrameCut.Shared;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.Messaging
{
    public class GeneralMessagingService : IMessagingService
    {
        private readonly ConcurrentDictionary<string, MessagingUser> _users = new();

        public object? Call(string targetId, string command, object[] args)
        {
            if (_users.TryGetValue(targetId, out var user) && user.Callbacks.TryGetValue(command, out var callback))
                return callback.Method(args);
            return null;
        }

        public IReadOnlyList<PluginCommandDescriptor> Discover(string targetId)
        {
            if (!_users.TryGetValue(targetId, out var user)) return [];
            var commands = user.Callbacks.Values.Select(x => CloneDescriptor(x.Descriptor)).OrderBy(x => x.Command, StringComparer.Ordinal).ToArray();
            Logger.Log($"Discovered {commands.Length} messaging commands from '{targetId}'.");
            return commands;
        }

        public void RegisterCallBack(string callbackerId, string callbackCommand, Func<object[], object?> method)
            => RegisterCallBack(callbackerId, new PluginCommandDescriptor { Command = callbackCommand }, method);

        public void RegisterCallBack(string callbackId, PluginCommandDescriptor command, Func<object[], object?> method)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
            ArgumentNullException.ThrowIfNull(command);
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Command);
            ArgumentNullException.ThrowIfNull(method);
            var parameters = command.Parameters.Select(x =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(x.Name);
                return new PluginCommandParameter
                {
                    Name = x.Name,
                    Type = string.IsNullOrWhiteSpace(x.Type) ? "object" : x.Type,
                    Description = x.Description ?? string.Empty,
                    Required = x.Required,
                    DefaultJson = x.DefaultJson,
                };
            }).ToArray();
            if (parameters.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Length)
                throw new ArgumentException("Messaging command parameter names must be unique.", nameof(command));
            var descriptor = new PluginCommandDescriptor
            {
                Command = command.Command,
                Description = command.Description ?? string.Empty,
                Parameters = parameters,
            };
            _users.GetOrAdd(callbackId, static _ => new()).Callbacks[descriptor.Command] = new(descriptor, method);
            Logger.Log($"Messaging target '{callbackId}' registered discoverable command '{descriptor.Command}'.");
        }

        public void UnRegisterCallBack(string callbackId)
        {
            _users.TryRemove(callbackId, out _);
        }

        public void UnRegisterCallBack(string callbackId, string callbackCommand)
        {
            if (_users.TryGetValue(callbackId, out var user)) user.Callbacks.TryRemove(callbackCommand, out _);
        }

        private static PluginCommandDescriptor CloneDescriptor(PluginCommandDescriptor command) => new()
        {
            Command = command.Command,
            Description = command.Description,
            Parameters = command.Parameters.Select(x => new PluginCommandParameter
            {
                Name = x.Name,
                Type = x.Type,
                Description = x.Description,
                Required = x.Required,
                DefaultJson = x.DefaultJson,
            }).ToArray(),
        };

        private sealed class MessagingUser
        {
            public ConcurrentDictionary<string, RegisteredCallback> Callbacks { get; } = new(StringComparer.Ordinal);
        }

        private sealed record RegisteredCallback(PluginCommandDescriptor Descriptor, Func<object[], object?> Method);
    }




}
