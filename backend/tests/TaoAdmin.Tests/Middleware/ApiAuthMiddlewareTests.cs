using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using SqlSugar;
using TaoAdmin.Common.Auth;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Web;
using Xunit;

namespace TaoAdmin.Tests.Middleware;

/// <summary>
/// 接口级授权中间件实效断言（SQLite 内存库真库）：
/// 匿名接口/未认证/未登记接口放行；已登记接口按权限码校验——
/// 无权限 403（下游不触达）、带权限码或超管放行；停用/已删除登记不命中；方法大小写归一。
/// </summary>
public class ApiAuthMiddlewareTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly ApiAuthMiddleware _mw;

    public ApiAuthMiddlewareTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysApi>();
        _mw = new ApiAuthMiddleware(_ => Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Close();
        // SQLite 内存库随连接关闭释放；临时文件库（若有）按文件名清理
        var tmp = Path.Combine(Path.GetTempPath(), "apiauth_test.db");
        if (File.Exists(tmp)) File.Delete(tmp);
    }

    private async Task SeedApi(string path = "/api/user/page", string method = "GET",
        string perms = "sys:user:list", int status = 1, bool deleted = false)
    {
        await _db.Insertable(new SysApi
        {
            Id = 3001, Name = "用户分页", Path = path, HttpMethod = method,
            PermsCode = perms, Status = status, IsDeleted = deleted
        }).ExecuteCommandAsync();
    }

    private static HttpContext Ctx(string path, string method, ClaimsPrincipal? user = null,
        bool anonymous = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        if (user != null) ctx.User = user;
        if (anonymous)
            ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new AllowAnonymousAttribute()), "anon"));
        return ctx;
    }

    private static ClaimsPrincipal UserWithPerms(params string[] perms)
    {
        var claims = perms.Select(p => new Claim(PermissionAuthorizationHandler.PermClaimType, p)).ToList();
        claims.Add(new Claim(ClaimTypes.Name, "alice"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal SuperAdmin()
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "root"),
            new Claim(ClaimTypes.Role, PermissionAuthorizationHandler.SuperAdminRole)
        }, "TestAuth"));

    [Fact]
    public async Task AnonymousEndpoint_ShouldPassThrough_EvenWithoutPermission()
    {
        // 匿名接口直接放行（能红：若漏判匿名会 403）
        await SeedApi();
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", UserWithPerms(), anonymous: true), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task Unauthenticated_ShouldPassThrough()
    {
        // 未认证请求不做接口级校验（认证层职责）
        await SeedApi();
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET"), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task UnregisteredApi_ShouldPassThrough()
    {
        // 向后兼容：未登记接口放行
        await SeedApi(path: "/api/other");
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", UserWithPerms()), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task Registered_NoPermission_ShouldReturn403_AndNotReachNext()
    {
        await SeedApi();
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        var ctx = Ctx("/api/user/page", "GET", UserWithPerms("sys:other:read"));

        await mw.InvokeAsync(ctx, _db);

        Assert.False(reached); // 实效断言：下游未被触达
        Assert.Equal(403, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("\"code\":403", body);
        Assert.Contains("没有该接口的访问权限", body);
        Assert.Contains(ctx.TraceIdentifier, body); // traceId 透出便于排查
    }

    [Fact]
    public async Task Registered_WithPermClaim_ShouldPassThrough()
    {
        await SeedApi(perms: "Sys:User:List"); // 登记权限码大小写混合
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        // 用户权限码全小写——OrdinalIgnoreCase 匹配
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", UserWithPerms("sys:user:list")), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task Registered_SuperAdmin_ShouldPassThrough()
    {
        await SeedApi();
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", SuperAdmin()), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task Registered_DisabledStatus_ShouldPassThrough()
    {
        // 停用的登记不参与校验（能红：若漏判 Status 会 403）
        await SeedApi(status: 0);
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", UserWithPerms()), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task Registered_Deleted_ShouldPassThrough()
    {
        // 已删除的登记不参与校验
        await SeedApi(deleted: true);
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", UserWithPerms()), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task MethodCase_ShouldBeNormalized()
    {
        // 请求方法小写、登记大写——归一后命中（能红：若不归一会放行）
        await SeedApi(method: "POST");
        var mw = new ApiAuthMiddleware(_ => Task.CompletedTask);
        var ctx = Ctx("/api/user/page", "post", UserWithPerms());

        await mw.InvokeAsync(ctx, _db);

        Assert.Equal(403, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task WrongMethod_ShouldPassThrough()
    {
        // 登记的是 GET，请求 DELETE——不命中
        await SeedApi(method: "GET");
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "DELETE", UserWithPerms()), _db);
        Assert.True(reached);
    }

    [Fact]
    public async Task Registered_EmptyPermsCode_ShouldReturn403()
    {
        // 登记接口未配权限码：普通用户拒绝（空权限码判定为未授权）
        await SeedApi(perms: "");
        var mw = new ApiAuthMiddleware(_ => Task.CompletedTask);
        var ctx = Ctx("/api/user/page", "GET", UserWithPerms());

        await mw.InvokeAsync(ctx, _db);

        Assert.Equal(403, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Registered_EmptyPermsCode_SuperAdmin_ShouldPassThrough()
    {
        // 空权限码对超管仍放行（超管短路在权限码判定之前）
        await SeedApi(perms: "");
        var reached = false;
        var mw = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; });
        await mw.InvokeAsync(Ctx("/api/user/page", "GET", SuperAdmin()), _db);
        Assert.True(reached);
    }
}
