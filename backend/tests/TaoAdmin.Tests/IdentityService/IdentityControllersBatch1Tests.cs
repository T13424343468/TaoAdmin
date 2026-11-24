using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Domain.Repositories;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第一批）：密码策略、站点参数、个人中心、在线用户。
/// 控制器是薄封装，用真应用服务 + SQLite 内存库/真会话账本，断言 ApiResult 实效。
/// </summary>
public class IdentityControllersBatch1Tests
{
    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysSiteConfig, SysUser>();
        return db;
    }

    private static readonly SnowflakeIdGenerator Snow = new(3, 3);

    private static void SetUser(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId)
    {
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new(identity) }
        };
    }

    /// <summary>给控制器挂上 HttpContext（ApiOk 需要 TraceIdentifier）。</summary>
    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c)
        => c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new DefaultHttpContext() };

    // ---------- 密码策略 ----------

    [Fact]
    public async Task PasswordPolicy_Get_ShouldReturnDefaults_AndSave_ShouldPersist()
    {
        var db = CreateDb();
        var svc = new PasswordPolicyAppService(db, Snow, new MemoryCache(new MemoryCacheOptions()));
        var controller = new PasswordPolicyController(svc);
        AttachHttp(controller);

        var got = await controller.Get();
        Assert.True(got.Success);
        Assert.NotNull(got.Data);

        var save = await controller.Save(new PasswordPolicy(10, 3, 90));
        Assert.True(save.Success);
        Assert.Equal("保存成功", save.Message);

        var reread = await controller.Get();
        Assert.Equal(10, reread.Data!.MinLength);
        Assert.Equal(3, reread.Data.MinKinds);
        Assert.Equal(90, reread.Data.ForceChangeDays);
    }

    // ---------- 站点参数 ----------

    [Fact]
    public async Task SiteConfig_Get_ShouldReturnDefaults_AndSave_ShouldPersist()
    {
        var db = CreateDb();
        var svc = new SiteConfigAppService(db, Snow, new MemoryCache(new MemoryCacheOptions()));
        var controller = new SiteConfigController(svc);
        AttachHttp(controller);

        var got = await controller.Get();
        Assert.True(got.Success);
        Assert.NotNull(got.Data);

        var save = await controller.Save(new SiteConfigSaveRequest { siteName = "道映管理台", siteFooter = "©道映" });
        Assert.True(save.Success);
        Assert.Equal("保存成功", save.Message);

        var reread = await controller.Get();
        Assert.Equal("道映管理台", reread.Data!.siteName);
        Assert.Equal("©道映", reread.Data.siteFooter);
    }

    // ---------- 个人中心 ----------

    private sealed class FakeUserRepository : ISysUserRepository
    {
        public readonly Dictionary<long, SysUser> Users = new();
        public SysUser? LastSelfProfileUpdate;

        public Task<SysUser?> FindByIdAsync(long id) => Task.FromResult(Users.TryGetValue(id, out var u) ? u : null);
        public Task<SysUser?> FindByNameAsync(string userName) => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));
        public Task<bool> ExistsNameAsync(string userName) => Task.FromResult(Users.Values.Any(u => u.UserName == userName));
        public Task AddAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateSelfProfileAsync(SysUser user) { Users[user.Id] = user; LastSelfProfileUpdate = user; return Task.CompletedTask; }
        public Task SoftDeleteAsync(long id) { Users.Remove(id); return Task.CompletedTask; }
        public Task TouchLoginAsync(long id) => Task.CompletedTask;
        public Task ResetPasswordAsync(long id, string passwordHash) { Users[id].Password = passwordHash; return Task.CompletedTask; }
        public Task ReplaceRolesAsync(long userId, IReadOnlyCollection<long> roleIds) => Task.CompletedTask;
        public Task DeleteRoleLinksAsync(long userId) => Task.CompletedTask;
    }

    private sealed class FixedPolicy : IPasswordPolicyProvider
    {
        public Task<PasswordPolicy> GetAsync() => Task.FromResult(new PasswordPolicy(8, 2, 0));
        public string? Validate(string password) => password.Length >= 8 ? null : "密码太短";
    }

    [Fact]
    public async Task Profile_Get_ShouldReturnSelfFields()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = new SysUser { Id = 1, UserName = "tao", NickName = "老板", Status = 1, TenantId = 0 };
        var controller = new ProfileController(new ProfileAppService(repo, new OnlineSessionStore(), new FixedPolicy()));
        SetUser(controller, 1);

        var got = await controller.Get();
        Assert.True(got.Success);
        Assert.Equal("tao", got.Data!.username);
        Assert.Equal("老板", got.Data.nickname);
    }

    [Fact]
    public async Task Profile_Save_ShouldUpdateSelfProfile()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = new SysUser { Id = 1, UserName = "tao", NickName = "旧", Status = 1, TenantId = 0 };
        var controller = new ProfileController(new ProfileAppService(repo, new OnlineSessionStore(), new FixedPolicy()));
        SetUser(controller, 1);

        var result = await controller.Save(new ProfileSaveRequest { nickname = "新昵称", phone = "138", email = "a@b.c" });

        Assert.True(result.Success);
        Assert.Equal("资料已更新", result.Message);
        Assert.Equal("新昵称", repo.LastSelfProfileUpdate!.NickName);
    }

    [Fact]
    public async Task Profile_ChangePassword_ShouldSucceed_OnCorrectOldPassword()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = new SysUser
        {
            Id = 1, UserName = "tao", Status = 1, TenantId = 0,
            Password = BCrypt.Net.BCrypt.HashPassword("Old@123456")
        };
        var sessions = new OnlineSessionStore();
        var controller = new ProfileController(new ProfileAppService(repo, sessions, new FixedPolicy()));
        SetUser(controller, 1);

        var result = await controller.ChangePassword(new ChangePasswordRequest { oldPassword = "Old@123456", newPassword = "New@123456" });

        Assert.True(result.Success);
        Assert.Contains("重新登录", result.Message);
    }

    // ---------- 在线用户 ----------

    [Fact]
    public void OnlineUser_List_Kick_BatchKick_ShouldReflectSessionLedger()
    {
        var sessions = new OnlineSessionStore();
        sessions.Register(new OnlineSession { SessionId = "s1", UserId = 1, UserName = "tao" });
        sessions.Register(new OnlineSession { SessionId = "s2", UserId = 2, UserName = "lee" });
        sessions.Register(new OnlineSession { SessionId = "s3", UserId = 3, UserName = "wang" });
        var controller = new OnlineUserController(sessions);
        AttachHttp(controller);

        var list = controller.List();
        Assert.True(list.Success);
        Assert.Equal(3, list.Data!.Count);

        var kick = controller.Kick("s1");
        Assert.True(kick.Success);
        Assert.Equal("该会话已被强制下线", kick.Message);
        Assert.Equal("会话不存在或已下线", controller.Kick("s1").Message); // 二次下线

        var batch = controller.BatchKick(new BatchKickRequest { sessionIds = new List<string> { "s2", "s3", "ghost", "ghost" } });
        Assert.True(batch.Success);
        Assert.Contains("批量下线完成", batch.Message);
        Assert.Empty(sessions.List());
    }
}
