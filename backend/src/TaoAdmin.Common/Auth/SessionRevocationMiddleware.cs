using Microsoft.AspNetCore.Http;
using TaoAdmin.Common.Http;

namespace TaoAdmin.Common.Auth;

/// <summary>
/// 会话吊销校验中间件（通用版，IdentityService 与 TaskService 共用）：
/// 强制下线后令牌即刻失效。JWT 本身无状态，强制下线由在线会话仓库登记吊销；
/// 本中间件在认证后校验当前令牌 jti 是否已被吊销，命中即 401（前端统一拦截跳登录页）。
/// 吊销状态经 OnlineSessionStore 的 Redis 镜像跨服务共享，TaskService 接入同一中间件后
/// 被强制下线的令牌在所有服务即刻失效。
/// </summary>
public class SessionRevocationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OnlineSessionStore sessions)
    {
        var isAuthed = context.User.Identity?.IsAuthenticated == true;
        var jti = context.User.FindFirst("jti")?.Value;
        if (isAuthed && jti != null)
        {
            if (sessions.IsRevoked(jti))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new ApiResult<object>
                {
                    Code = 401,
                    Success = false,
                    Message = "该会话已被强制下线",
                    TraceId = context.TraceIdentifier
                });
                return;
            }
            // 活跃续期：先续再放行，保证在线名单的最近活跃时间可用
            sessions.Touch(jti);
        }
        await next(context);
    }
}
