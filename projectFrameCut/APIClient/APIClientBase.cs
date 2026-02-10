using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.APIClient
{
    /// <summary>
    /// 服务类型枚举
    /// </summary>
    public enum ServiceType
    {
        
        /// <summary>
        /// API 服务器
        /// </summary>
        ApiServer,


        AuthServer,
        
        /// <summary>
        /// 文件服务器
        /// </summary>
        FileServer
    }

    public static class APIClientBase
    {
#if DEBUG
        public static string Stage = "127";

        public static string APIBaseUrl = "0.0.1";

#else
        public static string Stage = "develop";

        public static string APIBaseUrl = "example.com";
#endif

        public static int APIVersion = 1;

        /// <summary>
        /// 获取协议方案（DEBUG 模式使用 HTTP，生产环境使用 HTTPS）
        /// </summary>
        public static string GetScheme()
        {
#if DEBUG
            return "http";
#else
            return "https";
#endif
        }

        /// <summary>
        /// 获取服务的子域名前缀
        /// </summary>
        /// <param name="serviceType">服务类型</param>
        /// <returns>子域名前缀</returns>
        private static string GetServiceSubdomain(ServiceType serviceType)
        {
#if DEBUG
            return serviceType switch
            {
                ServiceType.ApiServer => "apiservice-projectframecut_apiserver.dev.localhost",
                ServiceType.FileServer => "fileprovider-projectframecut_apiserver.dev.localhost",
                ServiceType.AuthServer => "authservice-projectframecut_apiserver.dev.localhost",
                _ => throw new ArgumentException($"Unknown service type: {serviceType}")
            };
#else
            return serviceType switch
            {
                ServiceType.ApiServer => "api",
                ServiceType.FileServer => "file",
                _ => throw new ArgumentException($"Unknown service type: {serviceType}")
            };
#endif
        }
        private static int GetServicePort(ServiceType serviceType)
        {
#if !DEBUG
            return 443;
#endif
            return serviceType switch
            {
                ServiceType.ApiServer => 7576,
                ServiceType.FileServer => 7146,
                ServiceType.AuthServer => 7230,
                _ => throw new ArgumentException($"Unknown service type: {serviceType}")
            };
        }

        /// <summary>
        /// 根据服务类型获取 URI
        /// </summary>
        /// <param name="serviceType">服务类型</param>
        /// <param name="relativePath">相对路径</param>
        /// <param name="query">查询字符串</param>
        /// <returns>完整的 URI</returns>
        public static Uri GetUri(ServiceType serviceType, string relativePath, string query = "")
        {
            var subdomain = GetServiceSubdomain(serviceType);
            
            var builder = new UriBuilder
            {
                Scheme = "https",
#if DEBUG
                Host = subdomain,
                Port = GetServicePort(serviceType),
#else
                Host = $"{subdomain}.{Stage}.{APIBaseUrl}",
                Port = -1, // 使用默认端口（HTTP: 80, HTTPS: 443）
#endif
                Path = relativePath,
                Query = query
            };
            return builder.Uri;
        }


    }
}
