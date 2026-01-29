using projectFrameCut.APIClient.Models;
using System;
using System.Collections;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.APIClient
{
    /// <summary>
    /// OAuth登录服务，支持Google、GitHub、Microsoft等第三方登录
    /// Windows平台使用浏览器+轮询检测方式
    /// 移动平台使用MAUI WebAuthenticator
    /// </summary>
    public static class OAuthService
    {
        // 使用懒加载单例HttpClient，避免每次创建连接
        private static readonly Lazy<HttpClient> _httpClientLazy = new Lazy<HttpClient>(() =>
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
        });

        private static HttpClient HttpClient => _httpClientLazy.Value;

        /// <summary>
        /// 创建配置好的 HttpClient（已废弃，使用单例HttpClient）
        /// </summary>
        [Obsolete("Use HttpClient property instead")]
        private static HttpClient CreateHttpClient()
        {
            return HttpClient;
        }

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
        /// 使用Google登录
        /// </summary>
        public static async Task<OAuthLoginResponse?> LoginWithGoogleAsync()
        {
            return await LoginWithProviderAsync("google");
        }

        /// <summary>
        /// 使用GitHub登录
        /// </summary>
        public static async Task<OAuthLoginResponse?> LoginWithGitHubAsync()
        {
            return await LoginWithProviderAsync("github");
        }

        /// <summary>
        /// 使用Microsoft登录
        /// </summary>
        public static async Task<OAuthLoginResponse?> LoginWithMicrosoftAsync()
        {
            return await LoginWithProviderAsync("microsoft");
        }

        /// <summary>
        /// 使用指定的OAuth提供商登录
        /// </summary>
        /// <param name="provider">提供商名称（google、github、microsoft）</param>
        private static async Task<OAuthLoginResponse?> LoginWithProviderAsync(string provider)
        {
            try
            {
#if WINDOWS
                // Windows平台使用浏览器+轮询检测
                return await LoginWithProviderPollingAsync(provider);
#else
                // 移动平台使用WebAuthenticator
                return await LoginWithProviderMobileAsync(provider);
#endif
            }
            catch (TaskCanceledException)
            {
                // 用户取消了登录
                return null;
            }
            catch (Exception ex)
            {
                throw new Exception($"{provider} OAuth登录失败: {ex.Message}", ex);
            }
        }

#if !WINDOWS
        /// <summary>
        /// 移动平台OAuth登录实现（使用WebAuthenticator）
        /// </summary>
        private static async Task<OAuthLoginResponse?> LoginWithProviderMobileAsync(string provider)
        {
            // 构建OAuth登录URL
            var loginUri = APIClientBase.GetUri(
                ServiceType.AuthServer,
                $"api/oauth/login/{provider.ToLower()}"
            );

            // 构建回调URL scheme
            var callbackScheme = "projectframecut";

            // 使用MAUI的WebAuthenticator进行OAuth认证
            var authResult = await WebAuthenticator.AuthenticateAsync(
                new Uri(loginUri.ToString()),
                new Uri($"{callbackScheme}://oauth-callback")
            );

            // 从回调URL中提取token
            if (authResult != null && authResult.Properties.TryGetValue("token", out var token))
            {
                var isNewUser = authResult.Properties.TryGetValue("isNewUser", out var isNewUserStr)
                    && bool.TryParse(isNewUserStr, out var isNew) && isNew;

                var expiresAt = DateTime.UtcNow.AddHours(24);
                TokenManager.SaveToken(token, expiresAt);

                var user = await AuthService.GetCurrentUserAsync();

                return new OAuthLoginResponse
                {
                    Token = token,
                    ExpiresAt = expiresAt,
                    User = user ?? new User(),
                    IsNewUser = isNewUser
                };
            }
            else
            {
                throw new Exception("未能从OAuth回调中获取token");
            }
        }
#endif

#if WINDOWS
        /// <summary>
        /// Windows平台OAuth登录实现（使用浏览器+轮询检测）
        /// </summary>
        private static async Task<OAuthLoginResponse?> LoginWithProviderPollingAsync(string provider)
        {
            // 生成唯一的会话ID
            var sessionId = Guid.NewGuid().ToString("N");

            // 构建OAuth登录URL
            var loginUri = APIClientBase.GetUri(
                ServiceType.AuthServer,
                $"api/oauth/login/{provider.ToLower()}",
                $"?sessionId={sessionId}"
            );

            // 在默认浏览器中打开OAuth登录页面
            await Browser.OpenAsync(loginUri, BrowserLaunchMode.SystemPreferred);

            // 开始轮询检测OAuth状态（最多等待5分钟）
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            
            var pollingInterval = TimeSpan.FromSeconds(2); // 每2秒轮询一次
            var jsonOptions = GetJsonOptions();

            User? user = null;
            OAuthStatusResponse? statusResponse = null;

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollingInterval, cts.Token);

                    // 调用API检查OAuth状态 - 使用APIClientBase统一生成URI
                    var statusUri = APIClientBase.GetUri(
                        ServiceType.AuthServer,
                        $"api/oauth/status/{sessionId}"
                    );

                    System.Diagnostics.Debug.WriteLine($"[OAuth] 轮询状态: {statusUri}");

                    HttpResponseMessage response;
                    try
                    {
                        response = await HttpClient.GetAsync(statusUri, cts.Token);
                    }
                    catch (HttpRequestException httpEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[OAuth] HTTP请求失败: {httpEx.Message}");
                        System.Diagnostics.Debug.WriteLine($"[OAuth] 内部异常: {httpEx.InnerException?.Message}");
                        // HTTP错误，等待后继续重试
                        continue;
                    }
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        statusResponse = JsonSerializer.Deserialize<OAuthStatusResponse>(json, jsonOptions);

                        if (statusResponse?.Status == "completed" && !string.IsNullOrEmpty(statusResponse.Token))
                        {
                            var expiresAt = DateTime.UtcNow.AddHours(24);

                            // OAuth完成，保存Token
                            TokenManager.SaveToken(statusResponse.Token, expiresAt);

                            // 获取用户信息

                            cts.Cancel();
                        }
                        else if (statusResponse?.Status == "failed")
                        {
                            throw new Exception(statusResponse.ErrorMessage ?? "OAuth登录失败");
                        }
                        // 如果是 "pending"，继续轮询
                    }
                }
                catch (TaskCanceledException)
                {
                    // 超时或用户取消
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[OAuth] 轮询错误: {ex.Message}");
                    // 继续轮询
                }
                if (statusResponse?.Token is not null)
                {
                    var expiresAt = DateTime.UtcNow.AddHours(24);
                    user = await AuthService.GetCurrentUserAsync();

                    return new OAuthLoginResponse
                    {
                        Token = statusResponse.Token,
                        ExpiresAt = expiresAt,
                        User = user ?? new User(),
                        IsNewUser = statusResponse.IsNewUser
                    };
                }
            }

            // 超时
            throw new TaskCanceledException("OAuth登录超时");
        }
