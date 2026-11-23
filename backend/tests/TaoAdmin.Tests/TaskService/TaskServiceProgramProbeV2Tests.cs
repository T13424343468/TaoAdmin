using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quartz;
using Quartz.Impl;
using System.Collections.Specialized;
using TaoAdmin.TaskService;
using Xunit;

namespace TaoAdmin.Tests.TaskService;

/// <summary>
/// TaskService 启动探针 v2：补齐 Program.cs 可选分支实效覆盖——
/// ES 日志 sink、OpenAPI 文档端点、Nacos 开关（补齐 NacosConfig 监听配置后可启动）。
/// </summary>
public class TaskServiceProgramProbeV2Tests
{
    private static (WebApplicationFactory<TaskServiceProbe> Factory, HttpClient Client) Build(
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? extra = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        var factory = new WebApplicationFactory<TaskServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-task2-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-task2-logs-{runId}"));
                b.UseSetting("DocsAccess:Mode", "Open"); // 测试宿主非回环来源，文档放开以探文档端点
                // 独立命名调度器：避免与 v1 探针共用默认 RAM 调度器导致 heartbeatJob 重复注册冲突
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<ISchedulerFactory>();
                    services.AddSingleton<ISchedulerFactory>(new StdSchedulerFactory(
                        new NameValueCollection { ["quartz.scheduler.instanceName"] = $"probe-{runId}" }));
                });
                extra?.Invoke(b);
            });
        return (factory, factory.CreateClient());
    }

    [Fact]
    public async Task Program_ES装配与OpenAPI文档可用()
    {
        var (factory, client) = Build(b => b.UseSetting("Elasticsearch:Uri", "http://localhost:9200"));
        using var holder = factory;

        var openapi = await client.GetAsync("/openapi/v1.json");
        Assert.True(openapi.IsSuccessStatusCode, $"openapi 状态 {(int)openapi.StatusCode}");
        Assert.Contains("TaoAdmin 任务服务 API", await openapi.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Program_Nacos开关打开_装配不崩()
    {
        var (factory, client) = Build(b =>
        {
            b.UseSetting("Nacos:Enabled", "true");
            b.UseSetting("NacosConfig:ServerAddresses:0", "http://localhost:8848");
            b.UseSetting("NacosConfig:Namespace", "test");
            b.UseSetting("NacosConfig:Listeners:0:Group", "DEFAULT_GROUP");
            b.UseSetting("NacosConfig:Listeners:0:DataId", "taoadmin-test");
            b.UseSetting("NacosConfig:Listeners:0:Type", "JSON");
            b.UseSetting("Nacos:ServerAddresses:0", "http://localhost:8848");
            b.UseSetting("Nacos:ServiceName", "taoadmin-task-test");
        });
        using var holder = factory;

        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode, $"Nacos 开启后健康检查 {(int)health.StatusCode}");
    }
}
