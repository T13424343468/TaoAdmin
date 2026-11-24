using Microsoft.AspNetCore.Mvc.Testing;
using TaoAdmin.TaskService;
using Xunit;

namespace TaoAdmin.Tests.TaskService;

/// <summary>
/// TaskService 启动探针（WebApplicationFactory 真实启动）：
/// 走完整 Program.cs 装配链（Serilog/SqlSugar/Quartz/JWT/中间件/建表种子），
/// SQLite 文件库 + 临时日志目录，断言健康检查与资源指标端点真实可用。
/// </summary>
public class TaskServiceProgramProbeTests
{
    [Fact]
    public async Task Program_启动探针_健康检查与资源指标可用()
    {
        var runId = Guid.NewGuid().ToString("N");
        var logDir = Path.Combine(Path.GetTempPath(), $"tao-task-logs-{runId}");
        var dbFile = Path.Combine(Path.GetTempPath(), $"tao-task-{runId}.db");

        using var factory = new WebApplicationFactory<TaskServiceProbe>()
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
        Assert.Contains("taoadmin-task", body);

        var metrics = await client.GetAsync("/metrics/resources");
        Assert.True(metrics.IsSuccessStatusCode);
        var metricsBody = await metrics.Content.ReadAsStringAsync();
        Assert.Contains("cpuPercent", metricsBody, StringComparison.OrdinalIgnoreCase);

        // 启动种子：示例心跳任务已落库
        Assert.True(File.Exists(dbFile));
    }
}
