using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public class HashServices
    {
        [DebuggerNonUserCode()]
        public static async Task<string> ComputeFileSHA512Async(string fileName, CancellationToken? ct = null)
        {
            string hashSHA512 = "";
            if (System.IO.File.Exists(fileName))
            {
                using (System.IO.FileStream fs = new System.IO.FileStream(fileName, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                {

                    System.Security.Cryptography.SHA512 calculator = System.Security.Cryptography.SHA512.Create();
                    Byte[] buffer = await calculator.ComputeHashAsync(fs, ct ?? CancellationToken.None);
                    calculator.Clear();

                    StringBuilder stringBuilder = new StringBuilder();
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        stringBuilder.Append(buffer[i].ToString("x2"));
                    }
                    hashSHA512 = stringBuilder.ToString();
                }
            }
            return hashSHA512;
        }
    }
}
