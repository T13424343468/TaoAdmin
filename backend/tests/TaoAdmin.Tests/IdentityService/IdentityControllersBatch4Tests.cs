using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第四批）：审计（验证码/安全设置/日志/清理/导出）、通知渠道、告警规则。
/// 真应用服务 + SQLite 内存库，断言 ApiResult 与落库/留痕实效。
/// </summary>
public class IdentityControllersBatch4Tests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly SnowflakeIdGenerator _snow = new(6, 6);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public IdentityControllersBatch4Tests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysSiteConfig, SysOperationLog, SysNotifyLog, SysAlertRule>();
    }

    public void Dispose() => _db.Dispose();

    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId = 1, string name = "admin")
    {
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name));
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) }
        };
    }

    private (AuditController Controller, LoginSecurityAppService Security) CreateAudit()
    {
        var security = new LoginSecurityAppService(_db, _snow, _cache);
        var controller = new AuditController(
            security,
            new TaoAdmin.Common.Security.CaptchaService(_cache),
            new OperationLogAppService(_db, _cache),
            new AuditCleanupService(_db, NullLogger<AuditCleanupService>.Instance),
            new AuditTrail(_db, _snow, NullLogger<AuditTrail>.Instance),
            new AuditLogExcelAppService(_db));
        AttachHttp(controller);
        return (controller, security);
    }

    [Fact]
    public void Audit_Captcha_ShouldReturnIdAndSvg()
    {
        var (controller, _) = CreateAudit();
        var result = controller.Captcha();
        Assert.True(result.Success);
        var payload = (dynamic)((dynamic)result.Data!);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task Audit_Settings_GetSave_ShouldRoundTrip_AndLeaveTrail()
    {
        var (controller, security) = CreateAudit();

        var got = await controller.GetSettings();
        Assert.True(got.Success);
        Assert.NotNull(got.Data);

        var save = await controller.SaveSettings(new LoginSecuritySettings(3, 10, true, 2, 30, true));
        Assert.True(save.Success);
        Assert.Equal("保存成功", save.Message);

        var reread = await security.GetAsync();
        Assert.Equal(3, reread.LoginMaxFails);
        Assert.Equal(10, reread.LoginLockMinutes);
        Assert.Equal(30, reread.LogRetentionDays);

        // 保存自动留痕（AuditTrail 写 sys_operation_log，Category=audit）
        Assert.Equal(1, _db.Queryable<SysOperationLog>().Count(l => l.Category == "audit"));
    }

    [Fact]
    public async Task Audit_Logs_Cleanup_BatchDelete_Export_ShouldWork()
    {
        var (controller, _) = CreateAudit();
        await _db.Insertable(new SysOperationLog
        {
            Id = _snow.NextId(), Category = "login", UserName = "tao", Success = 1,
            Method = "POST", Path = "/login", CreatedAt = DateTime.Now.AddDays(-100)
        }).ExecuteCommandAsync();
        await _db.Insertable(new SysOperationLog
        {
            Id = _snow.NextId(), Category = "operation", UserName = "lee", Success = 1,
            Method = "GET", Path = "/users", CreatedAt = DateTime.Now
        }).ExecuteCommandAsync();

        var page = await controller.Logs(category: "login");
        Assert.True(page.Success);

        // 保留 30 天：100 天前的旧日志被清
        var cleanup = await controller.Cleanup();
        Assert.True(cleanup.Success);
        Assert.Equal(1, _db.Queryable<SysOperationLog>().Count());

        var batch = await controller.BatchDeleteLogs(new BatchIdsRequest
        { Ids = _db.Queryable<SysOperationLog>().ToList().Select(l => l.Id).ToList() });
        Assert.True(batch.Success);
        Assert.Contains("批量删除完成", batch.Message);
        // 批量删除为物理删除；控制器随后写一条 audit:log/batch-delete 留痕，业务日志应为 0
        Assert.Equal(0, _db.Queryable<SysOperationLog>().Count(l => l.Category != "audit"));

        // 导出空日志也返回 xlsx 文件流
        var export = await controller.ExportLogs();
        var file = Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(export);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.StartsWith("audit-log-", file.FileDownloadName);
    }

    [Fact]
    public async Task NotifyChannel_GetSave_Test_Logs_ShouldRoundTrip()
    {
        var notifier = new AlertNotifier(_db, _snow,
            new TaoAdmin.Application.Services.SmtpEmailSender(_db),
            new TaoAdmin.Application.Services.DingTalkSender(_db, new System.Net.Http.HttpClient()));
        var controller = new NotifyChannelController(new NotifyChannelAppService(_db, _snow), notifier,
            _db, new AuditTrail(_db, _snow, NullLogger<AuditTrail>.Instance));
        AttachHttp(controller);

        var save = await controller.Save(new Dictionary<string, string> { ["notify_alert_receivers"] = "ops@tao.local" });
        Assert.True(save.Success);
        Assert.Equal("保存成功", save.Message);

        var got = await controller.Get();
        Assert.True(got.Success);

        // 渠道接线测试：email 已配接收人但 SMTP 未配置 → 发送失败仍落 sys_notify_log（成功失败均留痕）
        var test = await controller.Test(new NotifyTestRequest { channel = "email" });
        Assert.False(test.Success);
        Assert.Equal(1, _db.Queryable<SysNotifyLog>().Count());
        var notifyRow = _db.Queryable<SysNotifyLog>().Single();
        Assert.False(notifyRow.Success);
        Assert.Contains("SMTP", notifyRow.Error);

        var logs = await controller.Logs();
        Assert.True(logs.Success);
    }

    [Fact]
    public async Task AlertRule_GetSaveDelete_ShouldRoundTrip_AndLeaveTrail()
    {
        var controller = new AlertRuleController(new AlertRuleAppService(_db, _snow),
            new AuditTrail(_db, _snow, NullLogger<AuditTrail>.Instance));
        AttachHttp(controller);

        var save = await controller.Save(new AlertRuleSaveRequest
        { Name = "任务失败告警", EventType = "JOB_FAIL", Threshold = 3, Channels = "email", Enabled = true });
        Assert.True(save.Success);
        Assert.Equal("保存成功", save.Message);

        var list = await controller.Get();
        Assert.True(list.Success);

        var id = _db.Queryable<SysAlertRule>().Single().Id.ToString();
        var del = await controller.Delete(id);
        Assert.True(del.Success);
        Assert.Equal("删除成功", del.Message);
        Assert.Equal(2, _db.Queryable<SysOperationLog>().Count(l => l.Category == "audit")); // 保存+删除各留痕一次
    }
}
