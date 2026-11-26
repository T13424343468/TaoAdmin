using System.Collections.Concurrent;
using System.Text.Json;
using TaoAdmin.Common.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TaoAdmin.Common.Idempotency;

/// <summary>
/// 幂等标记：标注在需要防重复提交的接口上。
/// 客户端请求头携带 Idempotency-Key（任意唯一值，如 UUID），同一 Key 在 TTL 内重复请求
/// 直接返回第一次的结果，不重复执行业务（防连点/重试导致重复下单、重复提交）。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class IdempotentAttribute : Attribute
{
    /// <summary>幂等保留时长（秒），默认 600 秒</summary>
    public int TtlSeconds { get; set; } = 600;
}

/// <summary>
/// 幂等过滤器（全局注册，仅对标注 [Idempotent] 的接口生效）。
/// 幂等键 = 租户 + 用户 + 请求路径 + Idempotency-Key。
/// 【本地】默认内存实现；【线上】多实例部署建议把 IdempotencyStore 换成 Redis 实现（接口已抽象）。
/// </summary>
public class IdempotencyFilter : IAsyncActionFilter
{
    private readonly IdempotencyStore _store;

    public IdempotencyFilter(IdempotencyStore store) => _store = store;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var marker = context.ActionDescriptor.EndpointMetadata.OfType<IdempotentAttribute>().FirstOrDefault();
        if (marker == null)
        {
            await next();
            return;
        }

        var http = context.HttpContext;
        var key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key))
        {
            context.Result = new JsonResult(new ApiResult<object> { Code = 400, Success = false, Message = "该接口要求携带 Idempotency-Key 请求头防重复提交" });
            return;
        }

        var cacheKey = $"{http.User.FindFirst("tenant_id")?.Value ?? "0"}:{http.User.FindFirst("UserId")?.Value ?? "anon"}:{http.Request.Path}:{key}";
        var cached = _store.TryGet(cacheKey);
        if (cached != null)
        {
            // 命中幂等缓存：直接返回首次结果，不重复执行
            var result = JsonSerializer.Deserialize<ApiResult<object>>(cached);
            http.Response.Headers["X-Idempotent-Replayed"] = "true";
            context.Result = new JsonResult(result);
            return;
        }
        if (_store.IsInFlight(cacheKey))
        {
            // 同 Key 并发请求：首次结果尚未落缓存，直接拒绝重复执行（防重也防并发）
            context.Result = new JsonResult(new ApiResult<object> { Code = 429, Success = false, Message = "相同请求正在处理中，请勿重复提交" });
            return;
        }
        _store.Begin(cacheKey, TimeSpan.FromSeconds(marker.TtlSeconds));

        try
        {
            var executed = await next();
            if (executed.Exception == null && executed.Result != null)
            {
                var payload = executed.Result switch
                {
                    ObjectResult o => o.Value,
                    ContentResult c => c.Content,
                    _ => (object?)null
                };
                if (payload != null)
                {
                    var body = JsonSerializer.Serialize(payload);
                    _store.Save(cacheKey, body, TimeSpan.FromSeconds(marker.TtlSeconds));
                }
            }
        }
        finally
        {
            _store.End(cacheKey);
        }
    }
}

/// <summary>幂等缓存存取（内存实现；多实例部署可替换为 Redis 实现）</summary>
public class IdempotencyStore
{
    private class Entry
    {
        public string Body = "";
        public DateTime ExpireAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, DateTime> _inFlight = new();
    // 过期清理节奏：每 200 次写入顺带清一次过期项，避免不同 Key 持续增长不回收
    private int _writeCounter;

    public string? TryGet(string key)
    {
        if (!_cache.TryGetValue(key, out var entry)) return null;
        if (DateTime.Now > entry.ExpireAt)
        {
            _cache.TryRemove(key, out _);
            return null;
        }
        return entry.Body;
    }

    /// <summary>同 Key 并发防重：标记执行中（TTL 内有效），并发重复请求据此拒绝</summary>
    public bool IsInFlight(string key)
        => _inFlight.TryGetValue(key, out var started) && DateTime.Now - started < TimeSpan.FromMinutes(5);

    public void Begin(string key, TimeSpan ttl)
        => _inFlight[key] = DateTime.Now;

    public void End(string key)
        => _inFlight.TryRemove(key, out _);

    public void Save(string key, string body, TimeSpan ttl)
    {
        _cache[key] = new Entry { Body = body, ExpireAt = DateTime.Now.Add(ttl) };
        if (++_writeCounter % 200 == 0) PruneExpired();
    }

    /// <summary>顺带清理过期条目（随写入节奏执行，无需后台线程）</summary>
    private void PruneExpired()
    {
        var now = DateTime.Now;
        foreach (var kv in _cache)
        {
            if (now > kv.Value.ExpireAt)
                _cache.TryRemove(kv.Key, out _);
        }
        foreach (var kv in _inFlight)
        {
            if (now - kv.Value > TimeSpan.FromMinutes(5))
                _inFlight.TryRemove(kv.Key, out _);
        }
    }
}
