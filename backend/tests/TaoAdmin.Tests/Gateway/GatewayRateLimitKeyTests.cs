using System.Net;
using TaoAdmin.GatewayService;
using Xunit;

namespace TaoAdmin.Tests.Gateway;

/// <summary>
/// 限流分桶键实效断言：覆盖 GatewayRateLimitKey.From 的两个分支——
/// 有远端地址按 IP 分桶；无远端地址（如内存管道测试宿主）落 unknown 兜底桶。
/// </summary>
public class GatewayRateLimitKeyTests
{
    [Fact]
    public void From_有远端地址_按IP分桶()
    {
        var key = GatewayRateLimitKey.From(IPAddress.Parse("1.2.3.4"));
        Assert.Equal("ip:1.2.3.4", key);
    }

    [Fact]
    public void From_无远端地址_落unknown兜底桶()
    {
        var key = GatewayRateLimitKey.From(null);
        Assert.Equal("ip:unknown", key);
    }
}
