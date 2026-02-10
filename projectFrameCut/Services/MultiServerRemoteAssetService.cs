using projectFrameCut.APIClient.Models;
using projectFrameCut.Render.RenderAPIBase.Project;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Services
{
    /// <summary>
    /// 多服务器远程资源服务
    /// 支持从多个远程服务器获取资源并合并
    /// </summary>
    public class MultiServerRemoteAssetService
    {
        private static MultiServerRemoteAssetService? _instance;
        private readonly RemoteServerService _serverService;
        private readonly JsonSerializerOptions _jsonOptions;

        private MultiServerRemoteAssetService()
        {
            _serverService = RemoteServerService.Instance;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public static MultiServerRemoteAssetService Instance => _instance ??= new MultiServerRemoteAssetService();

        /// <summary>
        /// 从所有已登录且启用的服务器获取资源
        /// </summary>
        public async Task<List<AssetWithServerInfo>> GetAllAssetsFromAllServersAsync()
        {
            var allAssets = new List<AssetWithServerInfo>();
            var servers = _serverService.GetAllServers()
                .Where(s => s.IsEnabled && s.IsLoggedIn && !s.IsTokenExpired)
                .ToList();

            // 并行从所有服务器获取资源
            var tasks = servers.Select(server => GetAssetsFromServerAsync(server));
            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                allAssets.AddRange(result);
            }

            return allAssets;
        }

        /// <summary>
        /// 从指定服务器获取资源
        /// </summary>
        public async Task<List<AssetWithServerInfo>> GetAssetsFromServerAsync(RemoteServer server)
        {
            var assetsWithServerInfo = new List<AssetWithServerInfo>();

            try
            {
                var assets = await GetAssetsFromServerInternalAsync(server);
                
                foreach (var asset in assets)
                {
                    assetsWithServerInfo.Add(new AssetWithServerInfo
                    {
                        Asset = asset,
                        ServerId = server.Id,
                        ServerName = server.Name,
                        ServerUrl = server.Url
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"从服务器 {server.Name} 获取资源失败: {ex.Message}");
            }

            return assetsWithServerInfo;
        }

        /// <summary>
        /// 从服务器获取资源列表（内部方法）
        /// </summary>
        private async Task<List<AssetItem>> GetAssetsFromServerInternalAsync(RemoteServer server)
        {
            using var client = CreateHttpClient(server);
            
            var uri = BuildUri(server, "api/assets/popular");
            var response = await client.GetAsync(uri);
            
            if (response.IsSuccessStatusCode)
            {
                var jsonString = await response.Content.ReadAsStringAsync();
                var assets = JsonSerializer.Deserialize<List<AssetItem>>(jsonString, _jsonOptions);
                return assets ?? new List<AssetItem>();
            }
            else
            {
                throw new Exception($"Failed to fetch assets: {response.StatusCode} - {response.ReasonPhrase}");
            }
        }

        /// <summary>
        /// 从指定服务器获取资产详情
        /// </summary>
        public async Task<AssetItem?> GetAssetDetailAsync(string serverId, string assetId)
        {
            var server = _serverService.GetServerById(serverId);
            if (server == null || !server.IsLoggedIn)
                return null;

            try
            {
                using var client = CreateHttpClient(server);
                var uri = BuildUri(server, $"assets/detail/{assetId}");
                var response = await client.GetAsync(uri);
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    var asset = JsonSerializer.Deserialize<AssetItem>(jsonString, _jsonOptions);
                    return asset;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取资产详情失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 从指定服务器获取文件访问令牌
        /// </summary>
        public async Task<FileTokenResponse?> GetFileTokenAsync(string serverId, string assetId)
        {
            var server = _serverService.GetServerById(serverId);
            if (server == null || !server.IsLoggedIn)
                return null;

            try
            {
                using var client = CreateHttpClient(server);
                var uri = BuildUri(server, $"api/assets/getFile/{assetId}");
                var response = await client.GetAsync(uri);
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    var tokenResponse = JsonSerializer.Deserialize<FileTokenResponse>(jsonString, _jsonOptions);
                    return tokenResponse;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取文件令牌失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 搜索所有服务器的资源
        /// </summary>
        public async Task<List<AssetWithServerInfo>> SearchAssetsAcrossServersAsync(string keyword)
        {
            var allResults = new List<AssetWithServerInfo>();
            var servers = _serverService.GetAllServers()
                .Where(s => s.IsEnabled && s.IsLoggedIn && !s.IsTokenExpired)
                .ToList();

            var tasks = servers.Select(server => SearchAssetsInServerAsync(server, keyword));
            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                allResults.AddRange(result);
            }

            return allResults;
        }

        /// <summary>
        /// 在指定服务器中搜索资源
        /// </summary>
        private async Task<List<AssetWithServerInfo>> SearchAssetsInServerAsync(RemoteServer server, string keyword)
        {
            var assetsWithServerInfo = new List<AssetWithServerInfo>();

            try
            {
                using var client = CreateHttpClient(server);
                var uri = BuildUri(server, $"assets/search?keyword={Uri.EscapeDataString(keyword)}");
                var response = await client.GetAsync(uri);
                
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    var assetIds = JsonSerializer.Deserialize<string[]>(jsonString, _jsonOptions);
                    
                    if (assetIds != null)
                    {
                        foreach (var assetId in assetIds)
                        {
                            var asset = await GetAssetDetailAsync(server.Id, assetId);
                            if (asset != null)
                            {
                                assetsWithServerInfo.Add(new AssetWithServerInfo
                                {
                                    Asset = asset,
                                    ServerId = server.Id,
                                    ServerName = server.Name,
                                    ServerUrl = server.Url
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"在服务器 {server.Name} 搜索资源失败: {ex.Message}");
            }

            return assetsWithServerInfo;
        }

        /// <summary>
        /// 创建配置好的 HttpClient
        /// </summary>
        private HttpClient CreateHttpClient(RemoteServer server)
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
            client.Timeout = TimeSpan.FromSeconds(30);

            // 添加认证头
            if (!string.IsNullOrEmpty(server.AuthToken))
            {
                client.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.AuthToken);
            }

            return client;
        }

        /// <summary>
        /// 构建完整的 URI
        /// </summary>
        private Uri BuildUri(RemoteServer server, string relativePath)
        {
            var baseUrl = server.Url.TrimEnd('/');
            var fullUrl = $"{baseUrl}/{relativePath.TrimStart('/')}";
            return new Uri(fullUrl);
        }
    }

    /// <summary>
    /// 带有服务器信息的资产模型
    /// </summary>
    public class AssetWithServerInfo
    {
        /// <summary>
        /// 资产信息
        /// </summary>
        public AssetItem Asset { get; set; } = new();

        /// <summary>
        /// 服务器ID
        /// </summary>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>
        /// 服务器名称
        /// </summary>
        public string ServerName { get; set; } = string.Empty;

        /// <summary>
        /// 服务器URL
        /// </summary>
        public string ServerUrl { get; set; } = string.Empty;
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
