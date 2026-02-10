using System;
using System.Threading.Tasks;

namespace projectFrameCut.APIClient
{
    /// <summary>
    /// Token管理器，负责存储和管理JWT Token
    /// </summary>
    public static class TokenManager
    {
        private static string? _currentToken;
        private static DateTime _tokenExpiresAt;
        private const string TokenKey = "AuthToken";
        private const string ExpiresAtKey = "TokenExpiresAt";

        /// <summary>
        /// 获取当前的Token
        /// </summary>
        public static string? CurrentToken
        {
            get
            {
                // 如果内存中有token且未过期，直接返回
                if (!string.IsNullOrEmpty(_currentToken) && _tokenExpiresAt > DateTime.UtcNow)
                {
                    return _currentToken;
                }

                // 尝试从持久化存储中加载
                LoadFromStorage();
                
                // 检查是否过期
                if (!string.IsNullOrEmpty(_currentToken) && _tokenExpiresAt > DateTime.UtcNow)
                {
                    return _currentToken;
                }

                return null;
            }
        }

        /// <summary>
        /// 检查用户是否已登录
        /// </summary>
        public static bool IsLoggedIn => !string.IsNullOrEmpty(CurrentToken);

        /// <summary>
        /// 保存Token
        /// </summary>
        public static void SaveToken(string token, DateTime expiresAt)
        {
            _currentToken = token;
            _tokenExpiresAt = expiresAt;

            // 持久化存储
            try
            {
                Preferences.Set(TokenKey, token);
                Preferences.Set(ExpiresAtKey, expiresAt.ToString("o")); // ISO 8601格式
            }
            catch (Exception ex)
            {
                // 记录错误但不抛出，允许继续使用内存中的token
                System.Diagnostics.Debug.WriteLine($"Failed to save token: {ex.Message}");
            }
        }

        /// <summary>
        /// 清除Token（登出）
        /// </summary>
        public static void ClearToken()
        {
            _currentToken = null;
            _tokenExpiresAt = DateTime.MinValue;

            try
            {
                Preferences.Remove(TokenKey);
                Preferences.Remove(ExpiresAtKey);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear token: {ex.Message}");
            }
        }

        /// <summary>
        /// 从持久化存储加载Token
        /// </summary>
        private static void LoadFromStorage()
        {
            try
            {
                var token = Preferences.Get(TokenKey, null);
                var expiresAtStr = Preferences.Get(ExpiresAtKey, null);

                if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(expiresAtStr))
                {
                    if (DateTime.TryParse(expiresAtStr, out var expiresAt))
                    {
                        _currentToken = token;
                        _tokenExpiresAt = expiresAt;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load token: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查Token是否即将过期（剩余时间少于5分钟）
        /// </summary>
        public static bool IsTokenExpiringSoon()
        {
            if (string.IsNullOrEmpty(_currentToken))
            {
                return true;
            }

            return (_tokenExpiresAt - DateTime.UtcNow).TotalMinutes < 5;
        }
    }
}
