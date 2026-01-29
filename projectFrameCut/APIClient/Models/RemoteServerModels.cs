using System;
using System.Collections.Generic;

namespace projectFrameCut.APIClient.Models
{
    /// <summary>
    /// 远程服务器配置模型
    /// </summary>
    public class RemoteServer
    {
        /// <summary>
        /// 服务器唯一标识
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 服务器名称
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 服务器地址（URL）
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// 服务器描述
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 认证令牌
        /// </summary>
        public string AuthToken { get; set; } = string.Empty;

        /// <summary>
        /// 令牌过期时间
        /// </summary>
        public DateTime? TokenExpiresAt { get; set; }

        /// <summary>
        /// 登录用户信息
        /// </summary>
        public User? LoggedInUser { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 最后登录时间
        /// </summary>
        public DateTime? LastLoginAt { get; set; }

        /// <summary>
        /// 是否已登录
        /// </summary>
        public bool IsLoggedIn => !string.IsNullOrEmpty(AuthToken) && LoggedInUser != null;

        /// <summary>
        /// 令牌是否已过期
        /// </summary>
        public bool IsTokenExpired => TokenExpiresAt.HasValue && DateTime.UtcNow > TokenExpiresAt.Value;

        /// <summary>
        /// 克隆对象
        /// </summary>
        public RemoteServer Clone()
        {
            return new RemoteServer
            {
                Id = this.Id,
                Name = this.Name,
                Url = this.Url,
                Description = this.Description,
                IsEnabled = this.IsEnabled,
                AuthToken = this.AuthToken,
                TokenExpiresAt = this.TokenExpiresAt,
                LoggedInUser = this.LoggedInUser,
                CreatedAt = this.CreatedAt,
                LastUpdatedAt = this.LastUpdatedAt,
                LastLoginAt = this.LastLoginAt
            };
        }
    }

    /// <summary>
    /// 远程服务器登录请求
    /// </summary>
    public class RemoteServerLoginRequest
    {
        /// <summary>
        /// 服务器ID
        /// </summary>
        public string ServerId { get; set; } = string.Empty;

        /// <summary>
        /// 用户名或邮箱
        /// </summary>
        public string UserNameOrEmail { get; set; } = string.Empty;

        /// <summary>
        /// 密码
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// OAuth提供商（可选）
        /// </summary>
        public string? OAuthProvider { get; set; }
    }

    /// <summary>
    /// 远程服务器登录响应
    /// </summary>
    public class RemoteServerLoginResponse
    {
        /// <summary>
        /// 是否登录成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 认证令牌
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// 令牌过期时间
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// 登录用户信息
        /// </summary>
        public User? User { get; set; }
    }
}
