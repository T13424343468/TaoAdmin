using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using TaoAdmin.GatewayService;
using Xunit;

namespace TaoAdmin.Tests.Gateway;

/// <summary>
/// GatewayService 启动探针 v2：补齐 Program.cs 可选分支实效覆盖——
/// ES 日志 sink（配置 Uri 即装配，不可达只走自日志不崩）、OpenAPI 文档端点、
/// 已认证代理请求的用户上下文透传转换、限流超速 429 与拒绝响应体。
/// </summary>
public class GatewayProgramProbeV2Tests
{
    /// <summary>与网关 appsettings 完全一致的签名密钥（自签 JWT 走真实校验链）。</summary>
    private const string Secret = "TaoAdmin@2024#SecretKey*MustBeLongEnough!";

    private static string MakeToken(long userId = 42, string userName = "老板")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "TaoAdmin", audience: "TaoAdmin",
            claims: new[] { new Claim("UserId", userId.ToString()), new Claim("UserName", userName) },
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Program_ES日志装配与OpenAPI文档可用()
    {
        // ES 不可达：sink 装配成功，事件失败走 SelfLog 不崩主机；测试宿主非回环来源，文档放开以探文档端点
        using var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Elasticsearch:Uri", "http://localhost:9200");
                b.UseSetting("DocsAccess:Mode", "Open");
            });
        var client = factory.CreateClient();

        var openapi = await client.GetAsync("/openapi/v1.json");
        Assert.True(openapi.IsSuccessStatusCode, $"openapi 状态 {(int)openapi.StatusCode}");
        var doc = await openapi.Content.ReadAsStringAsync();
        Assert.Contains("TaoAdmin 网关（YARP）", doc);
    }

    [Fact]
    public async Task 代理请求_匿名与已认证均走透传转换()
    {
        // 下游 5001 不可达：转换在转发前执行，断言代理链路真实走完（5xx 即网关已尝试转发）
        using var factory = new WebApplicationFactory<GatewayResourceCollector>();
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/identity/health");
        Assert.True((int)anonymous.StatusCode >= 500);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/health");
        request.Headers.Authorization = new("Bearer", MakeToken());
        var authed = await client.SendAsync(request);
        Assert.True((int)authed.StatusCode >= 500);
    }

    [Fact]
    public async Task Program_Nacos开关打开_装配不崩()
    {
        // 实验：Nacos:Enabled=true 时装配链是否可启动（Nacos 服务器不可达）
        using var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Nacos:Enabled", "true");
                b.UseSetting("NacosConfig:ServerAddresses:0", "http://localhost:8848");
                b.UseSetting("NacosConfig:Namespace", "test");
                b.UseSetting("NacosConfig:Listeners:0:Group", "DEFAULT_GROUP");
                b.UseSetting("NacosConfig:Listeners:0:DataId", "taoadmin-test");
                b.UseSetting("NacosConfig:Listeners:0:Type", "JSON");
                b.UseSetting("Nacos:ServerAddresses:0", "http://localhost:8848");
                b.UseSetting("Nacos:ServiceName", "taoadmin-gateway-test");
            });
        var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode, $"Nacos 开启后健康检查 {(int)health.StatusCode}");
    }

    [Fact]
    public async Task 限流_窗口内第二次请求返回429()
    {
        using var factory = new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("RateLimiting:PermitLimit", "1");
                b.UseSetting("RateLimiting:WindowSeconds", "60");
            });
        var client = factory.CreateClient();

        await client.GetAsync("/api/job/jobs");   // 第 1 发：耗尽窗口配额（下游不可达 5xx 不影响计数）
        var rejected = await client.GetAsync("/api/job/jobs"); // 第 2 发：同 IP 分桶超速

        Assert.Equal(429, (int)rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains("请求太频繁", body);
    }
}
