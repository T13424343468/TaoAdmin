using System.Net;

namespace TaoAdmin.GatewayService;

/// <summary>
/// 限流分桶键口径：按客户端 IP 分桶（限流中间件位于认证之前，UserId 分桶在当前管道不可达）。
/// 抽成独立方法以便对「无远端地址落 unknown 桶」的兜底分支做实效断言。
/// </summary>
internal static class GatewayRateLimitKey
{
    internal static string From(IPAddress? remoteIp)
        => $"ip:{remoteIp?.ToString() ?? "unknown"}";
}
