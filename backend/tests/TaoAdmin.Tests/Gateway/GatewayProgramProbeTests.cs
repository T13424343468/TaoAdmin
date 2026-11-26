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
        var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b => b.UseSetting("Monitor:LogDir",
                Path.Combine(Path.GetTempPath(), $"tao-gw1-logs-{Guid.NewGuid():N}")));
        try
        {
            var client = factory.CreateClient();

            var health = await client.GetAsync("/health");
            Assert.True(health.IsSuccessStatusCode);
            var body = await health.Content.ReadAsStringAsync();
            Assert.Contains("UP", body);
            Assert.Contains("taoadmin-gateway", body);

            // 安全加固后口径：资源指标端点要求已认证，匿名访问被拒（防匿名探测服务器资源）
            var metrics = await client.GetAsync("/metrics/resources");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, metrics.StatusCode);

            // 聚合端点：同样要求已认证
            var aggregate = await client.GetAsync("/metrics/aggregate");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, aggregate.StatusCode);
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }
}