#endif

        /// <summary>
        /// 在浏览器中打开OAuth登录（备用方案）
        /// 适用于WebAuthenticator不可用的情况
        /// </summary>
        /// <param name="provider">提供商名称</param>
        public static async Task<bool> OpenOAuthLoginInBrowserAsync(string provider)
        {
            try
            {
                var loginUri = APIClientBase.GetUri(
                    ServiceType.AuthServer,
                    $"api/oauth/login/{provider.ToLower()}"
                );

                // 在默认浏览器中打开OAuth登录页面
                await Browser.OpenAsync(loginUri, BrowserLaunchMode.SystemPreferred);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开浏览器失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 手动处理从浏览器返回的OAuth token
        /// 当使用OpenOAuthLoginInBrowserAsync时，需要手动调用此方法来处理返回的token
        /// </summary>
        /// <param name="token">从OAuth回调URL获取的token</param>
        /// <param name="isNewUser">是否为新用户</param>
        public static async Task<OAuthLoginResponse?> CompleteOAuthLoginAsync(string token, bool isNewUser = false)
        {
            try
            {
                var expiresAt = DateTime.UtcNow.AddHours(24);

                // 保存Token
                TokenManager.SaveToken(token, expiresAt);

                // 获取用户信息
                var user = await AuthService.GetCurrentUserAsync();

                return new OAuthLoginResponse
                {
                    Token = token,
                    ExpiresAt = expiresAt,
                    User = user ?? new User(),
                    IsNewUser = isNewUser
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"完成OAuth登录失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 关联外部账号
        /// </summary>
        /// <param name="provider">提供商名称</param>
        public static async Task<bool> LinkExternalAccountAsync(string provider)
        {
            var token = TokenManager.CurrentToken;
            if (string.IsNullOrEmpty(token))
            {
                throw new UnauthorizedAccessException("未登录");
            }

            try
            {
                var uri = APIClientBase.GetUri(
                    ServiceType.AuthServer,
                    $"api/oauth/link/{provider.ToLower()}"
                );

                var request = new HttpRequestMessage(HttpMethod.Post, uri);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await HttpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                throw new Exception($"关联{provider}账号失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 取消关联外部账号
        /// </summary>
        /// <param name="provider">提供商名称</param>
        public static async Task<bool> UnlinkExternalAccountAsync(string provider)
        {
            var token = TokenManager.CurrentToken;
            if (string.IsNullOrEmpty(token))
            {
                throw new UnauthorizedAccessException("未登录");
            }

            try
            {
                var uri = APIClientBase.GetUri(
                    ServiceType.AuthServer,
                    $"api/oauth/unlink/{provider.ToLower()}"
                );

                var request = new HttpRequestMessage(HttpMethod.Delete, uri);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await HttpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                throw new Exception($"取消关联{provider}账号失败: {ex.Message}", ex);
            }
        }
    }
}
