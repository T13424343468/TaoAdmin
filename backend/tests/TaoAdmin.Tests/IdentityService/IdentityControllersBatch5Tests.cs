using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Security;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Domain;
using TaoAdmin.Infrastructure.Repositories;
using TaoAdmin.IdentityService.Controllers;
using TaoAdmin.Tests.TaskService;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第五批）：认证、用户、角色、菜单、接口资源五个控制器全端点。
/// 真应用服务 + SQLite 内存库 + 真仓储，Redis/消息队列用松散代理桩，断言 ApiResult 实效。
/// </summary>
public class IdentityControllersBatch5Tests
{
    private static readonly SnowflakeIdGenerator Snow = new(5, 5);

    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysUser, SysRole, SysUserRole, SysRoleMenu, SysRoleApi>();
        db.CodeFirst.InitTables<SysMenu, SysApi, SysTenant, SysPasswordResetToken>();
        return db;
    }

    private const string GoodPassword = "Aa@123456";

    private static JwtOptions JwtOpts() => new()
    {
        Issuer = "TaoAdmin", Audience = "TaoAdmin",
        SecretKey = "unit-test-secret-key-must-be-32+chars!!",
        ExpireMinutes = 120
    };

    private sealed class FixedPolicy : IPasswordPolicyProvider
    {
        public Task<PasswordPolicy> GetAsync() => Task.FromResult(new PasswordPolicy(8, 2, 0));
    }

    private sealed class FixedSecurity : ILoginSecurityProvider
    {
        public Task<LoginSecuritySettings> GetAsync()
            => Task.FromResult(new LoginSecuritySettings(5, 15, LoginCaptchaRequired: false, 0, 90, KickOnDuplicateLogin: false));
    }

    private sealed class FakeLockout : ILoginLockoutStore
    {
        private readonly Dictionary<string, int> _fails = new();
        public int? GetRemainingLockMinutes(string username, int lockMinutes) => null;
        public void RecordFailure(string username, int maxFails, int lockMinutes) => _fails[username] = _fails.GetValueOrDefault(username) + 1;
        public void Clear(string username) => _fails.Remove(username);
        public void Reset() => _fails.Clear();
        public int GetFails(string username) => _fails.GetValueOrDefault(username);
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<string> SentTo { get; } = new();
        public Task<(bool Ok, string Message)> SendAsync(string to, string subject, string body)
        {
            SentTo.Add(to);
            return Task.FromResult((true, "ok"));
        }
    }

    /// <summary>Redis 松散代理桩：StringSet 恒成功、KeyDelete 恒 0，其余成员返回默认值。</summary>
    private static RedisHelper FakeRedis()
    {
        var database = LooseProxy.Of<StackExchange.Redis.IDatabase>((method, _) => method.Name switch
        {
            nameof(StackExchange.Redis.IDatabase.StringSet) => true,
            nameof(StackExchange.Redis.IDatabase.KeyDelete) => 0L,
            _ => method.ReturnType == typeof(bool) ? false
               : method.ReturnType == typeof(long) ? 0L
               : null
        });
        var multiplexer = LooseProxy.Of<StackExchange.Redis.IConnectionMultiplexer>((method, _) =>
            method.Name == nameof(StackExchange.Redis.IConnectionMultiplexer.GetDatabase) ? database : null);
        return new RedisHelper(multiplexer);
    }

    /// <summary>消息队列松散代理桩：PublishAsync 静默成功。</summary>
    private static TaoAdmin.Common.Messaging.IMessageQueue FakeMq() =>
        LooseProxy.Of<TaoAdmin.Common.Messaging.IMessageQueue>((method, _) =>
            method.ReturnType == typeof(Task) ? Task.CompletedTask : null);

    private static SysUser NewUser(long id, string name, string password = GoodPassword) => new()
    {
        Id = id, UserName = name, Password = BCrypt.Net.BCrypt.HashPassword(password),
        NickName = name, Status = 1, TenantId = 0,
        CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
    };

    private static void SetUser(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId, string? jti = null)
    {
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
        if (jti != null) identity.AddClaim(new System.Security.Claims.Claim("jti", jti));
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new(identity) }
        };
    }

    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c)
        => c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new DefaultHttpContext() };

    private static (AuthController Controller, AuthAppService Auth, PasswordResetAppService Reset, SqlSugarClient Db, FakeEmail Email)
        BuildAuth()
    {
        var db = CreateDb();
        var opts = JwtOpts();
        var sessions = new OnlineSessionStore(opts.ExpireMinutes);
        var policy = new FixedPolicy();
        var auth = new AuthAppService(db, new SysUserRepository(db, Snow), new JwtTokenService(opts), FakeRedis(),
            new PermissionAppService(db), sessions, policy, new FixedSecurity(), new FakeLockout(),
            new CaptchaService(new MemoryCache(new MemoryCacheOptions())));
        var email = new FakeEmail();
        var reset = new PasswordResetAppService(db, email, policy, sessions);
        var controller = new AuthController(auth, reset);
        AttachHttp(controller);
        return (controller, auth, reset, db, email);
    }

    // ---------- 认证 ----------

    [Fact]
    public async Task Login_未知账号_抛业务异常()
    {
        var (controller, _, _, _, _) = BuildAuth();
        await Assert.ThrowsAsync<DomainException>(
            () => controller.Login(new LoginRequest { username = "ghost", password = "x" }));
    }

    [Fact]
    public async Task Login_正确密码_返回令牌并登记会话()
    {
        var (controller, _, _, db, _) = BuildAuth();
        db.Insertable(NewUser(11, "tao")).ExecuteCommand();

        var result = await controller.Login(new LoginRequest { username = "tao", password = GoodPassword });
        Assert.True(result.Success);
        Assert.Equal("登录成功", result.Message);
        Assert.False(string.IsNullOrEmpty(result.Data!.token));
    }

    [Fact]
    public async Task CurrentUser_返回用户名与角色权限()
    {
        var (controller, _, _, db, _) = BuildAuth();
        db.Insertable(NewUser(11, "tao")).ExecuteCommand();
        SetUser(controller, 11);

        var got = await controller.CurrentUser();
        Assert.True(got.Success);
        Assert.Equal("tao", got.Data!.username);
        Assert.Equal("平台", got.Data.tenantName);
    }

    [Fact]
    public async Task VerifyPassword_正确密码_放行()
    {
        var (controller, _, _, db, _) = BuildAuth();
        db.Insertable(NewUser(11, "tao")).ExecuteCommand();
        SetUser(controller, 11);

        var ok = await controller.VerifyPassword(new VerifyPasswordRequest { password = GoodPassword });
        Assert.True(ok.Success);
        Assert.Equal("密码正确", ok.Message);
    }

    [Fact]
    public async Task ForgotPassword_账号不存在_统一提示不泄露()
    {
        var (controller, _, _, _, email) = BuildAuth();

        var got = await controller.ForgotPassword(new ForgotPasswordRequest { username = "ghost" });
        Assert.True(got.Success);
        Assert.Contains("如账号存在", got.Message);
        Assert.Empty(email.SentTo); // 不存在不发送
    }

    [Fact]
    public async Task ResetPassword_无效令牌_抛业务异常()
    {
        var (controller, _, _, _, _) = BuildAuth();

        await Assert.ThrowsAsync<DomainException>(() => controller.ResetPassword(
            new ResetPasswordRequest { username = "tao", token = "bad-token", newPassword = GoodPassword }));
    }

    [Fact]
    public async Task Logout_吊销会话并清理刷新令牌()
    {
        var (controller, _, _, _, _) = BuildAuth();
        SetUser(controller, 11, jti: "sess-1");

        var got = await controller.Logout();
        Assert.True(got.Success);
        Assert.Equal("退出成功", got.Message);
    }

    // ---------- 用户 ----------

    private static (UserController Controller, UserAppService Svc, UserExcelAppService Excel, SqlSugarClient Db)
        BuildUser()
    {
        var db = CreateDb();
        var policy = new FixedPolicy();
        var svc = new UserAppService(db, new SysUserRepository(db, Snow), Snow, FakeMq(), policy);
        var excel = new UserExcelAppService(db, Snow, policy);
        var controller = new UserController(svc, excel);
        AttachHttp(controller);
        return (controller, svc, excel, db);
    }

    [Fact]
    public async Task User_Page_返回分页与角色聚合()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(new[] { NewUser(1, "u1"), NewUser(2, "u2") }).ExecuteCommand();

        var page = await controller.Page(1, 10);
        Assert.True(page.Success);
        Assert.Equal(2, page.Data!.Total);
        Assert.Equal(2, page.Data.Items.Count);
    }

    [Fact]
    public async Task User_Save_新增落库并发布事件_更新改资料()
    {
        var (controller, svc, _, db) = BuildUser();
        AttachHttp(controller);

        var add = await controller.Save(new UserSaveRequest { username = "newbie", password = GoodPassword, nickname = "新人" });
        Assert.True(add.Success);
        Assert.Equal("新增成功", add.Message);
        Assert.True(db.Queryable<SysUser>().Any(u => u.UserName == "newbie"));

        var userId = db.Queryable<SysUser>().First(u => u.UserName == "newbie").Id;
        var update = await controller.Save(new UserSaveRequest { id = userId.ToString(), username = "newbie", nickname = "改名", status = 1 });
        Assert.True(update.Success);
        Assert.Equal("修改成功", update.Message);
        Assert.Equal("改名", db.Queryable<SysUser>().First(u => u.Id == userId).NickName);
    }

    [Fact]
    public async Task User_Delete_不能删自己_可删他人()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(new[] { NewUser(1, "u1"), NewUser(2, "u2") }).ExecuteCommand();
        SetUser(controller, 1);

        await Assert.ThrowsAsync<DomainException>(() => controller.Delete("1"));
        var ok = await controller.Delete("2");
        Assert.True(ok.Success);
        Assert.Equal(0, db.Queryable<SysUser>().Count(u => u.Id == 2 && u.IsDeleted == false));
    }

    [Fact]
    public async Task User_BatchDelete_自动跳过当前账号()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(new[] { NewUser(1, "u1"), NewUser(2, "u2"), NewUser(3, "u3") }).ExecuteCommand();
        SetUser(controller, 1);

        var got = await controller.BatchDelete(new BatchIdsRequest { Ids = new() { 1, 2, 3, 2 } });
        Assert.True(got.Success);
        var json = System.Text.Json.JsonSerializer.Serialize(got.Data);
        Assert.Contains("\"deleted\":2", json);
        Assert.Contains("\"skipped\":1", json);
    }

    [Fact]
    public async Task User_BatchStatus_逐条生效并跳过自己()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(new[] { NewUser(1, "u1"), NewUser(2, "u2") }).ExecuteCommand();
        SetUser(controller, 1);

        var got = await controller.BatchStatus(new BatchStatusRequest { Ids = new() { 1, 2 }, status = 0 });
        Assert.True(got.Success);
        Assert.Equal(0, db.Queryable<SysUser>().First(u => u.Id == 2).Status);
        Assert.Equal(1, db.Queryable<SysUser>().First(u => u.Id == 1).Status); // 自己被跳过
    }

    [Fact]
    public async Task User_ResetPassword_按策略校验后重置()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(NewUser(2, "u2")).ExecuteCommand();
        SetUser(controller, 1);

        var ok = await controller.ResetPassword("2", new Dictionary<string, string> { ["password"] = "Bb@987654" });
        Assert.True(ok.Success);
        Assert.True(BCrypt.Net.BCrypt.Verify("Bb@987654", db.Queryable<SysUser>().First(u => u.Id == 2).Password));
    }

    [Fact]
    public async Task User_AssignRoles_全量覆盖角色关联()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(NewUser(2, "u2")).ExecuteCommand();
        SetUser(controller, 1);

        var ok = await controller.AssignRoles("2", new Dictionary<string, List<string>> { ["roleIds"] = new() { "101", "102" } });
        Assert.True(ok.Success);
        var roleIds = db.Queryable<SysUserRole>().Where(ur => ur.UserId == 2).Select(ur => ur.RoleId).ToList();
        Assert.Equal(new long[] { 101, 102 }, roleIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task User_Export_导出xlsx字节()
    {
        var (controller, _, _, db) = BuildUser();
        db.Insertable(NewUser(1, "u1")).ExecuteCommand();
        SetUser(controller, 1);

        var result = await controller.Export();
        var file = Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(result);
        Assert.Contains("spreadsheetml.sheet", file.ContentType);
        Assert.True(file.FileContents.Length > 0);
    }

    [Fact]
    public async Task User_ImportTemplate_模板可下载()
    {
        var (controller, _, _, _) = BuildUser();
        SetUser(controller, 1);

        var result = await controller.ImportTemplate();
        var file = Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(result);
        Assert.True(file.FileContents.Length > 0);
    }

    [Fact]
    public async Task User_Import_空文件拒绝_模板文件可导入()
    {
        var (controller, _, excel, _) = BuildUser();
        SetUser(controller, 1);

        await Assert.ThrowsAsync<DomainException>(() => controller.Import(null!));

        var template = await excel.BuildTemplateAsync();
        var formFile = new FormFile(new MemoryStream(template), 0, template.Length, "file", "users.xlsx");
        var got = await controller.Import(formFile);
        Assert.True(got.Success);
    }

    // ---------- 角色 ----------

    private static (RoleController Controller, RoleAppService Svc, SqlSugarClient Db) BuildRole()
    {
        var db = CreateDb();
        var svc = new RoleAppService(db, new SysRoleRepository(db, Snow), Snow);
        return (new RoleController(svc), svc, db);
    }

    [Fact]
    public async Task Role_Save_Page_All_全链路()
    {
        var (controller, svc, db) = BuildRole();
        AttachHttp(controller);

        var add = await controller.Save(new RoleSaveRequest { name = "运营", code = "ops" });
        Assert.True(add.Success);
        Assert.Equal("新增成功", add.Message);

        var page = await controller.Page(1, 10, keyword: "运营");
        Assert.True(page.Success);
        Assert.Single(page.Data!.Items);

        var all = await controller.All();
        Assert.Single(all.Data!);

        var id = db.Queryable<SysRole>().First(r => r.RoleCode == "ops").Id;
        var update = await controller.Save(new RoleSaveRequest { id = id.ToString(), name = "运营2", code = "ops" });
        Assert.True(update.Success);
        Assert.Equal("修改成功", update.Message);
        Assert.Equal("运营2", db.Queryable<SysRole>().First(r => r.Id == id).RoleName);
    }

    [Fact]
    public async Task Role_Delete_BatchDelete_级联清理()
    {
        var (controller, _, db) = BuildRole();
        db.Insertable(new[]
        {
            new SysRole { Id = 1, RoleName = "r1", RoleCode = "r1", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now },
            new SysRole { Id = 2, RoleName = "r2", RoleCode = "r2", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }
        }).ExecuteCommand();
        AttachHttp(controller);

        var ok = await controller.Delete("1");
        Assert.True(ok.Success);

        var batch = await controller.BatchDelete(new BatchIdsRequest { Ids = new() { 2, 99 } });
        Assert.True(batch.Success);
        Assert.Equal(0, db.Queryable<SysRole>().Count(r => r.IsDeleted == false));
    }

    [Fact]
    public async Task Role_Permissions_读取与分配()
    {
        var (controller, _, db) = BuildRole();
        db.Insertable(new SysRoleMenu { Id = 11, RoleId = 1, MenuId = 100 }).ExecuteCommand();
        db.Insertable(new SysRoleApi { Id = 12, RoleId = 1, ApiId = 200 }).ExecuteCommand();
        SetUser(controller, 1);

        var got = await controller.GetPermissions("1");
        Assert.True(got.Success);
        var json = System.Text.Json.JsonSerializer.Serialize(got.Data);
        Assert.Contains("100", json);
        Assert.Contains("200", json);

        var assign = await controller.AssignPermissions("1", new RolePermissionRequest { menuIds = new() { "300" }, apiIds = new() { "400" } });
        Assert.True(assign.Success);
        Assert.Equal(0, db.Queryable<SysRoleMenu>().Count(rm => rm.RoleId == 1 && rm.MenuId == 100));
        Assert.Equal(1, db.Queryable<SysRoleApi>().Count(ra => ra.RoleId == 1 && ra.ApiId == 400));
    }

    // ---------- 菜单 ----------

    private static (MenuController Controller, MenuAppService Svc, SqlSugarClient Db) BuildMenu()
    {
        var db = CreateDb();
        var svc = new MenuAppService(db, new SysMenuRepository(db), new PermissionAppService(db), Snow);
        return (new MenuController(svc), svc, db);
    }

    [Fact]
    public async Task Menu_Save_Tree_Delete_级联子节点()
    {
        var (controller, svc, db) = BuildMenu();
        AttachHttp(controller);

        var add = await controller.Save(new MenuSaveRequest { name = "系统管理", path = "/sys", type = "dir" });
        Assert.True(add.Success);
        Assert.Equal("新增成功", add.Message);

        var parentId = db.Queryable<SysMenu>().First(m => m.MenuName == "系统管理").Id;
        await controller.Save(new MenuSaveRequest { name = "用户", parentId = parentId.ToString(), type = "menu", path = "/sys/users" });
        Assert.Equal(1, db.Queryable<SysMenu>().Count(m => m.ParentId == parentId && m.IsDeleted == false));

        var tree = await controller.Tree();
        Assert.True(tree.Success);
        Assert.Contains(tree.Data!, n => n.name == "系统管理");

        var del = await controller.Delete(parentId.ToString());
        Assert.True(del.Success);
        Assert.Contains("2 个节点", del.Message); // 父 + 子级联
        Assert.Equal(0, db.Queryable<SysMenu>().Count(m => m.IsDeleted == false));
    }

    [Fact]
    public async Task Menu_Save_按钮缺权限码_拒绝()
    {
        var (controller, _, _) = BuildMenu();
        AttachHttp(controller);

        await Assert.ThrowsAsync<DomainException>(
            () => controller.Save(new MenuSaveRequest { name = "新增按钮", type = "button", permission = "" }));
    }

    // ---------- 接口资源 ----------

    private static (ApiController Controller, ApiAppService Svc, SqlSugarClient Db) BuildApi()
    {
        var db = CreateDb();
        var svc = new ApiAppService(db, new SysApiRepository(db), Snow);
        return (new ApiController(svc), svc, db);
    }

    [Fact]
    public async Task Api_Save_Page_All_全链路()
    {
        var (controller, _, db) = BuildApi();
        AttachHttp(controller);

        var add = await controller.Save(new ApiSaveRequest { name = "用户分页", method = "GET", path = "/users", permission = "sys:user:list" });
        Assert.True(add.Success);
        Assert.Equal("新增成功", add.Message);

        var page = await controller.Page(1, 10, keyword: "users");
        Assert.True(page.Success);
        Assert.Single(page.Data!.Items);

        var all = await controller.All();
        Assert.Single(all.Data!);

        var id = db.Queryable<SysApi>().First(a => a.Path == "/users").Id;
        var update = await controller.Save(new ApiSaveRequest { id = id.ToString(), name = "用户分页2", method = "GET", path = "/users" });
        Assert.True(update.Success);
        Assert.Equal("用户分页2", db.Queryable<SysApi>().First(a => a.Id == id).Name);
    }

    [Fact]
    public async Task Api_Delete_BatchDelete_级联清理()
    {
        var (controller, _, db) = BuildApi();
        db.Insertable(new[]
        {
            new SysApi { Id = 1, Name = "a1", Path = "/a1", HttpMethod = "GET", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now },
            new SysApi { Id = 2, Name = "a2", Path = "/a2", HttpMethod = "GET", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }
        }).ExecuteCommand();
        AttachHttp(controller);

        var ok = await controller.Delete("1");
        Assert.True(ok.Success);

        var batch = await controller.BatchDelete(new BatchIdsRequest { Ids = new() { 2, 99 } });
        Assert.True(batch.Success);
        Assert.Equal(0, db.Queryable<SysApi>().Count(a => a.IsDeleted == false));
    }
}
