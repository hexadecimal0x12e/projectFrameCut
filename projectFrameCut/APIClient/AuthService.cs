using projectFrameCut.APIClient.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.APIClient
{
    /// <summary>
    /// 认证服务，负责用户登录、注册等操作
    /// </summary>
    public static class AuthService
    {
        // 使用懒加载单例HttpClient
        private static readonly Lazy<HttpClient> _httpClientLazy = new Lazy<HttpClient>(() =>
        {
#if DEBUG
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            };
            var client = new HttpClient(handler);
#else
            var client = new HttpClient();
#endif
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        });

        private static HttpClient HttpClient => _httpClientLazy.Value;

        /// <summary>
        /// 配置JSON序列化选项
        /// </summary>
        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        /// <summary>
        /// 用户登录
        /// </summary>
        /// <param name="userNameOrEmail">用户名或邮箱</param>
        /// <param name="password">密码</param>
        /// <returns>登录响应，包含Token和用户信息</returns>
        public static async Task<LoginResponse?> LoginAsync(string userNameOrEmail, string password)
        {
            var uri = APIClientBase.GetUri(ServiceType.ApiServer, "api/auth/login");

            var request = new LoginRequest
            {
                UserNameOrEmail = userNameOrEmail,
                Password = password
            };

            var json = JsonSerializer.Serialize(request, GetJsonOptions());
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await HttpClient.PostAsync(uri, content);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    var loginResponse = JsonSerializer.Deserialize<LoginResponse>(responseJson, GetJsonOptions());

                    if (loginResponse != null)
                    {
                        // 保存Token
                        TokenManager.SaveToken(loginResponse.Token, loginResponse.ExpiresAt);
                    }

                    return loginResponse;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new UnauthorizedAccessException("用户名/邮箱或密码错误");
                }
                else
                {
                    var errorMessage = await response.Content.ReadAsStringAsync();
                    throw new Exception($"登录失败: {response.StatusCode} - {errorMessage}");
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"网络请求失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 用户注册
        /// </summary>
        /// <param name="userName">用户名</param>
        /// <param name="email">邮箱</param>
        /// <param name="password">密码</param>
        /// <returns>注册响应</returns>
        public static async Task<RegisterResponse?> RegisterAsync(string userName, string email, string password)
        {
            var uri = APIClientBase.GetUri(ServiceType.ApiServer, "api/auth/register");

            var request = new RegisterRequest
            {
                UserName = userName,
                Email = email,
                Password = password
            };

            var json = JsonSerializer.Serialize(request, GetJsonOptions());
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await HttpClient.PostAsync(uri, content);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<RegisterResponse>(responseJson, GetJsonOptions());
                }
                else
                {
                    var errorMessage = await response.Content.ReadAsStringAsync();
                    throw new Exception($"注册失败: {response.StatusCode} - {errorMessage}");
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"网络请求失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 获取当前登录用户信息
        /// </summary>
        /// <returns>用户信息</returns>
        public static async Task<User?> GetCurrentUserAsync()
        {
            var token = TokenManager.CurrentToken;
            if (string.IsNullOrEmpty(token))
            {
                throw new UnauthorizedAccessException("未登录");
            }

            var uri = APIClientBase.GetUri(ServiceType.ApiServer, "api/auth/me");
            
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            try
            {
                var response = await HttpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<User>(responseJson, GetJsonOptions());
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // Token可能已过期，清除本地Token
                    TokenManager.ClearToken();
                    throw new UnauthorizedAccessException("认证已过期，请重新登录");
                }
                else
                {
                    var errorMessage = await response.Content.ReadAsStringAsync();
                    Log($"获取用户信息失败: {response.StatusCode} - {errorMessage}");
                    return null;
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"网络请求失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 修改密码
        /// </summary>
        /// <param name="oldPassword">旧密码</param>
        /// <param name="newPassword">新密码</param>
        /// <returns>是否成功</returns>
        public static async Task<bool> ChangePasswordAsync(string oldPassword, string newPassword)
        {
            var token = TokenManager.CurrentToken;
            if (string.IsNullOrEmpty(token))
            {
                throw new UnauthorizedAccessException("未登录");
            }

            var uri = APIClientBase.GetUri(ServiceType.ApiServer, "api/auth/change-password");

            var request = new ChangePasswordRequest
            {
                OldPassword = oldPassword,
                NewPassword = newPassword
            };

            var json = JsonSerializer.Serialize(request, GetJsonOptions());
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            httpRequest.Content = content;

            try
            {
                var response = await HttpClient.SendAsync(httpRequest);
                
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    TokenManager.ClearToken();
                    throw new UnauthorizedAccessException("认证已过期，请重新登录");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    throw new Exception("旧密码不正确");
                }
                else
                {
                    var errorMessage = await response.Content.ReadAsStringAsync();
                    throw new Exception($"修改密码失败: {response.StatusCode} - {errorMessage}");
                }
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"网络请求失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 登出
        /// </summary>
        public static void Logout()
        {
            TokenManager.ClearToken();
        }

        /// <summary>
        /// 检查是否已登录
        /// </summary>
        public static bool IsLoggedIn => TokenManager.IsLoggedIn;
    }
}
