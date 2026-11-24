using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Storage;
using TaoAdmin.IdentityService;
using TaoAdmin.IdentityService.Services;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// IdentityService 启动探针 v2：补齐 Program.cs 可选分支实效覆盖——
/// ES 日志 sink、OpenAPI 文档端点、/error 兜底端点（无异常特征走默认 500 分支）、
/// 三个 DI 工厂（服务下线告警评估器/文件存储提供口/行政区划数据源）真实解析。
/// </summary>
public class IdentityProgramProbeV2Tests
{
    private static (WebApplicationFactory<IdentityServiceProbe> Factory, HttpClient Client) Build(
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? extra = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-id2-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-id2-logs-{runId}"));
                b.UseSetting("DocsAccess:Mode", "Open"); // 测试宿主非回环来源，文档放开以探文档端点
                extra?.Invoke(b);
            });
        return (factory, factory.CreateClient());
    }

    [Fact]
    public async Task Program_ES装配_OpenAPI文档与error兜底端点可用()
    {
        var (factory, client) = Build(b => b.UseSetting("Elasticsearch:Uri", "http://localhost:9200"));
        try
        {
            var openapi = await client.GetAsync("/openapi/v1.json");
            Assert.True(openapi.IsSuccessStatusCode, $"openapi 状态 {(int)openapi.StatusCode}");
            Assert.Contains("TaoAdmin 权限服务 API", await openapi.Content.ReadAsStringAsync());

            // /error 直达：无异常特征 → 默认 500 分支，HTTP 200 包业务失败体
            var error = await client.GetAsync("/error");
            Assert.True(error.IsSuccessStatusCode);
            var body = await error.Content.ReadAsStringAsync();
            Assert.Contains("\"success\":false", body.ToLowerInvariant());
            Assert.Contains("服务器内部错误", body);
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }

    [Fact]
    public void DI工厂_告警评估器_文件存储_区划数据源均可解析()
    {
        var (factory, client) = Build();
        try
        {
            _ = client;

            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;

            var evaluator = sp.GetRequiredService<ServiceDownAlertEvaluator>();
            Assert.NotNull(evaluator);

            var storage = sp.GetRequiredService<IFileStorageProvider>();
            Assert.NotNull(storage); // 默认 Provider 非 minio → 本地盘实现

            var region = sp.GetRequiredService<McaRegionDataSource>();
            Assert.NotNull(region);
            Assert.IsType<McaRegionDataSource>(sp.GetRequiredService<IRegionDataSource>());
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
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
            b.UseSetting("Nacos:ServiceName", "taoadmin-identity-test");
        });
        try
        {
            var health = await client.GetAsync("/health");
            Assert.True(health.IsSuccessStatusCode, $"Nacos 开启后健康检查 {(int)health.StatusCode}");
        }
        finally
        {
            // Nacos 客户端断连可能阻塞宿主停机：线程池释放+超时兜底，防同步上下文饿死
            ProbeDispose.SafeDispose(factory);
        }
    }
}
