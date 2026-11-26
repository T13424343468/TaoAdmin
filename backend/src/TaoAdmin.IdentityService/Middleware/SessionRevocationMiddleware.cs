using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Http;

namespace TaoAdmin.IdentityService.Middleware;

/// <summary>
/// 会话吊销校验中间件：通用实现已上移 TaoAdmin.Common.Auth（与 TaskService 共用），
/// 本类保留类型与命名空间兼容既有引用；吊销状态经 Redis 镜像跨服务共享。
/// </summary>
public class SessionRevocationMiddleware(RequestDelegate next)
    : global::TaoAdmin.Common.Auth.SessionRevocationMiddleware(next)
{
}
