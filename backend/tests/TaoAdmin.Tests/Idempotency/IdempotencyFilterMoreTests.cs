using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Idempotency;
using Xunit;

namespace TaoAdmin.Tests.Idempotency;

/// <summary>
/// 幂等过滤器补充口径（防重放缺口补测）：
/// 业务异常不缓存（可重试）、租户/用户/路径维度的幂等键隔离、空载荷不缓存。
/// </summary>
public class IdempotencyFilterMoreTests
{
    private static ActionExecutingContext BuildContext(
        string? idempotencyKey = "key-X",
        string tenantId = "1",
        string userId = "9",
        string path = "/api/order")
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("tenant_id", tenantId), new Claim("UserId", userId) }, "Bearer"));
        http.Request.Path = path;
        if (idempotencyKey != null) http.Request.Headers["Idempotency-Key"] = idempotencyKey;

        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = new List<object> { new IdempotentAttribute() }
        };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), new object());
    }

    private static ActionExecutionDelegate NextExecuting(ActionExecutingContext ctx, bool throwInBusiness)
        => new(() =>
        {
            if (throwInBusiness) throw new InvalidOperationException("业务执行失败");
            return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok(new { ok = true }))
            });
        });

    [Fact]
    public async Task BusinessException_ShouldNotCache_NextRequestShouldExecute()
    {
        // 第一次：业务抛异常 → 结果不缓存；第二次同 Key 重试应真实执行（可重试口径）
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var ctx1 = BuildContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => filter.OnActionExecutionAsync(ctx1, NextExecuting(ctx1, throwInBusiness: true)));
        Assert.Null(ctx1.Result);

        var executed2 = false;
        var ctx2 = BuildContext();
        await filter.OnActionExecutionAsync(ctx2, NextExecuting(ctx2, throwInBusiness: false));
        executed2 = true;
        Assert.True(executed2);
        Assert.Null(ctx2.Result); // 第二次是真实执行而非重放
    }

    [Fact]
    public async Task DifferentTenant_SameKey_ShouldNotShareIdempotencyRecord()
    {
        // 幂等键 = 租户 + 用户 + 路径 + Key：租户 A 的提交不得吞掉租户 B 的同 Key 请求
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var ctxTenantA = BuildContext(tenantId: "1");
        var executedA = false;
        await filter.OnActionExecutionAsync(ctxTenantA, new ActionExecutionDelegate(() =>
        {
            executedA = true;
            return Task.FromResult(new ActionExecutedContext(ctxTenantA, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("tenant-A"))
            });
        }));
        Assert.True(executedA);

        var ctxTenantB = BuildContext(tenantId: "2");
        var executedB = false;
        await filter.OnActionExecutionAsync(ctxTenantB, new ActionExecutionDelegate(() =>
        {
            executedB = true;
            return Task.FromResult(new ActionExecutedContext(ctxTenantB, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("tenant-B"))
            });
        }));
        Assert.True(executedB); // 租户 2 未被租户 1 的缓存命中吞掉
    }

    [Fact]
    public async Task DifferentUser_SameKey_ShouldNotShareIdempotencyRecord()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var ctxUserA = BuildContext(userId: "9");
        await filter.OnActionExecutionAsync(ctxUserA, new ActionExecutionDelegate(() =>
            Task.FromResult(new ActionExecutedContext(ctxUserA, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("user-A"))
            })));

        var ctxUserB = BuildContext(userId: "10");
        var executedB = false;
        await filter.OnActionExecutionAsync(ctxUserB, new ActionExecutionDelegate(() =>
        {
            executedB = true;
            return Task.FromResult(new ActionExecutedContext(ctxUserB, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("user-B"))
            });
        }));
        Assert.True(executedB);
    }

    [Fact]
    public async Task DifferentPath_SameKey_ShouldNotShareIdempotencyRecord()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var ctxPath1 = BuildContext(path: "/api/order");
        await filter.OnActionExecutionAsync(ctxPath1, new ActionExecutionDelegate(() =>
            Task.FromResult(new ActionExecutedContext(ctxPath1, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("order"))
            })));

        var ctxPath2 = BuildContext(path: "/api/payment");
        var executed2 = false;
        await filter.OnActionExecutionAsync(ctxPath2, new ActionExecutionDelegate(() =>
        {
            executed2 = true;
            return Task.FromResult(new ActionExecutedContext(ctxPath2, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("payment"))
            });
        }));
        Assert.True(executed2);
    }

    [Fact]
    public async Task NullPayload_ShouldNotBeCached()
    {
        // 无载荷结果（如 VoidResult）不写入缓存：同 Key 第二次仍真实执行
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var ctx1 = BuildContext();
        await filter.OnActionExecutionAsync(ctx1, new ActionExecutionDelegate(() =>
            Task.FromResult(new ActionExecutedContext(ctx1, new List<IFilterMetadata>(), null!)
            {
                Result = new EmptyResult()
            })));

        var ctx2 = BuildContext();
        var executed2 = false;
        await filter.OnActionExecutionAsync(ctx2, new ActionExecutionDelegate(() =>
        {
            executed2 = true;
            return Task.FromResult(new ActionExecutedContext(ctx2, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok(new { n = 1 }))
            });
        }));
        Assert.True(executed2);
    }

    [Fact]
    public async Task MissingTenantAndUserClaims_ShouldFallBackToDefaults()
    {
        // 无 tenant_id / UserId claim：cacheKey 兜底 "0" 与 "anon"，不抛异常且正常缓存
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity("Bearer")); // 无任何 claim
        http.Request.Path = "/api/anon";
        http.Request.Headers["Idempotency-Key"] = "key-anon";

        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = new List<object> { new IdempotentAttribute() }
        };
        var ctx = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

        await filter.OnActionExecutionAsync(ctx, new ActionExecutionDelegate(() =>
            Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok("anon-ok"))
            })));

        // 兜底键真实写入：0:anon:/api/anon:key-anon
        Assert.NotNull(store.TryGet("0:anon:/api/anon:key-anon"));
    }

    [Fact]
    public async Task Replay_ShouldReturnCachedBody_AsJsonResult()
    {
        // 重放路径：命中缓存 → 反序列化缓存体为 ApiResult 返回，且带重放标记
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);

        var ctx1 = BuildContext(idempotencyKey: "key-R");
        await filter.OnActionExecutionAsync(ctx1, new ActionExecutionDelegate(() =>
            Task.FromResult(new ActionExecutedContext(ctx1, new List<IFilterMetadata>(), null!)
            {
                Result = new ObjectResult(ApiResult<object>.Ok(new { orderId = "R1" }))
            })));

        var ctx2 = BuildContext(idempotencyKey: "key-R");
        var executed2 = false;
        await filter.OnActionExecutionAsync(ctx2, new ActionExecutionDelegate(() =>
        {
            executed2 = true;
            return Task.FromResult(new ActionExecutedContext(ctx2, new List<IFilterMetadata>(), null!));
        }));

        Assert.False(executed2); // 业务未执行
        Assert.Equal("true", ctx2.HttpContext.Response.Headers["X-Idempotent-Replayed"].ToString());
        var json = Assert.IsType<JsonResult>(ctx2.Result);
        var replayed = Assert.IsType<ApiResult<object>>(json.Value);
        Assert.True(replayed.Success);
    }
}
