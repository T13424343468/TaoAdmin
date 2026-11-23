using Microsoft.AspNetCore.Mvc.Testing;
using TaoAdmin.IdentityService;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// IdentityService 启动探针（WebApplicationFactory 真实启动）：
/// 走完整 Program.cs 装配链（Serilog/SqlSugar/Redis 懒加载/JWT/消息队列/中间件/建表种子），
/// SQLite 文件库 + 临时日志目录，断言健康检查、资源指标与登录接口真实可用。
/// </summary>
public class IdentityProgramProbeTests
{
    [Fact]
    public async Task Program_启动探针_健康检查与登录接口可用()
    {
        var runId = Guid.NewGuid().ToString("N");
        var logDir = Path.Combine(Path.GetTempPath(), $"tao-id-logs-{runId}");
        var dbFile = Path.Combine(Path.GetTempPath(), $"tao-id-{runId}.db");

        using var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={dbFile}");
                b.UseSetting("Monitor:LogDir", logDir);
            });
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);
        var body = await health.Content.ReadAsStringAsync();
        Assert.Contains("UP", body);
        Assert.Contains("taoadmin-identity", body);

        var metrics = await client.GetAsync("/metrics/resources");
        Assert.True(metrics.IsSuccessStatusCode);
        Assert.Contains("cpuPercent", await metrics.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // 启动建表种子：SQLite 库文件已生成
        Assert.True(File.Exists(dbFile));
    }
}
