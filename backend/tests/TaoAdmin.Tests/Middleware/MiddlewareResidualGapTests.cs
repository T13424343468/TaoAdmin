using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlSugar;
using System.Net;
using System.Security.Claims;
using TaoAdmin.Common.Web;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Web;
using Xunit;

namespace TaoAdmin.Tests.Middleware;

/// <summary>
/// 中间件残留缺口清零（分支 100% 硬门禁）：
/// ①ApiDocsAccessMiddleware：无远端地址按非本机 404、Authenticated 模式本机始终放行、
///   有身份对象但未认证 401（判定矩阵收口）；
/// ②ApiAuthMiddleware：身份对象存在但未认证（IsAuthenticated=false）跳过接口级校验。
/// </summary>
public class ApiDocsAccessGapTests
{
    private static ApiDocsAccessMiddleware Create(string mode, Func<HttpContext, Task> next)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DocsAccess:Mode"] = mode }).Build();
        return new ApiDocsAccessMiddleware(ctx => next(ctx), config);
    }

    [Fact]
    public async Task LocalOnly_远端地址缺失_按非本机404()
    {
        var reached = false;
        var mw = Create("LocalOnly", _ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.Request.Path = "/scalar";
        ctx.Connection.RemoteIpAddress = null; // 无远端地址：不得按本机放行

        await mw.InvokeAsync(ctx);

        Assert.False(reached);
        Assert.Equal(404, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_本机访问_始终放行()
    {
        var reached = false;
        var mw = Create("Authenticated", _ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/scalar";
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback; // 本机：无需登录即可看

        await mw.InvokeAsync(ctx);

        Assert.True(reached);
    }

    [Fact]
    public async Task Authenticated_身份对象存在但未认证_返回401()
    {
        var reached = false;
        var mw = Create("Authenticated", _ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.Request.Path = "/openapi/v1.json";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity()); // 有身份对象但未通过认证

        await mw.InvokeAsync(ctx);

        Assert.False(reached);
        Assert.Equal(401, ctx.Response.StatusCode);
    }
}

/// <summary>接口鉴权中间件补口：身份对象存在但未认证的请求不做接口级校验。</summary>
public class ApiAuthGapTests : IDisposable
{
    private readonly SqlSugarClient _db = new(new ConnectionConfig
    {
        ConnectionString = "DataSource=:memory:",
        DbType = SqlSugar.DbType.Sqlite,
        IsAutoCloseConnection = false
    });

    public ApiAuthGapTests() => _db.CodeFirst.InitTables<SysApi>();

    public void Dispose() => _db.Close();

    [Fact]
    public async Task NonAnonymous_身份存在但未认证_跳过接口级校验()
    {
        await _db.Insertable(new SysApi
        {
            Id = 3001, Name = "用户分页", Path = "/api/user/page", HttpMethod = "GET",
            PermsCode = "sys:user:list", Status = 1
        }).ExecuteCommandAsync();

        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/user/page";
        ctx.Request.Method = "GET";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity()); // Identity 非 null 但 IsAuthenticated=false

        await mw.InvokeAsync(ctx, _db);

        Assert.True(reached); // 未认证请求不做接口级校验（认证层职责）
        Assert.NotEqual(403, ctx.Response.StatusCode);
    }
}
