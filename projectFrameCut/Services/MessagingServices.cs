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
            messaging.RegisterCallBack(ProgramCallerID, "GetSetting", InternalCallBack_GetSetting);
        }

        private static object? InternalCallBack_GetSetting(object[] arg)
        {
            if (arg.Length != 1 || arg[0] is not string key)
            {
                return null;
            }
            return SettingsManager.GetSetting(key);
        }
    }
}
