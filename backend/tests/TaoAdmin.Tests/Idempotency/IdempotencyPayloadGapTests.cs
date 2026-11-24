using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using TaoAdmin.Common.Idempotency;
using Xunit;

namespace TaoAdmin.Tests.Idempotency;

/// <summary>
/// 幂等过滤器载荷判定矩阵收口（分支 100% 硬门禁）：
/// ContentResult 载荷被缓存并可重放；RedirectResult（默认臂）不缓存；
/// ObjectResult 空值不缓存；业务异常不缓存；结果为 null 不缓存。
/// </summary>
public class IdempotencyPayloadGapTests
{
    private static ActionExecutingContext BuildContext(string key)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/x";
        http.Request.Headers["Idempotency-Key"] = key;
        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = new List<object> { new IdempotentAttribute() }
        };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), new object());
    }

    private static ActionExecutionDelegate NextReturning(ActionExecutingContext ctx,
        IActionResult? result, Exception? exception = null)
        => new(() => Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), null!)
        {
            Result = result,
            Exception = exception
        }));

    private static string CacheKey(string key) => $"0:anon:/api/x:{key}";

    [Fact]
    public async Task ContentResult载荷被缓存并可重放()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);
        var ctx = BuildContext("k-content");

        await filter.OnActionExecutionAsync(ctx,
            NextReturning(ctx, new ContentResult { Content = "{\"code\":200}" }));

        Assert.NotNull(store.TryGet(CacheKey("k-content"))); // ContentResult 臂实效缓存
    }

    [Fact]
    public async Task RedirectResult默认臂不缓存()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);
        var ctx = BuildContext("k-redirect");

        await filter.OnActionExecutionAsync(ctx, NextReturning(ctx, new RedirectResult("/done")));

        Assert.Null(store.TryGet(CacheKey("k-redirect"))); // 非 ObjectResult/ContentResult：不缓存
    }

    [Fact]
    public async Task ObjectResult空值不缓存()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);
        var ctx = BuildContext("k-null");

        await filter.OnActionExecutionAsync(ctx, NextReturning(ctx, new ObjectResult(null)));

        Assert.Null(store.TryGet(CacheKey("k-null"))); // 载荷为 null：不缓存
    }

    [Fact]
    public async Task 业务异常不缓存()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);
        var ctx = BuildContext("k-throw");

        await filter.OnActionExecutionAsync(ctx,
            NextReturning(ctx, new ObjectResult(new { ok = true }), new InvalidOperationException("业务执行失败")));

        Assert.Null(store.TryGet(CacheKey("k-throw"))); // 异常路径：可重试，不落缓存
    }

    [Fact]
    public async Task 结果为null不缓存()
    {
        var store = new IdempotencyStore();
        var filter = new IdempotencyFilter(store);
        var ctx = BuildContext("k-noresult");

        await filter.OnActionExecutionAsync(ctx, NextReturning(ctx, result: null));

        Assert.Null(store.TryGet(CacheKey("k-noresult")));
    }
}
