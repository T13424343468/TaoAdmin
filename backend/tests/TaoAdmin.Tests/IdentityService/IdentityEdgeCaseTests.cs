using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Monitoring;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Handlers;
using TaoAdmin.IdentityService.Middleware;
using TaoAdmin.IdentityService.Services;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 服务内边角分支补齐：会话吊销中间件「带身份但未认证」态、
/// 用户创建消息处理器空载荷短路、资源监控快照解析为空与后台循环成功轮。
/// </summary>
public class IdentityEdgeCaseTests
{
    // ---------- 会话吊销中间件：身份存在但未认证 ----------

    [Fact]
    public async Task SessionRevocation_身份未认证_直接放行()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.User = new System.Security.Claims.ClaimsPrincipal(); // 无任何身份 → Identity 为 null 的分支

        var nextCalled = false;
        Microsoft.AspNetCore.Http.RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var mw = new TaoAdmin.Common.Auth.SessionRevocationMiddleware(next);

        await mw.InvokeAsync(ctx, new OnlineSessionStore());

        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);

        // 带身份但未认证（未指定认证类型）→ IsAuthenticated == false 的分支
        var ctx2 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx2.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var nextCalled2 = false;
        Microsoft.AspNetCore.Http.RequestDelegate next2 = _ => { nextCalled2 = true; return Task.CompletedTask; };
        await new TaoAdmin.Common.Auth.SessionRevocationMiddleware(next2).InvokeAsync(ctx2, new OnlineSessionStore());
        Assert.True(nextCalled2);
        Assert.NotEqual(401, ctx2.Response.StatusCode);
    }

    // ---------- 用户创建消息处理器：空载荷短路 ----------

    [Fact]
    public async Task UserCreated_空载荷_直接短路不落库()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysMessage>();

        var handler = new UserCreatedMessageHandler(db, new SnowflakeIdGenerator(12, 12),
            NullLogger<UserCreatedMessageHandler>.Instance);
        await handler.HandleAsync(new TaoAdmin.Common.Messaging.MessageEnvelope { Topic = "user.created", Body = "null" });

        Assert.Empty(db.Queryable<SysMessage>().ToList()); // payload == null 的短路分支
    }

    // ---------- 资源监控：快照解析为空 / 后台循环成功轮 ----------

    private sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> responder)
        : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private sealed class StubFactory(System.Net.Http.HttpMessageHandler handler) : IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(handler);
    }

    private static (ResourceMonitorService Svc, ResourceHistoryStore History) Build(
        Dictionary<string, string?> config, System.Net.Http.HttpMessageHandler handler, bool registerThresholds = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var history = new ResourceHistoryStore();

        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysSiteConfig>();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<ISqlSugarClient>(db);
        services.AddSingleton(new SnowflakeIdGenerator(7, 7));
        services.AddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        if (registerThresholds)
            services.AddSingleton<ResourceThresholdProvider>();
        var provider = services.BuildServiceProvider();

        return (new ResourceMonitorService(new StubFactory(handler), configuration, history,
            new TaoAdmin.Common.Alerting.InMemoryAlertEventPublisher(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ResourceMonitorService>.Instance), history);
    }

    [Fact]
    public async Task SampleAllAsync_快照解析为null_不入账()
    {
        var (svc, history) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Services:empty"] = "http://empty"
        }, new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("null") // JSON null → ReadFromJsonAsync 返回 null
        }));

        await svc.SampleAllAsync(System.Threading.CancellationToken.None);

        Assert.Null(history.LatestOf("empty")); // snapshot == null 的短路分支
    }

    [Fact]
    public async Task ExecuteAsync_成功轮次_循环体完整执行()
    {
        var (svc, history) = Build(new Dictionary<string, string?>
        {
            ["Monitor:Resource:IntervalSeconds"] = "1",
            ["Monitor:Services:gateway"] = "http://gw"
        }, new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(
                """{"cpuPercent":10,"memoryPercent":10,"diskPercent":10}""",
                System.Text.Encoding.UTF8, "application/json")
        }), registerThresholds: true);

        using var cts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(cts.Token);
        await Task.Delay(1300); // 至少跑过一轮成功采样
        cts.Cancel();
        await svc.StopAsync(System.Threading.CancellationToken.None);

        Assert.NotNull(history.LatestOf("gateway")); // try 块正常走完的成功轮分支
    }
}
