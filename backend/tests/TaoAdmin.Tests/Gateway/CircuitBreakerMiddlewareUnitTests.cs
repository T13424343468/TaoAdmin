using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaoAdmin.Common.Resilience;
using Xunit;

namespace TaoAdmin.Tests.Gateway;

/// <summary>
/// 熔断中间件单元测试：直连构造覆盖路径判定的兜底分支——
/// 空 PathString（Path.Value 为 null）走 ?? "" 兜底、非 /api/ 路径直接放行、
/// /api/ 路径进入熔断统计（下游 5xx 计失败直至熔断 503）。
/// </summary>
public class CircuitBreakerMiddlewareUnitTests
{
    private static CircuitBreakerMiddleware Build(Microsoft.AspNetCore.Http.RequestDelegate next, int threshold = 5)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CircuitBreaker:Enabled"] = "true",
            ["CircuitBreaker:FailureThreshold"] = threshold.ToString(),
            ["CircuitBreaker:BreakSeconds"] = "30"
        }).Build();
        return new CircuitBreakerMiddleware(next, config, NullLogger<CircuitBreakerMiddleware>.Instance);
    }

    [Fact]
    public async Task InvokeAsync_空PathString_走空串兜底并放行()
    {
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Path = default(Microsoft.AspNetCore.Http.PathString); // Path.Value 为 null

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled); // ?? "" 兜底后按非 /api/ 路径放行
    }

    [Fact]
    public async Task InvokeAsync_非Api路径_跳过熔断统计直接放行()
    {
        var nextCalled = false;
        var mw = Build(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Path = "/health";

        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(503, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_Api路径_连续失败达到阈值即熔断503()
    {
        var calls = 0;
        var mw = Build(_ => { calls++; throw new IOException("downstream down"); }, threshold: 2);

        // 前两发：进入统计并计失败（异常再抛）
        for (var i = 0; i < 2; i++)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Path = "/api/identity/users";
            await Assert.ThrowsAsync<IOException>(() => mw.InvokeAsync(ctx));
        }
        Assert.Equal(2, calls);

        // 第三发：熔断期内直接 503，不再触达下游
        var blocked = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        blocked.Request.Path = "/api/identity/users";
        await mw.InvokeAsync(blocked);
        Assert.Equal(2, calls); // 下游未被再次调用
        Assert.Equal(503, blocked.Response.StatusCode);
    }
}
