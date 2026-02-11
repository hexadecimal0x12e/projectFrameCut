using projectFrameCut.APIClient;
using projectFrameCut.APIClient.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Services
{
    /// <summary>
    /// 远程服务器管理服务
    /// </summary>
    public class RemoteServerService
    {
        private static RemoteServerService? _instance;
        private List<RemoteServer> _servers = new();
        private readonly string _serverConfigPath;
        private readonly JsonSerializerOptions _jsonOptions;

        private RemoteServerService()
        {
            var appDataPath = FileSystem.AppDataDirectory;
            _serverConfigPath = Path.Combine(appDataPath, "RemoteServers.json");
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            _ = LoadServersAsync();
        }

        public static RemoteServerService Instance => _instance ??= new RemoteServerService();

        /// <summary>
        /// 获取所有服务器
        /// </summary>
        public IReadOnlyList<RemoteServer> GetAllServers() => _servers.AsReadOnly();

        /// <summary>
        /// 根据ID获取服务器
        /// </summary>
        public RemoteServer? GetServerById(string id) => _servers.FirstOrDefault(s => s.Id == id);

        /// <summary>
        /// 添加或更新服务器
        /// </summary>
        public async Task<bool> SaveServerAsync(RemoteServer server)
        {
            if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Url))
                return false;

            var existingIndex = _servers.FindIndex(s => s.Id == server.Id);
            if (existingIndex >= 0)
            {
                _servers[existingIndex] = server;
            }
            else
            {
                _servers.Add(server);
            }

            await SaveServersToFileAsync();
            return true;
        }

        /// <summary>
        /// 删除服务器
        /// </summary>
        public async Task<bool> DeleteServerAsync(string id)
        {
            var index = _servers.FindIndex(s => s.Id == id);
            if (index < 0) return false;

            _servers.RemoveAt(index);
            await SaveServersToFileAsync();
            return true;
        }

        /// <summary>
        /// 登录到远程服务器（用户名密码）
        /// </summary>
        public async Task<RemoteServerLoginResponse> LoginAsync(string serverId, string userNameOrEmail, string password)
        {
            var server = GetServerById(serverId);
            if (server == null)
                return new RemoteServerLoginResponse { Success = false, Message = "服务器不存在" };

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var loginUrl = $"{server.Url.TrimEnd('/')}/api/auth/login";
                    
                    var request = new LoginRequest
                    {
                        UserNameOrEmail = userNameOrEmail,
                        Password = password
                    };

                    var content = new StringContent(
                        JsonSerializer.Serialize(request, _jsonOptions),
                        Encoding.UTF8,
                        "application/json");

                    var response = await client.PostAsync(loginUrl, content);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var loginResponse = JsonSerializer.Deserialize<LoginResponse>(responseContent, _jsonOptions);
                        if (loginResponse != null)
                        {
                            server.AuthToken = loginResponse.Token;
                            server.TokenExpiresAt = loginResponse.ExpiresAt;
                            server.LoggedInUser = loginResponse.User;
                            server.LastLoginAt = DateTime.UtcNow;
                            server.LastUpdatedAt = DateTime.UtcNow;
                            
                            await SaveServerAsync(server);

                            return new RemoteServerLoginResponse
                            {
                                Success = true,
                                Token = loginResponse.Token,
                                ExpiresAt = loginResponse.ExpiresAt,
                                User = loginResponse.User
                            };
                        }
                    }

                    return new RemoteServerLoginResponse 
                    { 
                        Success = false, 
                        Message = $"登录失败: {response.StatusCode} - {responseContent}" 
                    };
                }
            }
            catch (Exception ex)
            {
                return new RemoteServerLoginResponse 
                { 
                    Success = false, 
                    Message = $"登录异常: {ex.Message}" 
                };
            }
        }

        /// <summary>
        /// 使用OAuth登录到远程服务器
        /// </summary>
        public async Task<RemoteServerLoginResponse> LoginWithOAuthAsync(string serverId, string provider)
        {
            var server = GetServerById(serverId);
            if (server == null)
                return new RemoteServerLoginResponse { Success = false, Message = "服务器不存在" };

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    var oauthUrl = $"{server.Url.TrimEnd('/')}/api/oauth/login/{provider.ToLower()}";

                    var response = await client.GetAsync(oauthUrl);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var loginResponse = JsonSerializer.Deserialize<OAuthLoginResponse>(responseContent, _jsonOptions);
                        if (loginResponse != null)
                        {
                            server.AuthToken = loginResponse.Token;
                            server.TokenExpiresAt = loginResponse.ExpiresAt;
                            server.LoggedInUser = loginResponse.User;
                            server.LastLoginAt = DateTime.UtcNow;
                            server.LastUpdatedAt = DateTime.UtcNow;
                            
                            await SaveServerAsync(server);

                            return new RemoteServerLoginResponse
                            {
                                Success = true,
                                Token = loginResponse.Token,
                                ExpiresAt = loginResponse.ExpiresAt,
                                User = loginResponse.User
                            };
                        }
                    }

                    return new RemoteServerLoginResponse 
                    { 
                        Success = false, 
                        Message = $"OAuth登录失败: {response.StatusCode}" 
                    };
                }
            }
            catch (Exception ex)
            {
                return new RemoteServerLoginResponse 
                { 
                    Success = false, 
                    Message = $"OAuth登录异常: {ex.Message}" 
                };
            }
        }

        /// <summary>
        /// 从服务器登出
        /// </summary>
        public async Task<bool> LogoutAsync(string serverId)
        {
            var server = GetServerById(serverId);
            if (server == null) return false;

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.AuthToken);
                    
                    var logoutUrl = $"{server.Url.TrimEnd('/')}/api/auth/logout";
                    await client.PostAsync(logoutUrl, null);
                }
            }
            catch (Exception)
            {
                // 忽略登出错误，继续清除本地令牌
            }

            server.AuthToken = string.Empty;
            server.TokenExpiresAt = null;
            server.LoggedInUser = null;
            server.LastUpdatedAt = DateTime.UtcNow;
            
            await SaveServerAsync(server);
            return true;
        }

        /// <summary>
        /// 验证服务器连接
        /// </summary>
        public async Task<bool> VerifyServerConnectionAsync(string url)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var healthCheckUrl = $"{url.TrimEnd('/')}/api/health";
                    var response = await client.GetAsync(healthCheckUrl);
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从文件加载服务器配置
        /// </summary>
        private async Task LoadServersAsync()
        {
            try
            {
                if (File.Exists(_serverConfigPath))
                {
                    var json = await File.ReadAllTextAsync(_serverConfigPath);
                    _servers = JsonSerializer.Deserialize<List<RemoteServer>>(json, _jsonOptions) ?? new();
                }
            }
            catch (Exception)
            {
                _servers = new();
            }
        }

        /// <summary>
        /// 保存服务器配置到文件
        /// </summary>
        private async Task SaveServersToFileAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_servers, _jsonOptions);
                await File.WriteAllTextAsync(_serverConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存服务器配置失败: {ex.Message}");
            }
        }
    }
}
