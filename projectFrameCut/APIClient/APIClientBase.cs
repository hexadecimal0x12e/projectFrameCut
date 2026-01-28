using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.APIClient
{
    public static class APIClientBase
    {
#if DEBUG
        public static string Stage = "127";

        public static string APIBaseUrl = "0.0.1";

        public static int APIPort_ApiServer = 5427;
        public static int APIPort_FileServer = 5032;
#else
        public static string Stage = "develop";

        public static string APIBaseUrl = "example.com";

        public static int APIPort_ApiServer = 7146;
        public static int APIPort_FileServer = 7576;
#endif

        public static int APIVersion = 1;

        /// <summary>
        /// 获取协议方案（DEBUG 模式使用 HTTP，生产环境使用 HTTPS）
        /// </summary>
        public static string GetScheme()
        {
#if DEBUG
            return "http";
#else
            return "https";
#endif
        }

        public static Uri GetUri(int port, string relativePath, string query = "")
        {
            var builder = new UriBuilder
            {
                Scheme = GetScheme(),
                Host = $"{Stage}.{APIBaseUrl}",
                Port = port,
                Path = relativePath,
                Query = query
            };
            return builder.Uri;
        }


    }
}
