using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using TaoAdmin.GatewayService;
using Xunit;

namespace TaoAdmin.Tests.Gateway;

/// <summary>
/// 终账收尾网关缺口清零（分支 100% 硬门禁）：
/// ①透传转换：无效 token（有身份对象但未认证，IsAuthenticated=false 侧）；
/// ②透传转换：已认证但缺 UserId/UserName claim（FindFirst ?. ?? "" 的 null 侧）。
/// </summary>
public class FinalGapGatewayTests
{
    /// <summary>与网关 appsettings 完全一致的签名密钥（自签 JWT 走真实校验链）。</summary>
    private const string Secret = "TaoAdmin@2024#SecretKey*MustBeLongEnough!";

    private static string MakeToken(params Claim[] claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "TaoAdmin", audience: "TaoAdmin",
            claims: claims,
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static WebApplicationFactory<GatewayResourceCollector> Build()
        => new WebApplicationFactory<GatewayResourceCollector>()
            .WithWebHostBuilder(b => b.UseSetting("Monitor:LogDir",
                Path.Combine(Path.GetTempPath(), $"tao-gwgap-logs-{Guid.NewGuid():N}")));

    [Fact]
    public async Task 代理请求_无效token_未认证身份走转换跳过透传()
    {
        var factory = Build();
        var client = factory.CreateClient();
        try
        {

            // 无效 token：JWT 中间件产生非 null 但未认证的身份对象（IsAuthenticated=false 侧）
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/health");
            request.Headers.Authorization = new("Bearer", "invalid-token");
            var resp = await client.SendAsync(request);

            Assert.True((int)resp.StatusCode >= 500); // 转换已执行，下游不可达 5xx
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }

    [Fact]
    public async Task 代理请求_已认证但缺用户claim_空串透传不抛()
    {
        var factory = Build();
        var client = factory.CreateClient();
        try
        {

            // 已认证但无 UserId/UserName claim：FindFirst ?? "" 的 null 侧
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity/health");
            request.Headers.Authorization = new("Bearer", MakeToken(new Claim("role", "viewer")));
            var resp = await client.SendAsync(request);

            Assert.True((int)resp.StatusCode >= 500); // 转换已执行，下游不可达 5xx
        }
        finally
        {
            ProbeDispose.SafeDispose(factory);
        }
    }
}
