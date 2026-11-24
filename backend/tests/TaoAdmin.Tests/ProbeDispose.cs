using Microsoft.AspNetCore.Mvc.Testing;

namespace TaoAdmin.Tests;

/// <summary>
/// 探针工厂释放助手：宿主停机续延若挂在 xUnit 单并发同步上下文上，
/// 在测试线程直接 Dispose 会与停机互相等待造成全量假死。
/// 统一线程池释放并带 30 秒超时兜底，停机异常不拖垮测试进程。
/// </summary>
internal static class ProbeDispose
{
    public static void SafeDispose<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory) where TEntryPoint : class
    {
        try
        {
            Task.Run(factory.Dispose).Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // 停机异常不拖垮测试进程：宿主已尽力停止
        }
    }
}
