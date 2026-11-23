using System.Security.Claims;
using TaoAdmin.Common.Web;
using Xunit;

namespace TaoAdmin.Tests.Common;

/// <summary>
/// ClaimsPrincipal 扩展实效断言：UserId 缺失/坏值回退 0、UserName 空回退空串、
/// 权限码与角色集合逐项断言、超管判定（能红：漏判会返回错误值而非抛错）。
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal MakeUser(Action<List<Claim>>? setup = null, bool authenticated = true)
    {
        var claims = new List<Claim>();
        setup?.Invoke(claims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "Bearer" : null));
    }

    [Fact]
    public void GetUserId_WithValidClaim_ShouldReturnParsedId()
    {
        var user = MakeUser(c => c.Add(new("UserId", "12345")));
        Assert.Equal(12345L, user.GetUserId());
    }

    [Fact]
    public void GetUserId_MissingClaim_ShouldReturnZero()
    {
        var user = MakeUser();
        Assert.Equal(0L, user.GetUserId());
    }

    [Fact]
    public void GetUserId_BadValue_ShouldReturnZero()
    {
        // 能红：TryParse 失败必须回退 0，不能抛异常
        var user = MakeUser(c => c.Add(new("UserId", "not-a-number")));
        Assert.Equal(0L, user.GetUserId());
    }

    [Fact]
    public void GetUserName_WithClaim_ShouldReturnName()
    {
        var user = MakeUser(c => c.Add(new(ClaimTypes.Name, "alice")));
        Assert.Equal("alice", user.GetUserName());
    }

    [Fact]
    public void GetUserName_MissingClaim_ShouldReturnEmpty()
    {
        // 能红：缺失必须回退空串，不能 null
        var user = MakeUser();
        Assert.Equal(string.Empty, user.GetUserName());
    }

    [Fact]
    public void GetPermissions_ShouldReturnAllPermClaims()
    {
        var user = MakeUser(c =>
        {
            c.Add(new("perm", "sys:user:list"));
            c.Add(new("perm", "sys:role:add"));
            c.Add(new(ClaimTypes.Name, "alice"));
        });
        var perms = user.GetPermissions().ToList();
        Assert.Equal(2, perms.Count);
        Assert.Contains("sys:user:list", perms);
        Assert.Contains("sys:role:add", perms);
    }

    [Fact]
    public void GetPermissions_Empty_ShouldReturnEmptyNotThrow()
    {
        var user = MakeUser();
        Assert.Empty(user.GetPermissions());
    }

    [Fact]
    public void GetRoles_ShouldReturnAllRoleClaims()
    {
        var user = MakeUser(c =>
        {
            c.Add(new(ClaimTypes.Role, "admin"));
            c.Add(new(ClaimTypes.Role, "ops"));
        });
        var roles = user.GetRoles();
        Assert.Equal(2, roles.Count);
        Assert.Contains("admin", roles);
        Assert.Contains("ops", roles);
    }

    [Fact]
    public void IsSuperAdmin_WithAdminRole_ShouldReturnTrue()
    {
        var user = MakeUser(c => c.Add(new(ClaimTypes.Role, "admin")));
        Assert.True(user.IsSuperAdmin());
    }

    [Fact]
    public void IsSuperAdmin_WithOtherRole_ShouldReturnFalse()
    {
        // 能红：非 admin 角色不得误判超管
        var user = MakeUser(c => c.Add(new(ClaimTypes.Role, "viewer")));
        Assert.False(user.IsSuperAdmin());
    }

    [Fact]
    public void IsSuperAdmin_NoRoles_ShouldReturnFalse()
    {
        var user = MakeUser();
        Assert.False(user.IsSuperAdmin());
    }

    [Fact]
    public void GetUserName_IdentityNull_ShouldReturnEmpty()
    {
        // 能红：ClaimsPrincipal 无参构造时 Identity 为 null，必须回退空串不能 NullReference
        var user = new ClaimsPrincipal();
        Assert.Equal(string.Empty, user.GetUserName());
    }
}
