using projectFrameCut.Render.RenderAPIBase.Project;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.APIClient
{
    public static class RemoteAssetServices
    {
        /// <summary>
        /// 创建配置好的 HttpClient（开发环境会忽略 SSL 证书验证）
        /// </summary>
        /// <param name="includeAuth">是否包含认证信息</param>
        private static HttpClient CreateHttpClient(bool includeAuth = false)
        {
#if DEBUG
            // 开发环境：忽略 SSL 证书验证
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            var client = new HttpClient(handler);
#else
            // 生产环境：使用默认配置
            var client = new HttpClient();
#endif

            // 如果需要认证且用户已登录，添加Authorization头
            if (includeAuth)
            {
                var token = TokenManager.CurrentToken;
                if (!string.IsNullOrEmpty(token))
                {
                    client.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
            }

            return client;
        }

        /// <summary>
        /// 获取所有可重用资产
        /// </summary>
        public static async Task<List<AssetItem>> GetAllAssets()
        {
            var uri = APIClientBase.GetUri(ServiceType.ApiServer, "api/assets/popular");
            using var client = CreateHttpClient(includeAuth: true);
            var response = await client.GetAsync(uri);
            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true // 忽略属性名大小写
                };
                var assets = JsonSerializer.Deserialize<List<AssetItem>>(jsonString, options);
                return assets ?? new List<AssetItem>();
            }
            else
            {
                throw new Exception($"Failed to fetch all assets: {response.StatusCode} - {response.ReasonPhrase}");
            }
        }
        
        /// <summary>
        /// 获取资产的文件访问 Token
        /// </summary>
        public static async Task<FileTokenResponse> GetFileToken(string assetId)
        {
            var uri = APIClientBase.GetUri(ServiceType.ApiServer, $"api/assets/getFile/{assetId}");
            using var client = CreateHttpClient(includeAuth: true);
            var response = await client.GetAsync(uri);
            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true // 忽略属性名大小写
                };
                var tokenResponse = JsonSerializer.Deserialize<FileTokenResponse>(jsonString, options);
                return tokenResponse ?? throw new InvalidDataException($"Invalid token response for asset: {assetId}");
            }
            else
            {
                throw new Exception($"Failed to get file token: {response.StatusCode} - {response.ReasonPhrase}");
            }
        }
        
        public static async Task<AssetItem> GetAssetDetail(string id)
        {
            var uri = APIClientBase.GetUri(ServiceType.ApiServer, $"assets/detail/{id}");
            using var client = CreateHttpClient(includeAuth: true);
            var response = await client.GetAsync(uri);
            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true // 忽略属性名大小写
                };
                var assets = JsonSerializer.Deserialize<AssetItem>(jsonString, options);
                return assets ?? throw new InvalidDataException($"Invalid asset data received for id: {id}");
            }
            else
            {
                throw new Exception($"Failed to fetch assets: {response.StatusCode} - {response.ReasonPhrase}");
            }
        }

        public static async Task<IEnumerable<string>> SearchAssets(string keyword) 
        {
            var uri = APIClientBase.GetUri(ServiceType.ApiServer, $"assets/search?keyword={Uri.EscapeDataString(keyword)}");
            using var client = CreateHttpClient(includeAuth: true);
            var response = await client.GetAsync(uri);
            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true // 忽略属性名大小写
                };
                var assets = JsonSerializer.Deserialize<string[]>(jsonString, options);
                return assets ?? [];
            }
            else
            {
                throw new Exception($"Failed to fetch assets: {response.StatusCode} - {response.ReasonPhrase}");
            }

        }
    }
    
    /// <summary>
    /// 文件访问 Token 响应
    /// </summary>
    public class FileTokenResponse
    {
        public string token { get; set; } = string.Empty;
        public string assetId { get; set; } = string.Empty;
        public string assetName { get; set; } = string.Empty;
        public int expiresIn { get; set; }
    }
}
