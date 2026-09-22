using projectFrameCut.Render.Messaging;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace projectFrameCut.Services
{
    public static class MessagingServices
    {
        public const string ProgramCallerID = "projectFrameCut.Program";
        public static IMessagingService MessagingService
        {
            [DebuggerStepThrough()]
            get
            {
                if (!inited || messaging is null) throw new InvalidOperationException("MessagingServices has not been inited. Please call MessagingServices.Init() before using it.");
                return messaging;
            }
        }

        private static bool inited = false;
        private static IMessagingService? messaging;

        public static void Init()
        {
            if (inited) return;
            inited = true;
            messaging = new GeneralMessagingService();
            messaging.RegisterCallBack(ProgramCallerID, new PluginCommandDescriptor
            {
                Command = "GetSetting",
                Description = "Gets an application setting by key.",
                Parameters =
                [
                    new()
                    {
                        Name = "key",
                        Type = "string",
                        Description = "The setting key.",
                        Required = true,
                    },
                ],
            }, InternalCallBack_GetSetting);
            messaging.RegisterCallBack(ProgramCallerID, new PluginCommandDescriptor
            {
                Command = "GetOneTimeProjectRPCToken",
                Description = "Gets an one-time RPC token for connecting to backend, allow project modifications, scripting and automation.",
                Parameters =
                [
                    new()
                    {
                        Name = "name",
                        Type = "string",
                        Description = "The caller name.",
                        Required = true,
                    },
                    new()
                    {
                        Name = "purpose",
                        Type = "string",
                        Description = "The purpose of the token.",
                        Required = true,
                    },
                    new()
                    {
                        Name = "isIsolated",
                        Type = "bool",
                        Description = "Indicate whether this plugin is running under a isolated engine.",
                        Required = true,
                    }
                ],
            }, InternalCallBack_GetRPCToken);
        }

        private static object? InternalCallBack_GetSetting(object[] arg)
        {
            if (arg.Length != 1 || arg[0] is not string key)
            {
                return null;
            }
            return SettingsManager.GetSetting(key);
        }
        private static object? InternalCallBack_GetRPCToken(object[] arg)
        {
            if (arg.Length != 3
                || arg[0] is not string name
                || arg[1] is not string purpose
                || arg[2] is not bool isIsolated
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(purpose))
                return null;

#if !WINDOWS
            if (isIsolated) throw new PlatformNotSupportedException("Isolated RPC pipes are only supported on Windows.");
#endif
            var client = RenderRpcBootstrap.Client;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var session = client.GetGuiProjectSessionAsync(new EmptyRequest(), timeout.Token).GetAwaiter().GetResult();
            if (session.SessionId == Guid.Empty)
                throw new InvalidOperationException("No GUI project session is currently available.");

            var response = client.CreateGuiProjectPipeAsync(new CreateGuiProjectPipeRequest
            {
                SessionId = session.SessionId,
                Isolated = isIsolated,
                ClientName = name,
            }, timeout.Token).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(response.Token))
                throw new InvalidOperationException("The render backend returned an empty RPC pipe token.");

            Logger.Log($"Created one-time project RPC pipe for '{name}' ({purpose}){(isIsolated ? " with isolated access" : string.Empty)}.");
            return RenderProtocol.AdditionalPipePrefix + response.Token;
        }
    }
}
