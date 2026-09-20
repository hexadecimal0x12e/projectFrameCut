using projectFrameCut.Render.Messaging;
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
                        Name = "purpose",
                        Type = "string",
                        Description = "The purpose of the token.",
                        Required = true,
                    },
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
            return "TODO";
        }
    }
}
