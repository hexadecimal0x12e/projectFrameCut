using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public static class StringHelper
    {
        public static string Format(this string input, params object[] args) => string.Format(input, args);
    }
}
