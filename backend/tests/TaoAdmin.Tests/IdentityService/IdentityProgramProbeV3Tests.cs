using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaoAdmin.Common.Storage;
using TaoAdmin.IdentityService;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// IdentityService 启动探针 v3：补齐 Program.cs 剩余可选分支——
/// 精简配置宿主（无 Monitor:LogDir / Jwt / Redis 连接串，覆盖三处 ?? 兜底）、
/// MinIO 存储提供口装配分支、业务异常经 /error 兜底端点返回领域码。
/// 精简宿主日志落运行目录 logs/，与回退目录清理测试串行（LogDirFallback 集合）。
/// </summary>
[Collection("LogDirFallback")]
public class IdentityProgramProbeV3Tests
{
    [Fact]
    public async Task Program_精简配置_三处兜底分支可用()
    {
        // 内容根指向空目录：appsettings.json 不加载 → Monitor:LogDir、Jwt 节、Redis 连接串全部缺省，
        // 仅经 UseSetting（宿主配置，优先级高于 appsettings）提供数据库
        var runId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tao-id3-root-{runId}");
        Directory.CreateDirectory(tempRoot);
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            using var factory = new WebApplicationFactory<IdentityServiceProbe>()
                .WithWebHostBuilder(b =>
                {
                    b.UseContentRoot(tempRoot);
                    b.UseSetting("Database:Provider", "SQLite");
                    b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-id3-{runId}.db");
                });
            var client = factory.CreateClient();

            var health = await client.GetAsync("/health");
            Assert.True(health.IsSuccessStatusCode, $"精简配置宿主健康检查 {(int)health.StatusCode}");
        }
        finally
        {
            // 探针日志落运行目录 logs/，测完即清，避免污染回退目录断言
            TaoAdmin.Tests.TaskService.TaskServiceProgramProbeV3Tests.TryDelete(
                Path.Combine(cwd, "logs", "identity.jsonl"));
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void DI工厂_存储提供口按Provider切换MinIO()
    {
        var runId = Guid.NewGuid().ToString("N");
        using var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-id3m-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-id3m-logs-{runId}"));
                b.UseSetting("FileStorage:Provider", "minio");
                b.UseSetting("FileStorage:Minio:Endpoint", "http://minio.internal:9000");
                b.UseSetting("FileStorage:Minio:AccessKey", "ak");
                b.UseSetting("FileStorage:Minio:SecretKey", "sk");
                b.UseSetting("FileStorage:Minio:Bucket", "taoadmin");
            });

        using var scope = factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageProvider>();
        Assert.IsType<MinioFileStorageProvider>(storage); // Provider=minio 的装配分支
    }

    [Fact]
    public async Task 业务异常_经error兜底端点返回领域码()
    {
        var runId = Guid.NewGuid().ToString("N");
        using var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-id3e-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-id3e-logs-{runId}"));
            });
        var client = factory.CreateClient();

        // 未知账号登录抛 DomainException(400)，经 UseExceptionHandler 重执行 /error：
        // 覆盖异常特征非空、领域异常取码、异常消息透出三个分支
        var login = await client.PostAsync("/auth/login",
            new StringContent("""{"username":"ghost","password":"x"}""", Encoding.UTF8, "application/json"));
        Assert.True(login.IsSuccessStatusCode, $"兜底端点状态 {(int)login.StatusCode}");
        var body = await login.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":false", body.Replace(" ", ""));
        Assert.Contains("\"code\":400", body.Replace(" ", "")); // 领域码而非 500
    }
}
