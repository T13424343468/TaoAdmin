using Microsoft.AspNetCore.Http;
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Web;
using Xunit;

namespace TaoAdmin.Tests.Common;

/// <summary>控制器基类：无数据 ApiOk 重载、ApiFail 与客户端 IP 的反代头/远端地址/兜底口径。</summary>
public class BaseApiControllerTests
{
    private class TestController : BaseApiController
    {
        public ApiResult<object> DoOk() => ApiOk();
        public ApiResult<string> DoOkData() => ApiOk("payload", message: "done");
        public ApiResult<string> DoFail() => ApiFail<string>("参数有误", 422);
        public string DoIp() => Ip;
    }

    private static TestController NewController(DefaultHttpContext http)
        => new() { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http } };

    [Fact]
    public void ApiOk_WithoutData_ShouldReturnSuccessWithTraceId()
    {
        var http = new DefaultHttpContext();
        var result = NewController(http).DoOk();

        Assert.True(result.Success);
        Assert.Equal(200, result.Code);
        Assert.Null(result.Data);
        Assert.Equal("success", result.Message);
        Assert.Equal(http.TraceIdentifier, result.TraceId); // TraceId 与请求上下文一致
    }

    [Fact]
    public void ApiOk_WithData_ShouldCarryPayloadAndCustomMessage()
    {
        var result = NewController(new DefaultHttpContext()).DoOkData();

        Assert.True(result.Success);
        Assert.Equal("payload", result.Data);
        Assert.Equal("done", result.Message);
    }

    [Fact]
    public void ApiFail_ShouldCarryBusinessCode_AndNoData()
    {
        var http = new DefaultHttpContext();
        var result = NewController(http).DoFail();

        Assert.False(result.Success);
        Assert.Equal(422, result.Code);
        Assert.Equal("参数有误", result.Message);
        Assert.Null(result.Data);
        Assert.Equal(http.TraceIdentifier, result.TraceId);
    }

    [Fact]
    public void Ip_XForwardedFor_ShouldTakeFirstSegmentTrimmed()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Forwarded-For"] = " 203.0.113.7 , 198.51.100.1";
        Assert.Equal("203.0.113.7", NewController(http).DoIp()); // 反代场景取首段并去空白
    }

    [Fact]
    public void Ip_NoForwardedFor_ShouldFallBackToRemoteIpAddress()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.55");
        Assert.Equal("192.0.2.55", NewController(http).DoIp());
    }

    [Fact]
    public void Ip_NoForwardedForAndNoRemoteIp_ShouldReturnEmpty()
    {
        var http = new DefaultHttpContext(); // 无反代头、无远端地址（如内存管道测试）
        Assert.Equal(string.Empty, NewController(http).DoIp()); // 兜底空串不抛出
    }

    // ==================== ClaimsPrincipalExtensions ====================

    [Fact]
    public void UserExtensions_ShouldReadIdNamePermsRoles()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("UserId", "42"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tao"),
            new System.Security.Claims.Claim(Auth.PermissionAuthorizationHandler.PermClaimType, "user:view"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "ops"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "admin"),
        }, "TestAuth"));

        Assert.Equal(42L, user.GetUserId());
        Assert.Equal("tao", user.GetUserName());
        Assert.Equal(new[] { "user:view" }, user.GetPermissions());
        Assert.Equal(new[] { "ops", "admin" }, user.GetRoles());
        Assert.True(user.IsSuperAdmin()); // 拥有 admin 角色即超管
    }

    [Fact]
    public void UserExtensions_Anonymous_ShouldReturnDefaults()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

        Assert.Equal(0L, user.GetUserId()); // 无 UserId 声明兜底 0
        Assert.Equal(string.Empty, user.GetUserName()); // 匿名兜底空串
        Assert.Empty(user.GetPermissions());
        Assert.Empty(user.GetRoles());
        Assert.False(user.IsSuperAdmin());
    }
}
