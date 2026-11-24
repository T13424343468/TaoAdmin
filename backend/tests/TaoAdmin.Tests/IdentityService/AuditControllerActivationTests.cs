using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaoAdmin.IdentityService;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 真机 v26 回归：AuditController 构造依赖 AuditLogExcelAppService 未注册，
/// 验证码/审计接口激活即 500，登录页验证码元素静默消失。
/// 回归口径：探针宿主真实 DI 激活 AuditController（匿名 /audit/captcha 即走该控制器构造），
/// 断言 200 且返回 captchaId/svg——控制器六项依赖缺一即红。
/// </summary>
public class AuditControllerActivationTests
{
    [Fact]
    public async Task 验证码接口_匿名可取_控制器依赖齐备()
    {
        var runId = Guid.NewGuid().ToString("N");
        var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-audit-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-audit-logs-{runId}"));
            });
        try
        {
            var client = factory.CreateClient();
            var resp = await client.GetAsync("/audit/captcha");
            Assert.True(resp.IsSuccessStatusCode, $"验证码接口 {(int)resp.StatusCode}（控制器激活失败即 DI 缺注册）");

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            // 业务码走字符串序列化（真机档口径）：code=="200"
            Assert.Equal("200", body.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("data").GetProperty("captchaId").GetString()));
            Assert.False(string.IsNullOrEmpty(body.GetProperty("data").GetProperty("svg").GetString()));
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }

    [Fact]
    public void DI容器_AuditController可激活_六项依赖全齐()
    {
        var runId = Guid.NewGuid().ToString("N");
        var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-auditdi-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-auditdi-logs-{runId}"));
            });
        try
        {
            using var scope = factory.Services.CreateScope();
            var controller = ActivatorUtilities.CreateInstance<TaoAdmin.IdentityService.Controllers.AuditController>(
                scope.ServiceProvider);
            Assert.NotNull(controller);
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }
}
