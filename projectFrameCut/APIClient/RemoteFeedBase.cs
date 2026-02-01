using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text;

namespace projectFrameCut.APIClient
{
    public static class RemoteFeedBase
    {
        public static IReadOnlyDictionary<string, ServerEntry> Servers => servers;

        static ConcurrentDictionary<string, ServerEntry> servers = new();

        public static HttpClient CreateClient()
        {
#if DEBUG
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                // 禁用自动解压缩，避免响应不完整问题
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                // 使用HTTP/1.1
                MaxConnectionsPerServer = 10
            };
            var client = new HttpClient(handler);
            client.DefaultRequestVersion = new Version(1, 1); // 强制使用HTTP/1.1
#else
            var client = new HttpClient();
#endif
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        public static async Task Register(string baseAddress)
        {
            var client = CreateClient();
            var rsp = await client.GetAsync(baseAddress);
            if((await rsp.Content.ReadFromJsonAsync<ServerEntry>()) is ServerEntry e)
            {
                servers.AddOrUpdate(e.Id, e, (_, _) => e);
            }
        }

    }

    public class ServerEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string BaseAddress { get; set; }
        public string[] SupportedOAuthProvider { get; set; }

        public Dictionary<string,string> Endpoints { get; set; }

        public Uri GetUri(ServiceType type, string relativePath, string query = "")
        {
            var builder = new UriBuilder(Endpoints[type.ToString()]);
            builder.Path = relativePath;
            builder.Query = query;
            return builder.Uri;
        }
    }
}
