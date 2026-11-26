using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using TaoAdmin.GatewayService;
using Xunit;

namespace TaoAdmin.Tests.Gateway;

/// <summary>
/// GatewayService 启动探针 v3：补齐 Program.cs 剩余分支——
/// 未配置 Monitor:LogDir 的日志目录兜底、Jwt:SecretKey 缺省时的默认签名密钥兜底、
/// 已认证请求按 UserId 分桶的限流路径。精简宿主日志落运行目录 logs/，
/// 与回退目录清理测试串行（LogDirFallback 集合）。
/// </summary>
public class GatewayProgramProbeV3Tests
{
    private const string Secret = "TaoAdmin@2024#SecretKey*MustBeLongEnough!";

    private static string MakeToken(long userId = 7)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "TaoAdmin", audience: "TaoAdmin",
            claims: new[] { new Claim("UserId", userId.ToString()), new Claim("UserName", "tester") },
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string MakeTokenWithoutUserId()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "TaoAdmin", audience: "TaoAdmin",
            claims: new[] { new Claim("UserName", "tester") },
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Program_未配日志目录_回退运行目录logs()
    {
        // 网关 appsettings 本就没有 Monitor 节：不设置即走 ?? "logs" 兜底
        var cwd = Directory.GetCurrentDirectory();
        var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b => b.UseSetting("DocsAccess:Mode", "Open"));
        try
        {
            var client = factory.CreateClient();

            var health = await client.GetAsync("/health");
            Assert.True(health.IsSuccessStatusCode, $"无日志目录宿主健康检查 {(int)health.StatusCode}");
        }
        finally
        {
            // 异步释放：避免同步 Dispose 占死 xunit 同步上下文工作线程（2 核沙箱仅 2 个泵线程）
            await factory.DisposeAsync();
            // 探针日志落运行目录 logs/，测完即清，避免污染回退目录断言
            TaoAdmin.Tests.TaskService.TaskServiceProgramProbeV3Tests.TryDelete(
                Path.Combine(cwd, "logs", "gateway.jsonl"));
        }
    }

    [Fact]
    public async Task Program_SecretKey缺省_默认密钥装配可用()
    {
        // 安全加固后口径：SecretKey 缺失即 fail-fast，宿主启动必须失败（不再回退默认密钥）。
        // 注入走精简内容根：临时目录放一份只含 ReverseProxy、不含 Jwt 段的 appsettings.json，
        // 密钥缺省必然触发拦截；不用进程级环境变量（xunit 并行下会污染其他宿主探针测试，已实测踩坑）。
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tao-gw3-nojwt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        const string minimalConfig = """
            {
              "ReverseProxy": {
                "Routes": {
                  "identity": {
                    "ClusterId": "identity",
                    "Match": { "Path": "/api/identity/{**catch-all}" },
                    "Transforms": [ { "PathRemovePrefix": "/api/identity" } ]
                  }
                },
                "Clusters": {
                  "identity": {
                    "Destinations": { "d1": { "Address": "http://localhost:5001/" } }
                  }
                }
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "appsettings.json"), minimalConfig);
        var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b =>
            {
                b.UseContentRoot(tempRoot);
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-gw3-logs-{Guid.NewGuid():N}"));
            });
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                Task.Run(() => factory.CreateClient()));
        }
        finally
        {
            await factory.DisposeAsync();
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task 限流_已认证按UserId分桶_超速返回429()
    {
        var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("RateLimiting:PermitLimit", "1");
                b.UseSetting("RateLimiting:WindowSeconds", "60");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-gw3l-logs-{Guid.NewGuid():N}"));
            });
        try
        {
            var client = factory.CreateClient();

            var first = new HttpRequestMessage(HttpMethod.Get, "/api/job/jobs");
            first.Headers.Authorization = new("Bearer", MakeToken(userId: 7));
            // 读尽并释放第 1 发响应：确保限流租约确定性归还，防慢环境（覆盖率采集）下第 2 发排队等租约
            using (var resp1 = await client.SendAsync(first))
            {
                await resp1.Content.ReadAsStringAsync();
            }

            var second = new HttpRequestMessage(HttpMethod.Get, "/api/job/jobs");
            second.Headers.Authorization = new("Bearer", MakeToken(userId: 7));
            var rejected = await client.SendAsync(second); // 第 2 发：同用户桶超速

            Assert.Equal(429, (int)rejected.StatusCode);
            Assert.Contains("请求太频繁", await rejected.Content.ReadAsStringAsync());
        }
        finally
        {
            await factory.DisposeAsync(); // 异步释放，避免占死同步上下文泵线程
        }
    }

    [Fact]
    public async Task 限流_已认证但无UserId声明_回退IP分桶()
    {
        // 已认证上下文但缺 UserId 声明：FindFirst 为 null，分桶键回退 ip: 前缀
        var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("RateLimiting:PermitLimit", "1");
                b.UseSetting("RateLimiting:WindowSeconds", "60");
                b.UseSetting("Monitor:LogDir", Path.Combine(Path.GetTempPath(), $"tao-gw3n-logs-{Guid.NewGuid():N}"));
            });
        try
        {
            var client = factory.CreateClient();

            var first = new HttpRequestMessage(HttpMethod.Get, "/api/job/jobs");
            first.Headers.Authorization = new("Bearer", MakeTokenWithoutUserId());
            using (var resp1 = await client.SendAsync(first))
            {
                await resp1.Content.ReadAsStringAsync(); // 读尽响应归还限流租约
            }

            var second = new HttpRequestMessage(HttpMethod.Get, "/api/job/jobs");
            second.Headers.Authorization = new("Bearer", MakeTokenWithoutUserId());
            var rejected = await client.SendAsync(second); // 同 IP 桶第二发：超速

            Assert.Equal(429, (int)rejected.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
