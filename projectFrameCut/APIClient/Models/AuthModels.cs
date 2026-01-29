using System;
using System.Collections.Generic;

namespace projectFrameCut.APIClient.Models
{
    /// <summary>
    /// 用户数据模型
    /// </summary>
    public class User
    {
        public Guid UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = "User";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public List<ExternalLogin> ExternalLogins { get; set; } = new();
    }

    /// <summary>
    /// 外部登录信息
    /// </summary>
    public class ExternalLogin
    {
        public string Provider { get; set; } = string.Empty;
        public string ProviderKey { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public DateTime LinkedAt { get; set; }
    }

    /// <summary>
    /// 登录请求
    /// </summary>
    public class LoginRequest
    {
        public string UserNameOrEmail { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// 登录响应
    /// </summary>
    public class LoginResponse
    {
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public User User { get; set; } = new();
    }

    /// <summary>
    /// 注册请求
    /// </summary>
    public class RegisterRequest
    {
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// 注册响应
    /// </summary>
    public class RegisterResponse
    {
        public string Message { get; set; } = string.Empty;
        public User User { get; set; } = new();
    }

    /// <summary>
    /// 修改密码请求
    /// </summary>
    public class ChangePasswordRequest
    {
        public string OldPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    /// <summary>
    /// OAuth登录响应
    /// </summary>
    public class OAuthLoginResponse
    {
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public User User { get; set; } = new();
        public bool IsNewUser { get; set; }
    }

    /// <summary>
    /// OAuth状态响应（用于轮询）
    /// </summary>
    public class OAuthStatusResponse
    {
        /// <summary>
        /// OAuth状态: pending(进行中), completed(已完成), failed(失败), expired(过期)
        /// </summary>
        public string Status { get; set; } = "pending";
        
        /// <summary>
        /// 如果状态为completed，此处包含JWT Token
        /// </summary>
        public string? Token { get; set; }
        
        /// <summary>
        /// 是否为新注册用户
        /// </summary>
        public bool IsNewUser { get; set; }
        
        /// <summary>
        /// 如果状态为failed，此处包含错误信息
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
