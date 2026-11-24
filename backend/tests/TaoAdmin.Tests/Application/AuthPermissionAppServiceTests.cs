using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SqlSugar;
using StackExchange.Redis;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Security;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Domain.Repositories;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 认证/权限服务补账：登录安全链路（判锁→验证码→密码→锁定记账→互踢→会话登记→租户名→强制改密）、
/// 锁屏解锁复核、当前用户、退出吊销；权限上下文三路径（超管全量/无角色空/普通角色按授权）与菜单建树。
/// Redis 用 DispatchProxy 桩记录调用；SQLite 真库实效断言。
/// </summary>
public class AuthPermissionAppServiceTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);
    private readonly FakeUserRepository _users = new();
    private readonly SecurityStub _security = new();
    private readonly LockoutStub _lockout = new();
    private readonly OnlineSessionStore _sessions = new();
    private readonly CaptchaService _captcha = new(new MemoryCache(new MemoryCacheOptions()));

    public AuthPermissionAppServiceTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"authperm_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables(typeof(SysRole), typeof(SysUserRole), typeof(SysMenu), typeof(SysApi),
            typeof(SysRoleMenu), typeof(SysRoleApi), typeof(SysTenant));
        FakeRedis.Reset();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private AuthAppService NewAuthSvc() => new(
        _db, _users, new JwtTokenService(new JwtOptions()), new RedisHelper(FakeRedis.Multiplexer),        new PermissionAppService(_db), _sessions, new DefaultPolicyStub { Policy = _policy },
        _security, _lockout, _captcha);

    private PermissionAppService NewPermSvc() => new(_db);

    private PasswordPolicy _policy = new(8, 3, 0);

    private static SysUser NewUser(long id = 1, string name = "tao", string password = "Aa@123456",
        int status = 1, long tenantId = 0, DateTime? passwordUpdatedAt = null)
        => new()
        {
            Id = id, UserName = name, Password = BCrypt.Net.BCrypt.HashPassword(password),
            NickName = name, Status = status, TenantId = tenantId,
            PasswordUpdatedAt = passwordUpdatedAt, CreatedAt = DateTime.Now
        };

    private LoginRequest Req(string name = "tao", string password = "Aa@123456") => new() { username = name, password = password };

    // ==================== LoginAsync ====================

    [Fact]
    public async Task Login_Locked_ShouldThrow423_WithoutRecordingNewFailure()
    {
        _users.Users[1] = NewUser();
        _lockout.LockedMinutes = 12;
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().LoginAsync(Req()));
        Assert.Equal(423, ex.Code);
        Assert.Contains("12", ex.Message);
        Assert.Empty(_lockout.Recorded); // 锁定期不计新失败
    }

    [Fact]
    public async Task Login_CaptchaRequired_WrongCode_ShouldThrow400_AndNotTouchLockout()
    {
        _users.Users[1] = NewUser();
        _security.Settings = new LoginSecuritySettings(5, 15, LoginCaptchaRequired: true, CaptchaAfterFails: 0, 90, false);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            NewAuthSvc().LoginAsync(new LoginRequest { username = "tao", password = "Aa@123456", captchaId = "x", captchaCode = "bad" }));
        Assert.Equal(400, ex.Code);
        Assert.Contains("验证码", ex.Message);
        Assert.Empty(_lockout.Recorded); // 验证码错误不进锁定账
    }

    [Fact]
    public async Task Login_CaptchaRequired_CorrectCode_ShouldPass()
    {
        _users.Users[1] = NewUser();
        _security.Settings = new LoginSecuritySettings(5, 15, LoginCaptchaRequired: true, CaptchaAfterFails: 0, 90, false);
        var (id, _) = _captcha.Generate();
        var code = _captcha.Peek(id)!;
        var (token, _) = await NewAuthSvc().LoginAsync(new LoginRequest { username = "tao", password = "Aa@123456", captchaId = id, captchaCode = code.ToLower() });
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task Login_CaptchaOnlyAfterFails_BelowThreshold_ShouldSkipCaptcha()
    {
        _users.Users[1] = NewUser();
        _security.Settings = new LoginSecuritySettings(5, 15, LoginCaptchaRequired: true, CaptchaAfterFails: 3, 90, false);
        _lockout.Fails["tao"] = 2; // 未达阈值，不需要验证码
        var (token, _) = await NewAuthSvc().LoginAsync(Req());
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task Login_CaptchaOnlyAfterFails_ReachedThreshold_ShouldRequireCaptcha()
    {
        _users.Users[1] = NewUser();
        _security.Settings = new LoginSecuritySettings(5, 15, LoginCaptchaRequired: true, CaptchaAfterFails: 3, 90, false);
        _lockout.Fails["tao"] = 3;
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().LoginAsync(Req()));
        Assert.Equal(400, ex.Code); // 未带验证码即拒
    }

    [Fact]
    public async Task Login_UnknownUser_ShouldRecordFailure_AndThrow400()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().LoginAsync(Req("ghost", "whatever")));
        Assert.Equal(400, ex.Code);
        Assert.Contains("ghost", _lockout.Recorded);
    }

    [Fact]
    public async Task Login_WrongPassword_ShouldRecordFailure_AndThrow400()
    {
        _users.Users[1] = NewUser();
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().LoginAsync(Req(password: "Wrong@123")));
        Assert.Equal(400, ex.Code);
        Assert.Contains("tao", _lockout.Recorded);
    }

    [Fact]
    public async Task Login_DisabledUser_ShouldThrow403()
    {
        _users.Users[1] = NewUser(status: 0);
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().LoginAsync(Req()));
        Assert.Equal(403, ex.Code);
    }

    [Fact]
    public async Task Login_Success_ShouldClearLockout_RegisterSession_StoreRefreshToken_TouchLogin()
    {
        _users.Users[1] = NewUser();
        _lockout.Fails["tao"] = 2;

        var (token, dto) = await NewAuthSvc().LoginAsync(Req(), ip: "1.2.3.4", userAgent: "UA");

        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal("tao", dto.username);
        Assert.Equal("1", dto.userId);
        Assert.Contains("tao", _lockout.Cleared); // 成功清零解锁
        Assert.Single(_sessions.List().Where(s => s.UserId == 1)); // 在线会话已登记
        Assert.Equal("1.2.3.4", _sessions.List()[0].Ip);
        Assert.Contains("refresh_token:1", FakeRedis.SetKeys); // 刷新令牌已存
        Assert.Equal(1, _users.LastTouchedId); // 登录时间已更新
        Assert.False(dto.mustChangePassword);
    }

    [Fact]
    public async Task Login_KickEnabled_ShouldRevokeOldSessions()
    {
        _users.Users[1] = NewUser();
        _security.Settings = new LoginSecuritySettings(5, 15, false, 0, 90, KickOnDuplicateLogin: true);
        _sessions.Register(new OnlineSession { SessionId = "old", UserId = 1, UserName = "tao" });

        await NewAuthSvc().LoginAsync(Req());

        Assert.True(_sessions.IsRevoked("old"));
    }

    [Fact]
    public async Task Login_KickDisabled_ShouldKeepOldSessions()
    {
        _users.Users[1] = NewUser();
        _sessions.Register(new OnlineSession { SessionId = "old", UserId = 1, UserName = "tao" });

        await NewAuthSvc().LoginAsync(Req());

        Assert.False(_sessions.IsRevoked("old"));
    }

    [Fact]
    public async Task Login_TenantName_PlatformZero_ExistingTenant_MissingTenantFallback()
    {
        _users.Users[1] = NewUser(tenantId: 0);
        _users.Users[2] = NewUser(id: 2, name: "t2", tenantId: 5);
        _users.Users[3] = NewUser(id: 3, name: "t3", tenantId: 7); // 无对应租户行
        _db.Insertable(new SysTenant { Id = 5, TenantName = "甲租户", TenantCode = "tA" }).ExecuteCommand();
        var svc = NewAuthSvc();

        var p = await svc.LoginAsync(Req());
        Assert.Equal("平台", p.User.tenantName);

        var t5 = await svc.LoginAsync(Req(name: "t2"));
        Assert.Equal("甲租户", t5.User.tenantName);

        var t7 = await svc.LoginAsync(Req(name: "t3"));
        Assert.Equal("平台", t7.User.tenantName); // 租户行缺失兜底
    }

    [Fact]
    public async Task Login_PasswordExpired_ShouldFlagMustChange()
    {
        _users.Users[1] = NewUser(passwordUpdatedAt: DateTime.Now.AddDays(-30));
        _policy = new PasswordPolicy(8, 3, ForceChangeDays: 7);
        var (_, dto) = await NewAuthSvc().LoginAsync(Req());
        Assert.True(dto.mustChangePassword);
    }

    // ==================== VerifyPasswordAsync（锁屏解锁） ====================

    [Fact]
    public async Task Verify_MissingUser_ShouldThrow401()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().VerifyPasswordAsync(999, "x"));
        Assert.Equal(401, ex.Code);
    }

    [Fact]
    public async Task Verify_Locked_ShouldThrow423()
    {
        _users.Users[1] = NewUser();
        _lockout.LockedMinutes = 5;
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().VerifyPasswordAsync(1, "Aa@123456"));
        Assert.Equal(423, ex.Code);
    }

    [Fact]
    public async Task Verify_WrongPassword_ShouldRecordFailure_AndThrow400()
    {
        _users.Users[1] = NewUser();
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().VerifyPasswordAsync(1, "nope"));
        Assert.Equal(400, ex.Code);
        Assert.Contains("tao", _lockout.Recorded);
    }

    [Fact]
    public async Task Verify_CorrectPassword_ShouldClearLockout()
    {
        _users.Users[1] = NewUser();
        await NewAuthSvc().VerifyPasswordAsync(1, "Aa@123456");
        Assert.Contains("tao", _lockout.Cleared);
    }

    // ==================== GetCurrentUserAsync ====================

    [Fact]
    public async Task GetCurrentUser_Missing_ShouldThrow401()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewAuthSvc().GetCurrentUserAsync(999));
        Assert.Equal(401, ex.Code);
    }

    [Fact]
    public async Task GetCurrentUser_ShouldCarryRolesPermissionsMenusAndAvatar()
    {
        _users.Users[1] = NewUser();
        _users.Users[1].Avatar = "/a.png";
        SeedRole(10, "mgr", userId: 1);
        SeedMenu(100, parentId: 0, perms: "m:100");
        SeedRoleMenu(10, 100);
        SeedApi(200, perms: "a:200");
        SeedRoleApi(10, 200);

        var dto = await NewAuthSvc().GetCurrentUserAsync(1);

        Assert.Equal("/a.png", dto.avatar);
        Assert.Contains("mgr", dto.roles);
        Assert.Contains("m:100", dto.permissions);
        Assert.Contains("a:200", dto.permissions);
        Assert.Single(dto.menus);
    }

    // ==================== LogoutAsync ====================

    [Fact]
    public async Task Logout_WithSession_ShouldRevoke_AndDeleteRefreshToken()
    {
        _sessions.Register(new OnlineSession { SessionId = "s1", UserId = 1, UserName = "tao" });
        await NewAuthSvc().LogoutAsync(1, "s1");
        Assert.True(_sessions.IsRevoked("s1"));
        Assert.Contains("refresh_token:1", FakeRedis.DeletedKeys);
    }

    [Fact]
    public async Task Logout_WithoutSession_ShouldOnlyDeleteRefreshToken()
    {
        _sessions.Register(new OnlineSession { SessionId = "s1", UserId = 1, UserName = "tao" });
        await NewAuthSvc().LogoutAsync(1);
        Assert.False(_sessions.IsRevoked("s1"));
        Assert.Contains("refresh_token:1", FakeRedis.DeletedKeys);
    }

    // ==================== PermissionAppService.GetUserAuthAsync ====================

    [Fact]
    public async Task GetUserAuth_SuperAdmin_ShouldGetAllEnabledMenusAndApiPerms()
    {
        SeedRole(10, "admin", userId: 1);
        SeedMenu(100, parentId: 0, perms: "menu:on", status: 1);
        SeedMenu(101, parentId: 0, perms: "menu:off", status: 0); // 停用不进权限
        SeedApi(200, perms: "api:on", status: 1);
        SeedApi(201, perms: "api:off", status: 0);

        var (roles, perms, menus) = await NewPermSvc().GetUserAuthAsync(1);

        Assert.Equal(new List<string> { "admin" }, roles);
        Assert.Contains("menu:on", perms);
        Assert.Contains("api:on", perms);
        Assert.DoesNotContain("menu:off", perms);
        Assert.DoesNotContain("api:off", perms);
        Assert.Single(menus); // 仅启用菜单
    }

    [Fact]
    public async Task GetUserAuth_NoRoles_ShouldReturnEmpty()
    {
        var (roles, perms, menus) = await NewPermSvc().GetUserAuthAsync(2);
        Assert.Empty(roles);
        Assert.Empty(perms);
        Assert.Empty(menus);
    }

    [Fact]
    public async Task GetUserAuth_NormalRole_ShouldGetGrantedMenusAndApis_WithDistinctPerms()
    {
        SeedRole(10, "mgr", userId: 3);
        SeedMenu(100, parentId: 0, perms: "m:100");
        SeedMenu(101, parentId: 0, perms: null);   // 无权限码菜单：进树不进权限
        SeedMenu(102, parentId: 0, perms: "m:102", status: 0); // 停用不进
        SeedRoleMenu(10, 100);
        SeedRoleMenu(10, 101);
        SeedRoleMenu(10, 102);
        SeedApi(200, perms: "m:100"); // 与菜单权限码重复 → 去重
        SeedRoleApi(10, 200);
        SeedApi(201, perms: "a:201");
        SeedRoleApi(99, 201); // 未授权给角色 10

        var (roles, perms, menus) = await NewPermSvc().GetUserAuthAsync(3);

        Assert.Equal(new List<string> { "mgr" }, roles);
        Assert.Single(perms); // m:100（菜单+接口去重）
        Assert.Contains("m:100", perms);
        Assert.DoesNotContain("a:201", perms); // 授权给角色 99，与本角色无关
        Assert.Equal(2, menus.Count); // 100 与 101
    }

    // ==================== BuildTree ====================

    [Fact]
    public void BuildTree_ShouldNestSort_AndTreatMissingParentAsRoot()
    {
        var dir = new SysMenu { Id = 1, ParentId = 0, MenuName = "目录", MenuType = 0, OrderNum = 1 };
        var child = new SysMenu { Id = 2, ParentId = 1, MenuName = "子菜单", MenuType = 1, OrderNum = 2 };
        var button = new SysMenu { Id = 3, ParentId = 1, MenuName = "按钮", MenuType = 2, OrderNum = 1 };
        var orphan = new SysMenu { Id = 4, ParentId = 999, MenuName = "孤儿", MenuType = 1, OrderNum = 0 };

        var tree = NewPermSvc().BuildTree(new List<SysMenu> { dir, child, button, orphan });

        Assert.Equal(2, tree.Count); // 目录 + 孤儿（父缺失回根）
        var root = tree.First(n => n.id == "1");
        Assert.Equal(2, root.children.Count);
        Assert.Equal("3", root.children[0].id); // 按排序号：按钮(1)在前
        Assert.Equal("dir", root.type);
        Assert.Equal("button", root.children[0].type);
        Assert.Null(root.parentId); // 顶级 parentId 置空
    }

    // ==================== 种子与桩 ====================

    private void SeedRole(long id, string code, long userId)
    {
        _db.Insertable(new SysRole { Id = id, RoleName = code, RoleCode = code, Status = 1 }).ExecuteCommand();
        _db.Insertable(new SysUserRole { Id = id, UserId = userId, RoleId = id }).ExecuteCommand();
    }

    private void SeedMenu(long id, long parentId, string? perms, int status = 1)
        => _db.Insertable(new SysMenu { Id = id, ParentId = parentId, MenuName = $"菜单{id}", MenuType = 1, PermsCode = perms, Status = status }).ExecuteCommand();

    private void SeedRoleMenu(long roleId, long menuId)
        => _db.Insertable(new SysRoleMenu { Id = roleId * 100 + menuId, RoleId = roleId, MenuId = menuId }).ExecuteCommand();

    private void SeedApi(long id, string? perms, int status = 1)
        => _db.Insertable(new SysApi { Id = id, Name = $"接口{id}", Path = $"/api/x{id}", HttpMethod = "GET", PermsCode = perms ?? "", Status = status }).ExecuteCommand();

    private void SeedRoleApi(long roleId, long apiId)
        => _db.Insertable(new SysRoleApi { Id = roleId * 100 + apiId, RoleId = roleId, ApiId = apiId }).ExecuteCommand();

    /// <summary>内存用户仓储替身：记录触达与更新调用。</summary>
    private class FakeUserRepository : ISysUserRepository
    {
        public readonly Dictionary<long, SysUser> Users = new();
        public long? LastTouchedId;

        public Task<SysUser?> FindByIdAsync(long id)
            => Task.FromResult(Users.TryGetValue(id, out var u) ? u : null);

        public Task<SysUser?> FindByNameAsync(string userName)
            => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));

        public Task<bool> ExistsNameAsync(string userName)
            => Task.FromResult(Users.Values.Any(u => u.UserName == userName));

        public Task AddAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateSelfProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task SoftDeleteAsync(long id) { Users.Remove(id); return Task.CompletedTask; }
        public Task TouchLoginAsync(long id) { LastTouchedId = id; return Task.CompletedTask; }
        public Task ResetPasswordAsync(long id, string passwordHash) { Users[id].Password = passwordHash; return Task.CompletedTask; }
        public Task ReplaceRolesAsync(long userId, IReadOnlyCollection<long> roleIds) => Task.CompletedTask;
        public Task DeleteRoleLinksAsync(long userId) => Task.CompletedTask;
    }

    private sealed class SecurityStub : ILoginSecurityProvider
    {
        public LoginSecuritySettings Settings = new(5, 15, LoginCaptchaRequired: false, CaptchaAfterFails: 0, 90, KickOnDuplicateLogin: false);
        public Task<LoginSecuritySettings> GetAsync() => Task.FromResult(Settings);
    }

    private sealed class LockoutStub : ILoginLockoutStore
    {
        public int? LockedMinutes;
        public readonly Dictionary<string, int> Fails = new();
        public readonly List<string> Recorded = new();
        public readonly List<string> Cleared = new();

        public int? GetRemainingLockMinutes(string username, int lockMinutes) => LockedMinutes;
        public void RecordFailure(string username, int maxFails, int lockMinutes) => Recorded.Add(username);
        public void Clear(string username) => Cleared.Add(username);
        public int GetFails(string username) => Fails.TryGetValue(username, out var f) ? f : 0;
        public void Reset() { LockedMinutes = null; Fails.Clear(); Recorded.Clear(); Cleared.Clear(); }
    }

    /// <summary>Redis 连接桩：DispatchProxy 拦截 IDatabase 调用，记录写入与删除，其余返回默认值。</summary>
    private class FakeRedis : DispatchProxy
    {
        public static readonly List<string> SetKeys = new();
        public static readonly List<string> DeletedKeys = new();
        public static void Reset() { SetKeys.Clear(); DeletedKeys.Clear(); }

        public static readonly IConnectionMultiplexer Multiplexer = CreateMultiplexer();

        private static IConnectionMultiplexer CreateMultiplexer()
            => DispatchProxy.Create<IConnectionMultiplexer, FakeRedis>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetDatabase")
                return DispatchProxy.Create<IDatabase, FakeRedis>();
            if (targetMethod?.Name == "StringSet" && args is { Length: > 0 })
            {
                SetKeys.Add(((RedisKey)args[0]!).ToString());
                return true;
            }
            if (targetMethod?.Name == "KeyDelete" && args is { Length: > 0 })
            {
                if (args[0] is RedisKey[] keys)
                {
                    DeletedKeys.AddRange(keys.Select(k => k.ToString()));
                    return (long)keys.Length;
                }
                DeletedKeys.Add(((RedisKey)args[0]!).ToString());
                return 1L;
            }
            return targetMethod!.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}

