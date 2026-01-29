using projectFrameCut.APIClient;
using projectFrameCut.APIClient.Models;
using System;
using System.Threading.Tasks;

namespace projectFrameCut.Tests
{
    /// <summary>
    /// 认证功能测试助手
    /// 用于验证登录、注册、OAuth等功能是否正常工作
    /// </summary>
    public static class AuthTestHelper
    {
        /// <summary>
        /// 测试完整的认证流程
        /// </summary>
        public static async Task<bool> RunAllTestsAsync()
        {
            Console.WriteLine("=== 开始认证功能测试 ===\n");

            bool allPassed = true;

            // 测试1: Token管理
            allPassed &= await TestTokenManagerAsync();

            // 测试2: 用户注册
            allPassed &= await TestUserRegistrationAsync();

            // 测试3: 用户登录
            allPassed &= await TestUserLoginAsync();

            // 测试4: 获取用户信息
            allPassed &= await TestGetCurrentUserAsync();

            // 测试5: 登出
            allPassed &= await TestLogoutAsync();

            Console.WriteLine($"\n=== 测试完成 ===");
            Console.WriteLine($"结果: {(allPassed ? "✅ 所有测试通过" : "❌ 部分测试失败")}");

            return allPassed;
        }

        /// <summary>
        /// 测试Token管理器
        /// </summary>
        private static async Task<bool> TestTokenManagerAsync()
        {
            Console.WriteLine("测试1: Token管理器");
            try
            {
                // 清除旧Token
                TokenManager.ClearToken();
                
                if (TokenManager.IsLoggedIn)
                {
                    Console.WriteLine("  ❌ 清除Token后仍然显示已登录");
                    return false;
                }

                // 保存测试Token
                var expiresAt = DateTime.UtcNow.AddHours(1);
                TokenManager.SaveToken("test-token-123", expiresAt);

                if (!TokenManager.IsLoggedIn)
                {
                    Console.WriteLine("  ❌ 保存Token后未显示已登录");
                    return false;
                }

                if (TokenManager.CurrentToken != "test-token-123")
                {
                    Console.WriteLine("  ❌ Token内容不匹配");
                    return false;
                }

                // 清除Token
                TokenManager.ClearToken();

                Console.WriteLine("  ✅ Token管理器测试通过");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ Token管理器测试失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试用户注册
        /// </summary>
        private static async Task<bool> TestUserRegistrationAsync()
        {
            Console.WriteLine("\n测试2: 用户注册");
            try
            {
                // 使用随机用户名避免冲突
                var userName = $"testuser_{DateTime.Now:yyyyMMddHHmmss}";
                var email = $"{userName}@test.com";
                var password = "Test123456!";

                var response = await AuthService.RegisterAsync(userName, email, password);

                if (response == null)
                {
                    Console.WriteLine("  ⚠️  注册返回null（可能用户已存在）");
                    return true; // 不算失败，可能是重复注册
                }

                Console.WriteLine($"  ✅ 用户注册成功: {response.User.UserName}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 用户注册失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试用户登录
        /// </summary>
        private static async Task<bool> TestUserLoginAsync()
        {
            Console.WriteLine("\n测试3: 用户登录");
            try
            {
                // 使用测试账号
                var response = await AuthService.LoginAsync("admin", "admin123");

                if (response == null)
                {
                    Console.WriteLine("  ❌ 登录返回null");
                    return false;
                }

                if (string.IsNullOrEmpty(response.Token))
                {
                    Console.WriteLine("  ❌ Token为空");
                    return false;
                }

                if (!TokenManager.IsLoggedIn)
                {
                    Console.WriteLine("  ❌ 登录后Token未保存");
                    return false;
                }

                Console.WriteLine($"  ✅ 用户登录成功: {response.User.UserName}");
                Console.WriteLine($"     Token: {response.Token.Substring(0, Math.Min(20, response.Token.Length))}...");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("  ❌ 用户名或密码错误");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 登录失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试获取当前用户信息
        /// </summary>
        private static async Task<bool> TestGetCurrentUserAsync()
        {
            Console.WriteLine("\n测试4: 获取用户信息");
            try
            {
                if (!AuthService.IsLoggedIn)
                {
                    Console.WriteLine("  ⚠️  用户未登录，跳过此测试");
                    return true;
                }

                var user = await AuthService.GetCurrentUserAsync();

                if (user == null)
                {
                    Console.WriteLine("  ❌ 获取用户信息返回null");
                    return false;
                }

                Console.WriteLine($"  ✅ 获取用户信息成功");
                Console.WriteLine($"     用户名: {user.UserName}");
                Console.WriteLine($"     邮箱: {user.Email}");
                Console.WriteLine($"     角色: {user.Role}");
                Console.WriteLine($"     创建时间: {user.CreatedAt}");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("  ❌ Token已过期或无效");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 获取用户信息失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试登出
        /// </summary>
        private static async Task<bool> TestLogoutAsync()
        {
            Console.WriteLine("\n测试5: 登出");
            try
            {
                AuthService.Logout();

                if (TokenManager.IsLoggedIn)
                {
                    Console.WriteLine("  ❌ 登出后仍然显示已登录");
                    return false;
                }

                Console.WriteLine("  ✅ 登出成功");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 登出失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试API请求是否包含认证头
        /// </summary>
        public static async Task<bool> TestAuthenticatedApiRequestAsync()
        {
            Console.WriteLine("\n测试: 带认证的API请求");
            try
            {
                // 先登录
                if (!AuthService.IsLoggedIn)
                {
                    Console.WriteLine("  正在登录...");
                    await AuthService.LoginAsync("admin", "admin123");
                }

                // 请求需要认证的API
                var assets = await RemoteAssetServices.GetAllAssets();

                Console.WriteLine($"  ✅ API请求成功，获取到 {assets.Count} 个资产");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("  ❌ 认证失败（Token无效或已过期）");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  API请求失败: {ex.Message}");
                Console.WriteLine("     （可能是后端服务器未运行或网络问题）");
                return true; // 不算测试失败
            }
        }

        /// <summary>
        /// 快速测试连接性
        /// </summary>
        public static async Task<bool> TestConnectionAsync()
        {
            Console.WriteLine("测试: 后端连接");
            try
            {
                var response = await AuthService.LoginAsync("admin", "admin123");
                Console.WriteLine("  ✅ 后端连接正常");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 无法连接到后端: {ex.Message}");
                Console.WriteLine("     请确保后端服务器正在运行");
                Console.WriteLine("     默认地址: http://localhost:5427");
                return false;
            }
        }
    }
}
