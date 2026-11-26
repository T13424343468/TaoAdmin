using Microsoft.AspNetCore.Http;
using SqlSugar;
using TaoAdmin.Common.Auth;
using TaoAdmin.Domain.Entities;

namespace TaoAdmin.Infrastructure.Web;

/// <summary>
/// 接口级授权中间件（增强层）：请求进入后按 SysApi 表（路径+HTTP 方法）匹配当前接口，
/// 命中已登记接口时校验角色-接口授权（用户权限码须包含该接口的权限码）。
/// 接口清单带短 TTL 内存缓存（60 秒），避免每请求实时查库；新增/修改接口最迟 1 分钟生效。
/// 向后兼容：未登记的接口、匿名接口（[AllowAnonymous]）、超管均直接放行，
/// 现有权限码级校验（[Permission] 特性）不受影响——本中间件是叠加校验，两道都过才放行。
/// </summary>
public class ApiAuthMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    // key = "METHOD|path"，value = (api, cachedAt)；api 为 null 表示确认未登记（缓存空结果）
    // 实例级缓存：中间件实例与应用管线同寿，缓存效果不变；避免静态缓存跨宿主/测试互相污染
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (SysApi? Api, DateTime CachedAt)> _apiCache = new();

    public ApiAuthMiddleware(RequestDelegate next) => _next = next;

    private SysApi? FindApi(ISqlSugarClient db, string path, string method)
    {
        var key = $"{method}|{path}";
        if (_apiCache.TryGetValue(key, out var hit) && DateTime.Now - hit.CachedAt < CacheTtl)
            return hit.Api;

        var api = db.Queryable<SysApi>()
            .First(a => a.Path == path && a.HttpMethod == method && a.Status == 1 && a.IsDeleted == false);
        _apiCache[key] = (api, DateTime.Now);
        return api;
    }

    public async Task InvokeAsync(HttpContext context, ISqlSugarClient db)
    {
        var endpoint = context.GetEndpoint();
        var anonymous = endpoint?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null;

        // 只对已认证的非匿名请求做接口级校验
        if (!anonymous && context.User.Identity is { IsAuthenticated: true })
        {
            // 探针实测：DefaultHttpContext 的 Path.Value 恒非 null（空路径返回空串）
            var (path, method) = (context.Request.Path.Value!, context.Request.Method.ToUpperInvariant());
            var api = FindApi(db, path, method);
            if (api != null && !PermissionAuthorizationHandler.HasApiPermission(context.User, api.PermsCode))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = 403,
                    success = false,
                    message = "没有该接口的访问权限",
                    traceId = context.TraceIdentifier
                });
                return;
            }
        }

        await _next(context);
    }
}
