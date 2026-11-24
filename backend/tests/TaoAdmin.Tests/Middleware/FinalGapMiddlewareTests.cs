using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SqlSugar;
using TaoAdmin.Common.Web;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Web;
using Xunit;

namespace TaoAdmin.Tests.Middleware;

/// <summary>
/// 终账收尾中间件缺口清零（分支 100% 硬门禁）：
/// ①ApiDocsAccessMiddleware：User.Identity 为 null（?. 短路侧）按未认证 401；
/// ②ApiAuthMiddleware：User.Identity 为 null 时跳过接口级校验（is 模式 null 侧）。
/// </summary>
public class FinalGapMiddlewareTests
{
    private static ApiDocsAccessMiddleware CreateDocsMw(string mode, Func<HttpContext, Task> next)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DocsAccess:Mode"] = mode }).Build();
        return new ApiDocsAccessMiddleware(ctx => next(ctx), config);
    }

    [Fact]
    public async Task DocsAccess_身份对象为null_按未认证401()
    {
        var reached = false;
        var mw = CreateDocsMw("Authenticated", _ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        ctx.Request.Path = "/openapi/v1.json";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");
        ctx.User = new ClaimsPrincipal(); // Identity == null：?. 短路侧

        await mw.InvokeAsync(ctx);

        Assert.False(reached);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ApiAuth_身份对象为null_跳过接口级校验()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysApi>();
        await db.Insertable(new SysApi
        {
            Id = 4001, Name = "用户分页", Path = "/api/user/page", HttpMethod = "GET",
            PermsCode = "sys:user:list", Status = 1
        }).ExecuteCommandAsync();

        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/user/page";
        ctx.Request.Method = "GET";
        ctx.User = new ClaimsPrincipal(); // Identity == null：is 模式 null 侧

        await mw.InvokeAsync(ctx, db);

        Assert.True(reached); // 无身份对象不做接口级校验（认证层职责）
        Assert.NotEqual(403, ctx.Response.StatusCode);
    }
}
