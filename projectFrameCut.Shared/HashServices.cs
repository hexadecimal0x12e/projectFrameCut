using SixLabors.ImageSharp.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public class HashServices
    {
        [DebuggerNonUserCode()]
        public static async Task<string> ComputeFileHashAsync(string fileName, HashAlgorithm? algorithm = null, CancellationToken? ct = null)
        {
            algorithm ??= SHA256.Create();
            if (System.IO.File.Exists(fileName))
            {
                using System.IO.FileStream fs = new System.IO.FileStream(fileName, System.IO.FileMode.Open, System.IO.FileAccess.Read);
                byte[] buffer = await algorithm.ComputeHashAsync(fs, ct ?? CancellationToken.None);
                algorithm.Clear();
                return buffer.Select(c => c.ToString("x2")).Aggregate((a, b) => a + b);
            }
            throw new FileNotFoundException("File not found", fileName);
        }

        [DebuggerNonUserCode()]
        public static string ComputeStringHash(string input, HashAlgorithm? algorithm = null) 
            => (algorithm ?? SHA512.Create())
                .ComputeHash(Encoding.UTF8.GetBytes(input))
                .Select(c => c.ToString("x2"))
                .Aggregate((a, b) => a + b);

        [DebuggerNonUserCode()]
        public static string ComputeBytesHash(byte[] input, HashAlgorithm? algorithm = null)
            => (algorithm ?? SHA256.Create())
                .ComputeHash(input)
                .Select(c => c.ToString("x2"))
                .Aggregate((a, b) => a + b);
    }
}
