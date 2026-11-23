using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Alerting;
using TaoAdmin.Common.Monitoring;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Services;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 资源监控后台任务实效测试：HTTP 桩返回真实快照 JSON，走真阈值提供口（SQLite 内存库），
/// 断言采样入历史、探测失败不入账、超限告警/持续不重发/恢复再发的状态迁移，以及周期循环可启可停。
/// </summary>
public class ResourceMonitorServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        // 与真实 IHttpClientFactory 同契约：每次 CreateClient 返回新 HttpClient（共享处理器），
        // 供服务在每次采样时设置 Timeout 等属性
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class RecordingPublisher : IAlertEventPublisher
    {
        public List<AlertEvent> Events { get; } = new();
        public Task PublishAsync(AlertEvent evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private static (ResourceMonitorService Svc, ResourceHistoryStore History, RecordingPublisher Publisher)
        Build(Dictionary<string, string?> config, HttpMessageHandler handler, bool registerThresholds = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var history = new ResourceHistoryStore();
        var publisher = new RecordingPublisher();

        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysSiteConfig>();

        var services = new ServiceCollection();
        services.AddSingleton<ISqlSugarClient>(db);
        services.AddSingleton(new SnowflakeIdGenerator(7, 7));
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        if (registerThresholds)
            services.AddSingleton<ResourceThresholdProvider>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var svc = new ResourceMonitorService(new StubFactory(handler), configuration,
            history, publisher, scopeFactory, NullLogger<ResourceMonitorService>.Instance);
        return (svc, history, publisher);
    }

    private static HttpResponseMessage JsonSnap(double cpu, double mem = 10, double disk = 10)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"cpuPercent":{{cpu}},"memoryPercent":{{mem}},"diskPercent":{{disk}}}""",
                System.Text.Encoding.UTF8, "application/json")
        };

    private static Dictionary<string, string?> OneService() =>
        new() { ["Monitor:Services:gateway"] = "http://gw" };

    [Fact]
    public void ConfiguredServices_读取配置并过滤空值()
    {
        var (svc, _, _) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Services:gateway"] = "http://gw",
            ["Monitor:Services:empty"] = ""
        }, new StubHandler(_ => JsonSnap(10)));

        var list = svc.ConfiguredServices();
        Assert.Single(list);
        Assert.Equal(("gateway", "http://gw"), list[0]);
    }

    [Fact]
    public async Task SampleAllAsync_正常采样入历史且不告警()
    {
        var (svc, history, publisher) = Build(OneService(), new StubHandler(_ => JsonSnap(10)));

        await svc.SampleAllAsync(CancellationToken.None);

        var latest = history.LatestOf("gateway");
        Assert.NotNull(latest);
        Assert.Equal("gateway", latest!.ServiceName); // 快照服务名以配置清单为准
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task SampleAllAsync_探测失败或异常均不入账()
    {
        var (svc, history, publisher) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Services:bad"] = "http://bad",     // HTTP 500
            ["Monitor:Services:down"] = "http://down"    // 连接抛异常
        }, new StubHandler(_ => throw new IOException("connection refused")));

        await svc.SampleAllAsync(CancellationToken.None);

        Assert.Null(history.LatestOf("bad"));
        Assert.Null(history.LatestOf("down"));
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task SampleAllAsync_超限告警_持续不重发_恢复再发()
    {
        int call = 0;
        var (svc, _, publisher) = Build(OneService(),
            new StubHandler(_ => JsonSnap(++call <= 2 ? 99 : 10)));

        await svc.SampleAllAsync(CancellationToken.None); // 首次超限：发告警（99 > 80+10 → Critical）
        await svc.SampleAllAsync(CancellationToken.None); // 持续超限：不重发
        await svc.SampleAllAsync(CancellationToken.None); // 恢复：发恢复事件

        Assert.Equal(2, publisher.Events.Count);
        var alert = publisher.Events[0];
        Assert.Equal("resource.threshold", alert.Type);
        Assert.Equal("gateway", alert.Source);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Contains("CPU 99%（阈值 80%）", alert.Content);
        Assert.Equal(AlertSeverity.Info, publisher.Events[1].Severity); // 恢复事件为 Info 级
    }

    [Fact]
    public async Task SampleAllAsync_HTTP非2xx与空内容均不入账()
    {
        var (svc, history, publisher) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Services:bad"] = "http://bad",     // HTTP 500：非 2xx 分支
            ["Monitor:Services:empty"] = "http://empty"  // 204 空内容：解析为 null 分支
        }, new StubHandler(req => req.RequestUri!.Host == "bad"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
            {
                Content = new StringContent("")
            }));

        await svc.SampleAllAsync(CancellationToken.None);

        Assert.Null(history.LatestOf("bad"));
        Assert.Null(history.LatestOf("empty"));
        Assert.Empty(publisher.Events);
    }

    [Fact]
    public async Task ExecuteAsync_采样中取消_走OCE分支退出()
    {
        // 采样阶段抛 OCE（FetchAsync 的异常过滤器放行取消）→ ExecuteAsync 捕获后跳出循环
        var (svc, _, publisher) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Resource:IntervalSeconds"] = "1",
            ["Monitor:Services:gateway"] = "http://gw"
        }, new StubHandler(_ => throw new OperationCanceledException("采样被取消")));

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        var finished = await Task.WhenAny(svc.ExecuteTask, Task.Delay(8000));

        Assert.Same(svc.ExecuteTask, finished); // 循环已退出而非挂死
        Assert.Empty(publisher.Events);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_周期采样_单轮失败不终止_取消即停()
    {
        // 不注册阈值提供口 → 每轮采样抛异常 → 走「单轮失败不终止」分支；间隔 1 秒
        var (svc, _, publisher) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Resource:IntervalSeconds"] = "1",
            ["Monitor:Services:gateway"] = "http://gw"
        }, new StubHandler(_ => JsonSnap(10)), registerThresholds: false);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await Task.Delay(1300); // 至少跑过一轮失败采样
        cts.Cancel();
        await svc.StopAsync(CancellationToken.None); // 取消后循环应退出，不挂死

        Assert.Empty(publisher.Events); // 失败轮次不产生任何事件
    }
}
