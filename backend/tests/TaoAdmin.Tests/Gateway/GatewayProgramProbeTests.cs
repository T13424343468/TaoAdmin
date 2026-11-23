using Microsoft.AspNetCore.Mvc.Testing;
using TaoAdmin.GatewayService;
using Xunit;

namespace TaoAdmin.Tests.Gateway;

/// <summary>
/// GatewayService 启动探针（WebApplicationFactory 真实启动）：
/// 走完整 Program.cs 装配链（Serilog/YARP/JWT/限流/熔断/文档中间件），
/// 断言健康检查、本进程资源指标与全服务聚合端点真实可用。
/// </summary>
public class GatewayProgramProbeTests
{
    [Fact]
    public async Task Program_启动探针_健康检查与资源指标可用()
    {
        using var factory = new WebApplicationFactory<GatewayResourceCollector>();
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);
        var body = await health.Content.ReadAsStringAsync();
        Assert.Contains("UP", body);
        Assert.Contains("taoadmin-gateway", body);

        var metrics = await client.GetAsync("/metrics/resources");
        Assert.True(metrics.IsSuccessStatusCode);
        Assert.Contains("cpuPercent", await metrics.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // 聚合端点：下游不可达时逐服务降级，不阻塞本端点
        var aggregate = await client.GetAsync("/metrics/aggregate");
        Assert.True(aggregate.IsSuccessStatusCode);
    }
}
