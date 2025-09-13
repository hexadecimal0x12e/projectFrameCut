using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render
{
    public static class Logger
    {
        public static void Log(string message)
        {
            Log($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
    }
}
