using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第六批）：租户删除、消息批量删除/演示发布、
/// 区划子级/搜索/详情、监控中心与资源监控全端点、审计通知记录。
/// 真应用服务 + SQLite 内存库，断言 ApiResult 实效。
/// </summary>
public class IdentityControllersBatch6Tests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly SnowflakeIdGenerator _snow = new(8, 8);

    public IdentityControllersBatch6Tests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysTenant, SysMessage, SysRegion, SysSiteConfig, SysNotifyLog>();
        _db.CodeFirst.InitTables<SysOperationLog>();
    }

    public void Dispose() => _db.Dispose();

    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId = 1,
        string name = "admin", string[]? roles = null, string? nickname = null)
    {
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name));
        if (nickname != null)
            identity.AddClaim(new System.Security.Claims.Claim("NickName", nickname));
        foreach (var r in roles ?? new[] { "admin" })
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r));
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) }
        };
    }

    // ---------- 租户删除 ----------

    [Fact]
    public async Task Tenant_Delete_软删后列表不可见()
    {
        _db.Insertable(new SysTenant
        { Id = 21, TenantName = "道映科技", TenantCode = "taoying", Status = 1 }).ExecuteCommand();
        var controller = new TenantController(new TenantAppService(_db, _snow));
        AttachHttp(controller);

        var del = await controller.Delete("21");
        Assert.True(del.Success);
        Assert.Equal("删除成功", del.Message);
        Assert.Empty(_db.Queryable<SysTenant>().Where(t => t.IsDeleted == false).ToList());
    }

    // ---------- 站内消息：批量删除与演示发布 ----------

    [Fact]
    public async Task Message_BatchDelete_软删返回实际数()
    {
        _db.Insertable(new SysMessage { Id = 31, Title = "一", ReceiverId = 1 }).ExecuteCommand();
        _db.Insertable(new SysMessage { Id = 32, Title = "二", ReceiverId = 1 }).ExecuteCommand();
        _db.Insertable(new SysMessage { Id = 33, Title = "三", ReceiverId = 1 }).ExecuteCommand();
        var controller = new MessageController(new MessageAppService(_db, new FakeMessageQueue()));
        AttachHttp(controller);

        var del = await controller.BatchDelete(new BatchIdsRequest { Ids = new List<long> { 31, 33, 999 } });
        Assert.True(del.Success);
        Assert.Contains("批量删除完成", del.Message);
        Assert.Equal(2, _db.Queryable<SysMessage>().Count(m => m.IsDeleted));
        Assert.Equal(1, _db.Queryable<SysMessage>().Count(m => !m.IsDeleted));
    }

    [Fact]
    public async Task Message_PublishDemo_空主题回退默认并发布()
    {
        var mq = new FakeMessageQueue();
        var controller = new MessageController(new MessageAppService(_db, mq));
        AttachHttp(controller);

        var got = await controller.PublishDemo(new Dictionary<string, string> { ["content"] = "hello" });
        Assert.True(got.Success);
        Assert.Contains("消息已发布", got.Message);
        Assert.Single(mq.Published);
        Assert.Equal("demo.test", mq.Published[0].topic); // 空主题回退默认
    }

    private sealed class FakeMessageQueue : TaoAdmin.Common.Messaging.IMessageQueue
    {
        public List<(string topic, string payload)> Published { get; } = new();
        public string ProviderName => "fake";
        public Task PublishAsync(string topic, string payload)
        {
            Published.Add((topic, payload));
            return Task.CompletedTask;
        }
        public Task SubscribeAsync(string topic, Func<TaoAdmin.Common.Messaging.MessageEnvelope, Task> onMessage, CancellationToken ct)
            => Task.CompletedTask;
    }

    // ---------- 区划：子级 / 搜索 / 详情 ----------

    [Fact]
    public async Task Region_Children_Search_GetByCode_实效()
    {
        _db.Insertable(new SysRegion
        { Id = 61, Code = "110000", Name = "北京市", ParentCode = "", Level = 1, FullPath = "北京市" }).ExecuteCommand();
        _db.Insertable(new SysRegion
        { Id = 62, Code = "110101", Name = "东城区", ParentCode = "110100", Level = 3, FullPath = "北京市/东城区" }).ExecuteCommand();
        var controller = new RegionController(new RegionAppService(_db), new EmptyUpdate(_db));
        AttachHttp(controller);

        var children = await controller.Children();
        Assert.True(children.Success);
        Assert.Single(children.Data!);
        Assert.Equal("北京市", children.Data![0].name);

        var search = await controller.Search("东城");
        Assert.True(search.Success);
        Assert.Single(search.Data!);
        Assert.Equal("110101", search.Data![0].code);

        var empty = await controller.Search("  ");
        Assert.True(empty.Success);
        Assert.Empty(empty.Data!);

        var detail = await controller.GetByCode("110101");
        Assert.True(detail.Success);
        Assert.Equal("东城区", detail.Data!.name);

        var missing = await controller.GetByCode("999999");
        Assert.True(missing.Success);
        Assert.Null(missing.Data);
    }

    private sealed class EmptyUpdate(SqlSugarClient db) : RegionUpdateService(
        db, new EmptyDataSource(), new EmptyPackageProvider(),
        new RegionWriteService(db), new SnowflakeIdGenerator(2, 2),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RegionUpdateService>.Instance)
    { }

    private sealed class EmptyPackageProvider : IRegionPackageProvider
    {
        public RegionSnapshot GetBuiltinSnapshot() => new();
    }

    private sealed class EmptyDataSource : IRegionDataSource
    {
        public Task<RegionSnapshot> FetchLatestAsync(Action<RegionFetchProgress> onProgress, CancellationToken ct)
            => Task.FromResult(new RegionSnapshot());
    }

    // ---------- 监控中心：服务健康 / 详情 / 日志 ----------

    [Fact]
    public async Task Monitor_Services_Detail_Logs_实效()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitor:LogDir"] = Path.Combine(Path.GetTempPath(), $"tao-mon-{Guid.NewGuid():N}")
            }).Build();
        var svc = new MonitorAppService(configuration, new StubHttpClientFactory());
        var controller = new MonitorController(svc);
        AttachHttp(controller);

        var services = await controller.Services();
        Assert.True(services.Success);
        Assert.Equal(3, services.Data!.Count); // 已知三个受监控服务
        Assert.All(services.Data!, s => Assert.Equal("DOWN", s.status)); // 未配置健康地址

        var detail = await controller.ServiceDetail("taoadmin-gateway");
        Assert.True(detail.Success);
        Assert.Equal("taoadmin-gateway", detail.Data!.serviceName);

        var fallback = await controller.ServiceDetail("no-such");
        Assert.True(fallback.Success);
        Assert.Equal("DOWN", fallback.Data!.status);

        var logs = await controller.Logs();
        Assert.True(logs.Success);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
        private sealed class StubHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    // ---------- 资源监控：快照 / 趋势 / 阈值 / 告警 ----------

    [Fact]
    public async Task ResourceMonitor_Current_History_Thresholds_Alerts_实效()
    {
        var history = new TaoAdmin.Common.Monitoring.ResourceHistoryStore();
        history.Add(new TaoAdmin.Common.Monitoring.ResourceSnapshot
        {
            ServiceName = "taoadmin-gateway", CpuPercent = 12.5, MemoryPercent = 30, DiskPercent = 40,
            CollectedAt = DateTime.Now
        });
        var thresholds = new ResourceThresholdProvider(_db, _snow, new MemoryCache(new MemoryCacheOptions()));
        var alerts = new TaoAdmin.Common.Alerting.InMemoryAlertEventPublisher();
        var controller = new ResourceMonitorController(history, alerts, thresholds);
        AttachHttp(controller);

        var current = await controller.Current();
        Assert.True(current.Success);

        var trend = await controller.History("taoadmin-gateway", 60);
        Assert.True(trend.Success);

        var clamped = await controller.History("taoadmin-gateway", -5); // 越界回退 60 分钟
        Assert.True(clamped.Success);

        var got = await controller.Thresholds();
        Assert.True(got.Success);
        Assert.Equal(ResourceThresholdProvider.DefaultCpuPercent, got.Data!.CpuPercent);

        var save = await controller.SaveThresholds(new TaoAdmin.Common.Monitoring.ResourceThresholds(70, 75, 85));
        Assert.True(save.Success);
        Assert.Equal("阈值已保存，立即生效", save.Message);
        var reread = await thresholds.GetAsync();
        Assert.Equal(70, reread.CpuPercent);

        var alertList = controller.Alerts();
        Assert.True(alertList.Success);
    }

    [Fact]
    public async Task ResourceMonitor_SaveThresholds_越界抛业务异常()
    {
        var thresholds = new ResourceThresholdProvider(_db, _snow, new MemoryCache(new MemoryCacheOptions()));
        var controller = new ResourceMonitorController(
            new TaoAdmin.Common.Monitoring.ResourceHistoryStore(),
            new TaoAdmin.Common.Alerting.InMemoryAlertEventPublisher(), thresholds);
        AttachHttp(controller);

        await Assert.ThrowsAsync<TaoAdmin.Domain.DomainException>(
            () => controller.SaveThresholds(new TaoAdmin.Common.Monitoring.ResourceThresholds(0, 75, 85)));
        await Assert.ThrowsAsync<TaoAdmin.Domain.DomainException>(
            () => controller.SaveThresholds(new TaoAdmin.Common.Monitoring.ResourceThresholds(70, 101, 85)));
        await Assert.ThrowsAsync<TaoAdmin.Domain.DomainException>(
            () => controller.SaveThresholds(new TaoAdmin.Common.Monitoring.ResourceThresholds(70, 75, 200)));
    }

    // ---------- 审计：通知发送记录 ----------

    [Fact]
    public async Task Audit_NotifyLogs_分页返回明细()
    {
        _db.Insertable(new SysNotifyLog
        {
            Id = _snow.NextId(), Channel = "email", Target = "ops@tao.local",
            Subject = "测试", Success = false, Error = "SMTP 未配置", CreatedAt = DateTime.Now
        }).ExecuteCommand();
        var controller = new NotifyChannelController(
            new NotifyChannelAppService(_db, _snow),
            new AlertNotifier(_db, _snow,
                new SmtpEmailSender(_db), new DingTalkSender(_db, new System.Net.Http.HttpClient())),
            _db,
            new AuditTrail(_db, _snow, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditTrail>.Instance));
        AttachHttp(controller);

        var logs = await controller.Logs();
        Assert.True(logs.Success);
        Assert.NotNull(logs.Data); // 有发送记录时分页数据非空
        var payload = logs.Data!;
        var props = payload.GetType().GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(1, Convert.ToInt64(props["total"].GetValue(payload)));
        var items = (System.Collections.IEnumerable)props["items"].GetValue(payload)!;
        var first = items.Cast<object>().Single();
        var itemProps = first.GetType().GetProperties().ToDictionary(p => p.Name);
        Assert.Equal("email", itemProps["channel"].GetValue(first));
        Assert.False((bool)itemProps["success"].GetValue(first)!);
    }
}
