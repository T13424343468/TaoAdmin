using Microsoft.AspNetCore.Http;
using TaoAdmin.Common.Auth;
using TaoAdmin.IdentityService.Middleware;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 会话吊销中间件实效测试：四态全覆盖——
/// 未认证放行、认证无jti放行、已吊销401不触达下游、活跃续期后放行。
/// 用真实 OnlineSessionStore（Register/Revoke/Touch 语义）作桩，断言实效：
/// 下游是否触达、状态码、吊销与续期行为均可区分。
/// </summary>
public class SessionRevocationMiddlewareTests
{
    private static HttpContext BuildContext(bool authed, string? jti)
    {
        var ctx = new DefaultHttpContext();
        if (authed)
        {
            var identity = new System.Security.Principal.GenericIdentity("u1");
            ctx.User = new System.Security.Claims.ClaimsPrincipal(identity);
            if (jti != null) ctx.User.AddIdentity(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("jti", jti)]));
        }
        return ctx;
    }

    private static async Task<(HttpContext Ctx, bool NextCalled)> RunAsync(
        bool authed, string? jti, OnlineSessionStore store)
    {
        var ctx = BuildContext(authed, jti);
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var mw = new SessionRevocationMiddleware(next);
        await mw.InvokeAsync(ctx, store);
        return (ctx, nextCalled);
    }

    [Fact]
    public async Task Anonymous_ShouldPassThrough()
    {
        var store = new OnlineSessionStore();
        var (ctx, nextCalled) = await RunAsync(authed: false, jti: null, store);
        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AuthedWithoutJti_ShouldPassThrough()
    {
        var store = new OnlineSessionStore();
        var (ctx, nextCalled) = await RunAsync(authed: true, jti: null, store);
        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task RevokedJti_ShouldReturn401_AndNotReachDownstream()
    {
        var store = new OnlineSessionStore();
        store.Register(new OnlineSession { SessionId = "tok-dead", UserId = 1, UserName = "u1" });
        store.Revoke("tok-dead");

        var (ctx, nextCalled) = await RunAsync(authed: true, jti: "tok-dead", store);
        Assert.False(nextCalled);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ActiveJti_ShouldTouch_AndPassThrough()
    {
        var store = new OnlineSessionStore();
        store.Register(new OnlineSession { SessionId = "tok-live", UserId = 1, UserName = "u1" });

        var (ctx, nextCalled) = await RunAsync(authed: true, jti: "tok-live", store);
        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
        // 续期语义：活跃会话在 Touch 后仍有效，未进吊销名单
        Assert.False(store.IsRevoked("tok-live"));
    }
}
