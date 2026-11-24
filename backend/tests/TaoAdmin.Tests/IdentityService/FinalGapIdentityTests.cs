using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.IdentityService;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 终账收尾身份服务缺口清零（分支 100% 硬门禁）：
/// Program.cs 告警评估器 DI 注册的通知委托真实调用——
/// 从真实宿主解析评估器并对 DOWN 服务评估，触发注册的 NotifyAsync lambda（行覆盖收口）。
/// </summary>
public class FinalGapIdentityTests
{
    [Fact]
    public async Task DI评估器_DOWN服务_真实触发通知委托()
    {
        var factory = new WebApplicationFactory<IdentityServiceProbe>()
            .WithWebHostBuilder(b =>
            {
                var runId = Guid.NewGuid().ToString("N");
                b.UseSetting("Database:Provider", "SQLite");
                b.UseSetting("ConnectionStrings:MySql", $"DataSource={Path.GetTempPath()}/tao-idgap-{runId}.db");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-idgap-logs-{runId}"));
                b.UseSetting("DocsAccess:Mode", "Open");
            });
        try
        {

            using var scope = factory.Services.CreateScope();
            var evaluator = scope.ServiceProvider.GetRequiredService<ServiceDownAlertEvaluator>();
            Assert.NotNull(evaluator);

            // DOWN 服务：评估器调用 DI 注册的通知委托（Program.cs 注册的 NotifyAsync lambda 真实执行）
            await evaluator.EvaluateAsync(new[]
            {
                new ServiceItemDto { serviceName = "identity", displayName = "权限服务", status = "DOWN" },
                new ServiceItemDto { serviceName = "task", displayName = "任务服务", status = "UP" }
            });

            Assert.Single(evaluator.DownCounts); // DOWN 累计计数，UP 清零不误伤
            Assert.Equal(1, evaluator.DownCounts["identity"]);
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }
}
