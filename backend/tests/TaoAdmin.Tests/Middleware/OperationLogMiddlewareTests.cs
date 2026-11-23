using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Middleware;
using Xunit;

namespace TaoAdmin.Tests.Middleware;

/// <summary>
/// 操作日志中间件（审计落库全分支）：
/// 登录预读三种 userName 形态与坏 JSON 容错、GET 不记、401 不记、
/// 已登录/匿名/登录三类身份取值、Success 四条件、路径与查询串截断、
/// IP 空值兜底、审计异常不破坏请求并告警。落库断言走 SQLite 内存库真实写入。
/// </summary>
public class OperationLogMiddlewareTests
{
    private static (SqlSugarClient Db, ServiceProvider Sp) CreateDbAndProvider()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysOperationLog>();

        var services = new ServiceCollection();
        services.AddSingleton<ISqlSugarClient>(db);
        services.AddSingleton(new SnowflakeIdGenerator(1, 1));
        return (db, services.BuildServiceProvider());
    }

    private static OperationLogMiddleware CreateMiddleware(
        Func<HttpContext, Task> next, ILogger<OperationLogMiddleware>? logger = null)
        => new(ctx => next(ctx), logger ?? NullLogger<OperationLogMiddleware>.Instance);

    /// <summary>轮询等待后台落库任务完成（Task.Run 异步，最多等 5 秒）。</summary>
    private static async Task<SysOperationLog> WaitForRowAsync(ISqlSugarClient db)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await db.Queryable<SysOperationLog>().ToListAsync();
            if (rows.Count > 0) return rows[0];
            await Task.Delay(50);
        }
        throw new TimeoutException("操作日志未在时限内落库");
    }

    /// <summary>等待一小段时间后确认没有落库（用于「不应记录」分支）。</summary>
    private static async Task AssertNoRowAsync(ISqlSugarClient db)
    {
        await Task.Delay(1000);
        Assert.Empty(await db.Queryable<SysOperationLog>().ToListAsync());
    }

    private static void SetAuthedUser(HttpContext http, long userId = 42, long tenantId = 7, string name = "carol")
    {
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("UserId", userId.ToString()),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim(ClaimTypes.Name, name)
        }, "Test"));
    }

    [Fact]
    public async Task LoginPath_JsonBody_ShouldBufferAndAllowDownstreamReread()
    {
        // 登录路径预读请求体提取 userName（密码不提取不落库），读完复位供下游完整重读
        var body = """{"userName":"alice","password":"Secret#123"}""";
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/auth/login";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.ContentLength = body.Length;

        string? seenByDownstream = null;
        await CreateMiddleware(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
            seenByDownstream = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
        }).InvokeAsync(http);

        Assert.Equal(body, seenByDownstream); // 下游拿到的请求体完整未损
    }

    [Fact]
    public async Task LoginPath_MalformedJson_ShouldNotBreakPipeline()
    {
        // 请求体不是合法 JSON：解析失败静默容忍，业务照常执行
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/api/identity/auth/login";
        var raw = "not-a-json-body";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        http.Request.ContentLength = raw.Length;

        var reachedBusiness = false;
        await CreateMiddleware(ctx =>
        {
            reachedBusiness = true;
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        Assert.True(reachedBusiness);
        Assert.Equal(200, http.Response.StatusCode);
    }

    [Fact]
    public async Task BusinessThrow_ShouldPropagate_NotSwallowed()
    {
        // 中间件不得吞业务异常：审计失败不影响业务，业务失败也不能被审计掩盖
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMiddleware(_ => throw new InvalidOperationException("业务失败"))
                .InvokeAsync(http));
    }

    [Fact]
    public async Task WriteRequest_ShouldPassThrough()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "PUT";
        http.Request.Path = "/api/users/1";

        var reachedBusiness = false;
        await CreateMiddleware(ctx =>
        {
            reachedBusiness = true;
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        Assert.True(reachedBusiness);
        Assert.Equal(204, http.Response.StatusCode);
    }

    // ---------- 登录预读：userName 三种提取形态 ----------

    [Fact]
    public async Task LoginSuccess_ShouldRecordLoginAttempt_WithLowerCamelUserName()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/auth/login";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"userName":"alice"}"""));
        http.Request.ContentLength = 20;

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("login", row.Category);
        Assert.Equal("alice", row.UserName); // 小驼峰 userName 命中
        Assert.Equal(0, row.UserId);         // 登录时无身份
        Assert.Equal(0, row.TenantId);
        Assert.Equal("POST", row.Method);
        Assert.Equal("/auth/login", row.Path);
        Assert.Equal(200, row.StatusCode);
        Assert.Equal(1, row.Success);
        Assert.Equal("", row.Ip);            // RemoteIpAddress 为 null 时兜底空串
        Assert.True(row.ElapsedMs >= 0);
    }

    [Fact]
    public async Task LoginBody_PascalUserName_ShouldBeExtracted()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/login";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"UserName":"Bob"}"""));
        http.Request.ContentLength = 17;

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("Bob", row.UserName); // 大驼峰 UserName 兜底命中
        Assert.Equal("login", row.Category);
    }

    [Fact]
    public async Task LoginBody_WithoutUserNameField_ShouldRecordEmptyAccount()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/auth/login";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"account":"x"}"""));
        http.Request.ContentLength = 15;

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("login", row.Category);
        Assert.Equal("", row.UserName); // 两个属性名都未命中 → null → 落库为空串
    }

    [Fact]
    public async Task LoginPath_EmptyBody_ShouldStillRecord_WithoutReadingBody()
    {
        // ContentLength 为 null（≤0）：跳过预读分支，登录尝试仍要落审计
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/auth/login";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("login", row.Category);
        Assert.Equal("", row.UserName);
    }

    [Fact]
    public async Task LoginPath_MalformedJson_ShouldStillRecordLoginAttempt()
    {
        // 坏 JSON：预读 catch 吞掉，但登录尝试（含暴力破解）必须留痕
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/auth/login";
        var raw = "not-a-json-body";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        http.Request.ContentLength = raw.Length;

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("login", row.Category);
        Assert.Equal("", row.UserName);
    }

    // ---------- 记录门槛：GET 不记、401 不记 ----------

    [Fact]
    public async Task GetRequest_NonLoginPath_ShouldNotRecord()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "GET";
        http.Request.Path = "/api/users";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        await AssertNoRowAsync(db); // 非登录 GET：不记审计
    }

    [Fact]
    public async Task UnauthorizedWrite_ShouldNotRecord()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/users";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        await AssertNoRowAsync(db); // 未登录 401：不记，避免噪声
    }

    [Fact]
    public async Task LoginPath_WithGetMethod_ShouldStillRecord()
    {
        // 登录路径即使是 GET 也要记（isLogin 优先于写操作判断）
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "GET";
        http.Request.Path = "/login";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("login", row.Category);
        Assert.Equal("GET", row.Method);
    }

    // ---------- 身份取值：已登录 / 匿名 ----------

    [Fact]
    public async Task AuthorizedWrite_ShouldRecordOperatorFromClaims()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/users";
        http.Request.QueryString = new QueryString("?page=1&size=10");
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        http.TraceIdentifier = "trace-abc";
        SetAuthedUser(http, userId: 42, tenantId: 7, name: "carol");

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("operation", row.Category);
        Assert.Equal(42, row.UserId);        // 已登录：从 UserId 声明取
        Assert.Equal(7, row.TenantId);       // 已登录：从 tenant_id 声明取
        Assert.Equal("carol", row.UserName); // 已登录：取 Identity.Name
        Assert.Equal(1, row.Success);
        Assert.Equal("10.1.2.3", row.Ip);
        Assert.Equal("trace-abc", row.TraceId);
        Assert.Equal("?page=1&size=10", row.QueryString); // 非空查询串原样保留
    }

    [Fact]
    public async Task AnonymousWrite_200_ShouldRecordAsAnonymous()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("operation", row.Category);
        Assert.Equal(0, row.UserId);   // 匿名：身份三字段全空
        Assert.Equal(0, row.TenantId);
        Assert.Equal("", row.UserName);
        Assert.Equal(1, row.Success);
    }

    [Fact]
    public async Task AnonymousWrite_EmptyPath_ShouldRecordEmptyPath()
    {
        // Path 未设置（HasValue=false）：路径取值兜底空串，写操作照常落库
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal("", row.Path);
        Assert.Equal("operation", row.Category);
    }

    [Fact]
    public async Task AnonymousWrite_NoIdentityAtAll_ShouldRecordAsAnonymous()
    {
        // ClaimsPrincipal 无任何身份（Identity 为 null）：?. 判空走 null 分支，仍按匿名落库
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";
        http.User = new ClaimsPrincipal(); // 无任何 ClaimsIdentity

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal(0, row.UserId);
        Assert.Equal(0, row.TenantId);
        Assert.Equal("", row.UserName);
        Assert.Equal(1, row.Success);
    }

    [Fact]
    public async Task DefaultQueryString_ShouldRecordNull()
    {
        // default(QueryString) 的 Value 为 null：?? "" 兜底生效，落库为 NULL
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";
        http.Request.QueryString = default(QueryString);

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Null(row.QueryString);
        Assert.Equal(1, row.Success);
    }

    // ---------- Success 四条件 ----------

    [Fact]
    public async Task ServerError5xx_ShouldMarkFailure()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 500;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal(500, row.StatusCode);
        Assert.Equal(0, row.Success); // StatusCode ≥ 400 → 失败
    }

    [Fact]
    public async Task ClientError4xx_ShouldMarkFailure()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal(0, row.Success); // 4xx 同样判失败
    }

    [Fact]
    public async Task BizFailItem_ShouldMarkFailure()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await CreateMiddleware(ctx =>
        {
            ctx.Items["BizFail"] = true; // HTTP 200 但业务失败
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal(200, row.StatusCode);
        Assert.Equal(0, row.Success); // BizFail 标记 → 失败
    }

    [Fact]
    public async Task BusinessThrow_ShouldRecordFailure_AndRethrow()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateMiddleware(_ => throw new InvalidOperationException("业务失败"))
                .InvokeAsync(http));

        var row = await WaitForRowAsync(db);
        Assert.Equal(0, row.Success); // 异常上抛的同时，审计记失败
        Assert.Equal(200, row.StatusCode);
    }

    // ---------- 截断与空值兜底 ----------

    [Fact]
    public async Task LongPath_ShouldTruncateTo200()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/" + new string('a', 250);

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal(200, row.Path.Length); // 超长路径截断到 200
        Assert.Equal(255, ("/api/" + new string('a', 250)).Length); // 原路径确实超长
    }

    [Fact]
    public async Task LongQueryString_ShouldTruncateTo500()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";
        http.Request.QueryString = new QueryString("?" + new string('q', 600));

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Equal(500, row.QueryString!.Length); // 超长查询串截断到 500
    }

    [Fact]
    public async Task EmptyQueryString_ShouldRecordNull()
    {
        var (db, sp) = CreateDbAndProvider();
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        await CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        }).InvokeAsync(http);

        var row = await WaitForRowAsync(db);
        Assert.Null(row.QueryString); // 空查询串落库为 NULL，不是空串
    }

    // ---------- 审计自身异常：不破坏请求 ----------

    /// <summary>委托包装：仅 Response 访问抛异常，模拟审计链路自身故障。</summary>
    private class ThrowingResponseContext : HttpContext
    {
        private readonly DefaultHttpContext _inner = new();

        public override IFeatureCollection Features => _inner.Features;
        public override HttpRequest Request => _inner.Request;
        public override HttpResponse Response => throw new InvalidOperationException("审计读响应失败");
        public override IDictionary<object, object?> Items { get => _inner.Items; set => _inner.Items = value; }
        public override ClaimsPrincipal User { get => _inner.User; set => _inner.User = value; }
        public override ConnectionInfo Connection => _inner.Connection;
        public override string TraceIdentifier { get => _inner.TraceIdentifier; set => _inner.TraceIdentifier = value; }
        public override ISession Session { get => _inner.Session; set => _inner.Session = value; }
        public override IServiceProvider RequestServices { get => _inner.RequestServices!; set => _inner.RequestServices = value; }
        public override CancellationToken RequestAborted { get => _inner.RequestAborted; set => _inner.RequestAborted = value; }
        public override WebSocketManager WebSockets => _inner.WebSockets;
        public override void Abort() => _inner.Abort();
    }

    [Fact]
    public async Task AuditFailure_ShouldNotBreakRequest_AndLogWarning()
    {
        var logger = new CapturingLogger();
        var http = new ThrowingResponseContext();
        http.Request.Method = "POST";
        http.Request.Path = "/api/orders";

        var reachedBusiness = false;
        await CreateMiddleware(ctx =>
        {
            reachedBusiness = true;
            return Task.CompletedTask;
        }, logger).InvokeAsync(http);

        Assert.True(reachedBusiness); // 请求本身不受审计故障影响
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("操作日志记录失败", warning.Msg);
        Assert.IsType<InvalidOperationException>(warning.Ex);
    }

    private class CapturingLogger : ILogger<OperationLogMiddleware>
    {
        public List<(LogLevel Level, Exception? Ex, string Msg)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