/// <summary>
/// 审计留痕补账：成功落库（类别/方法/路径/掩码/空值）、嵌套与数组掩码、失败仅警告不阻塞、
/// 掩码空串与坏 JSON 兜底（私有 Mask 经反射探针覆盖 JsonException 兜底分支）。
/// </summary>
public class AuditTrailTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;

    public AuditTrailTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"audit_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private AuditTrail NewTrail(ILogger<AuditTrail>? logger = null)
        => new(_db, new SnowflakeIdGenerator(1, 1), logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditTrail>.Instance);

    [Fact]
    public async Task WriteAsync_ShouldPersistAuditRow_WithMaskedSensitiveFields()
    {
        _db.CodeFirst.InitTables<SysOperationLog>();
        var trail = NewTrail();

        await trail.WriteAsync(7, "tao", 3, "user.update",
            before: new { name = "旧名", password = "Old@123" },
            after: new { name = "新名", password = "New@456", accessToken = "tk", clientSecret = "sc", note = "普通字段" },
            ip: "1.2.3.4");

        var log = _db.Queryable<SysOperationLog>().Single();
        Assert.Equal("audit", log.Category);
        Assert.Equal("AUDIT", log.Method);
        Assert.Equal("user.update", log.Path);
        Assert.Equal(200, log.StatusCode);
        Assert.Equal(1, log.Success);
        Assert.Equal(7, log.UserId);
        Assert.Equal("tao", log.UserName);
        Assert.Equal(3, log.TenantId);
        Assert.Equal("1.2.3.4", log.Ip);
        Assert.Contains("\\u0022password\\u0022:\\u0022***\\u0022", log.Detail);
        Assert.Contains("\\u0022accessToken\\u0022:\\u0022***\\u0022", log.Detail);
        Assert.Contains("\\u0022clientSecret\\u0022:\\u0022***\\u0022", log.Detail);
        Assert.Contains("note", log.Detail); // 非敏感键原样保留
        Assert.DoesNotContain("Old@123", log.Detail);
        Assert.DoesNotContain("New@456", log.Detail);
    }

    [Fact]
    public async Task WriteAsync_NullSides_ShouldSerializeAsNull()
    {
        _db.CodeFirst.InitTables<SysOperationLog>();
        await NewTrail().WriteAsync(1, "u", 0, "create", before: null, after: new { k = "v" });
        var detail = _db.Queryable<SysOperationLog>().Single().Detail!;
        Assert.Contains("\"before\":null", detail);
        Assert.Contains("\\u0022k\\u0022:\\u0022v\\u0022", detail);
    }

    [Fact]
    public async Task WriteAsync_BothSidesNull_ShouldSerializeBothNull()
    {
        _db.CodeFirst.InitTables<SysOperationLog>();
        await NewTrail().WriteAsync(1, "u", 0, "view", before: null, after: null);
        var detail = _db.Queryable<SysOperationLog>().Single().Detail!;
        Assert.Contains("\"before\":null", detail);
        Assert.Contains("\"after\":null", detail);
    }

    [Fact]
    public async Task WriteAsync_NestedAndArraySensitiveKeys_ShouldMaskRecursively()
    {
        _db.CodeFirst.InitTables<SysOperationLog>();
        await NewTrail().WriteAsync(1, "u", 0, "batch", before: null,
            after: new { items = new object[] { new { password = "p", keep = 1 }, new { TOKEN = "t" } } });
        var detail = _db.Queryable<SysOperationLog>().Single().Detail!;
        Assert.Contains("\\u0022password\\u0022:\\u0022***\\u0022", detail);
        Assert.Contains("\\u0022token\\u0022:\\u0022***\\u0022", detail); // Web 序列化键名转小写后仍命中掩码
        Assert.Contains("\\u0022keep\\u0022:1", detail);
    }

    [Fact]
    public async Task WriteAsync_WhenTableMissing_ShouldOnlyWarn_NotThrow()
    {
        // 不建表 → 插入必然失败：留痕失败只记警告，不阻塞业务
        var logger = new WarningLogger();
        await NewTrail(logger).WriteAsync(1, "u", 0, "op", before: null, after: new { k = 1 });
        Assert.Equal(1, logger.Warnings); // 失败仅记警告，不抛出
    }

    [Fact]
    public void Mask_EmptyString_ShouldReturnAsIs()
    {
        var result = InvokeMask("");
        Assert.Equal("", result);
    }

    [Fact]
    public void Mask_InvalidJson_ShouldReturnOriginalText()
    {
        // 探针：ToJson 产物恒为合法 JSON，坏 JSON 兜底分支只能经反射构造输入实锤
        var result = InvokeMask("{not json");
        Assert.Equal("{not json", result);
    }

    private static string InvokeMask(string input)
    {
        var method = typeof(AuditTrail).GetMethod("Mask", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Mask 方法不存在");
        return (string)method.Invoke(null, new object?[] { input })!;
    }

    private sealed class WarningLogger : ILogger<AuditTrail>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }
}
