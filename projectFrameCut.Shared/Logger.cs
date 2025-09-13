using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    

    public static class Logger
    {   


        public static void Log(string msg) => Log(msg, "info"); //fix the vs auto completion

        public static void Log(Exception e) => Log(e, false);

        public static void Log(Exception e, bool isCritical) => Log($"{(isCritical ? "A critical " : "")}{e.GetType().Name} error: {e.Message} {e.StackTrace}", isCritical ? "Critical" : "error");

        public static void Log(Exception e, string message = "", object? sender = null) => Log($"{sender?.GetType().Name} report a {e.GetType().Name} error when trying to {message} \r\n error message: {e.Message} {e.StackTrace}{(e.Data.Contains("RemoteStackTrace") ? e.Data["RemoteStackTrace"] : "")}", "error");

        
        public static void Log(string msg, string level = "info")
        {
            Console.WriteLine(msg);


        }


    }
}
