using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Domain.Repositories;
using TaoAdmin.Infrastructure.Repositories;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 操作日志三件套补账：分页五类筛选与钳制、游标编解码与坏游标兜底、批量物理删除；
/// 清理服务保留天数边界与满批续跑；后台任务真实调度（毫秒级注入）覆盖首跑/跳过/异常重试。
/// SQLite 真库实效断言。
/// </summary>
public class OperationLogAppServiceTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public OperationLogAppServiceTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"oplog_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables(typeof(SysOperationLog), typeof(SysSiteConfig));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private OperationLogAppService NewSvc() => new(_db, _cache);

    private long SeedLog(string category = "operation", string userName = "tao", int success = 1,
        DateTime? createdAt = null, string path = "/api/x")
    {
        var log = new SysOperationLog
        {
            Id = new SnowflakeIdGenerator(1, 1).NextId(),
            UserId = 1, UserName = userName, Category = category, Method = "POST",
            Path = path, StatusCode = 200, Success = success, CreatedAt = createdAt ?? DateTime.Now
        };
        _db.Insertable(log).ExecuteCommand();
        return log.Id;
    }

    // ==================== PageAsync ====================

    [Fact]
    public async Task Page_Filters_ShouldApplyAllFiveConditions()
    {
        SeedLog(category: "login", userName: "tao", success: 1, createdAt: DateTime.Now.AddHours(-3));
        SeedLog(category: "operation", userName: "lee", success: 0, createdAt: DateTime.Now.AddHours(-2));
        SeedLog(category: "operation", userName: "tao", success: 0, createdAt: DateTime.Now.AddHours(-1));
        var svc = NewSvc();

        var byCategory = await svc.PageAsync(1, 10, category: "operation", null, null, null, null);
        Assert.Equal(2, byCategory.Total);

        var byUser = await svc.PageAsync(1, 10, null, userName: "ta", null, null, null);
        Assert.Equal(2, byUser.Total); // 模糊命中 tao 两条

        var bySuccess = await svc.PageAsync(1, 10, null, null, success: 0, null, null);
        Assert.Equal(2, bySuccess.Total);

        var byBegin = await svc.PageAsync(1, 10, null, null, null, begin: DateTime.Now.AddMinutes(-90), null);
        Assert.Equal(1, byBegin.Total); // 仅近 90 分钟内一条

        var byEnd = await svc.PageAsync(1, 10, null, null, null, null, end: DateTime.Now.AddMinutes(-90));
        Assert.Equal(2, byEnd.Total); // 90 分钟前两条
    }

    [Fact]
    public async Task Page_ClampsPageIndexAndPageSize()
    {
        for (var i = 0; i < 3; i++) SeedLog();
        var svc = NewSvc();

        var zero = await svc.PageAsync(0, 0, null, null, null, null, null);
        Assert.Equal(1, zero.PageIndex);
        Assert.Equal(1, zero.PageSize); // 下限 1
        Assert.Single(zero.Items);

        var huge = await svc.PageAsync(1, 500, null, null, null, null, null);
        Assert.Equal(100, huge.PageSize); // 上限 100
    }

    [Fact]
    public async Task Page_CursorPaging_ShouldWalkStableOrder_AndStopAtEnd()
    {
        var baseTime = DateTime.Now.AddMinutes(-10);
        for (var i = 0; i < 5; i++) SeedLog(createdAt: baseTime.AddMinutes(i));
        var svc = NewSvc();

        var page1 = await svc.PageAsync(1, 2, null, null, null, null, null);
        Assert.Equal(2, page1.Items.Count);
        Assert.NotNull(page1.NextCursor); // 满页才产出游标

        var page2 = await svc.PageAsync(1, 2, null, null, null, null, null, cursor: page1.NextCursor);
        Assert.Equal(2, page2.Items.Count);
        Assert.False(page1.Items[0].Id == page2.Items[0].Id);
        // 游标页按 (CreatedAt, Id) 倒序衔接：page2 全部不晚于 page1 最后一条
        Assert.True(page2.Items[0].CreatedAt <= page1.Items[^1].CreatedAt);

        var page3 = await svc.PageAsync(1, 2, null, null, null, null, null, cursor: page2.NextCursor);
        Assert.Single(page3.Items);
        Assert.Null(page3.NextCursor); // 不足一页到底
    }

    [Fact]
    public async Task Page_BadCursor_ShouldFallBackToOffsetPaging()
    {
        SeedLog();
        var svc = NewSvc();
        var page = await svc.PageAsync(1, 10, null, null, null, null, null, cursor: "not-a-cursor");
        Assert.Single(page.Items); // 坏游标按无游标处理，不抛出
    }

    // ==================== ParseCursor / EncodeCursor ====================

    [Fact]
    public void ParseCursor_NullOrWhitespace_ShouldReturnNull()
    {
        Assert.Null(OperationLogAppService.ParseCursor(null));
        Assert.Null(OperationLogAppService.ParseCursor("  "));
    }

    [Fact]
    public void ParseCursor_Valid_ShouldRoundTrip()
    {
        var time = new DateTime(2026, 11, 23, 10, 0, 0);
        var cursor = OperationLogAppService.EncodeCursor(time, 42);
        var parsed = OperationLogAppService.ParseCursor(cursor);
        Assert.Equal((time, 42L), parsed);
    }

    [Fact]
    public void ParseCursor_InvalidBase64_ShouldReturnNull()
    {
        Assert.Null(OperationLogAppService.ParseCursor("!!!not base64!!!")); // FormatException 兜底
    }

    [Fact]
    public void ParseCursor_WrongSegmentCountOrNonNumeric_ShouldReturnNull()
    {
        var oneSegment = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("123"));
        Assert.Null(OperationLogAppService.ParseCursor(oneSegment));

        var nonNumeric = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("abc|def"));
        Assert.Null(OperationLogAppService.ParseCursor(nonNumeric));
    }

    // ==================== BatchDeleteAsync ====================

    [Fact]
    public async Task BatchDelete_ShouldPhysicallyDeleteDistinct()
    {
        var a = SeedLog();
        var b = SeedLog();
        SeedLog();

        var deleted = await NewSvc().BatchDeleteAsync(new List<long> { a, b, a });

        Assert.Equal(2, deleted);
        Assert.Single(_db.Queryable<SysOperationLog>().ToList()); // 物理删除
    }

    // ==================== AuditCleanupService ====================

    [Fact]
    public async Task Cleanup_ZeroRetention_ShouldDeleteNothing()
    {
        SeedLog(createdAt: DateTime.Now.AddDays(-100));
        var svc = new AuditCleanupService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditCleanupService>.Instance);

        var deleted = await svc.CleanupAsync(0);

        Assert.Equal(0, deleted);
        Assert.Single(_db.Queryable<SysOperationLog>().ToList()); // 永久保留一条不删
    }

    [Fact]
    public async Task Cleanup_ShouldDeleteOnlyExpiredRows()
    {
        SeedLog(createdAt: DateTime.Now.AddDays(-30));
        SeedLog(createdAt: DateTime.Now.AddDays(-20));
        SeedLog(createdAt: DateTime.Now); // 未过期
        var svc = new AuditCleanupService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditCleanupService>.Instance);

        var deleted = await svc.CleanupAsync(retentionDays: 10);

        Assert.Equal(2, deleted);
        Assert.Single(_db.Queryable<SysOperationLog>().ToList());
    }

    [Fact]
    public async Task Cleanup_MoreThanOneBatch_ShouldLoopUntilDrained()
    {
        // 5005 条过期日志：满批 5000 续跑一轮，剩余 5 条收尾
        var cutoff = DateTime.Now.AddDays(-10);
        var idGen = new SnowflakeIdGenerator(1, 1);
        var expired = Enumerable.Range(0, 5005).Select(i => new SysOperationLog
        {
            Id = idGen.NextId(),
            UserId = 1, UserName = "u", Category = "operation", Method = "GET",
            Path = "/api/x", StatusCode = 200, Success = 1, CreatedAt = cutoff.AddHours(-1)
        }).ToList();
        _db.Insertable(expired).ExecuteCommand();
        var svc = new AuditCleanupService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditCleanupService>.Instance);

        var deleted = await svc.CleanupAsync(retentionDays: 10);

        Assert.Equal(5005, deleted); // 满批循环 + 收尾批全部清空
        Assert.Empty(_db.Queryable<SysOperationLog>().ToList());
    }

    // ==================== LogCleanupBackgroundService ====================

    [Fact]
    public async Task BackgroundService_ShouldRunCleanup_AndSurviveErrors()
    {
        var old = LogCleanupBackgroundService.FirstDelay;
        var oldInterval = LogCleanupBackgroundService.Interval;
        LogCleanupBackgroundService.FirstDelay = TimeSpan.FromMilliseconds(1);
        LogCleanupBackgroundService.Interval = TimeSpan.FromMilliseconds(80);
        try
        {
            // 过期日志 2 条 + 未过期 1 条；默认保留 90 天 → 首跑清掉过期两条
            SeedLog(createdAt: DateTime.Now.AddDays(-100));
            SeedLog(createdAt: DateTime.Now.AddDays(-100));
            SeedLog(createdAt: DateTime.Now);

            var services = new ServiceCollection();
            services.AddSingleton<ISqlSugarClient>(_db);
            services.AddSingleton<SnowflakeIdGenerator>(new SnowflakeIdGenerator(1, 1));
            services.AddSingleton<IMemoryCache>(_cache);
            services.AddSingleton<LoginSecurityAppService>();
            services.AddSingleton<AuditCleanupService>();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();

            using var cts = new CancellationTokenSource();
            var host = new LogCleanupBackgroundService(provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<LogCleanupBackgroundService>.Instance);
            await host.StartAsync(cts.Token);
            await Task.Delay(300); // 等首跑完成
            cts.Cancel();
            await host.StopAsync(CancellationToken.None);

            Assert.Single(_db.Queryable<SysOperationLog>().ToList()); // 过期两条被清，未过期保留
        }
        finally
        {
            LogCleanupBackgroundService.FirstDelay = old;
            LogCleanupBackgroundService.Interval = oldInterval;
        }
    }

    [Fact]
    public async Task BackgroundService_ZeroRetention_ShouldSkipCleanup()
    {
        var old = LogCleanupBackgroundService.FirstDelay;
        var oldInterval = LogCleanupBackgroundService.Interval;
        LogCleanupBackgroundService.FirstDelay = TimeSpan.FromMilliseconds(1);
        LogCleanupBackgroundService.Interval = TimeSpan.FromMilliseconds(50);
        try
        {
            SeedLog(createdAt: DateTime.Now.AddDays(-100));
            _db.Insertable(new SysSiteConfig { Id = 1, ConfigKey = LoginSecurityKeys.LogRetentionDays, ConfigValue = "0" }).ExecuteCommand();

            var services = new ServiceCollection();
            services.AddSingleton<ISqlSugarClient>(_db);
            services.AddSingleton<SnowflakeIdGenerator>(new SnowflakeIdGenerator(1, 1));
            services.AddSingleton<IMemoryCache>(_cache);
            services.AddSingleton<LoginSecurityAppService>();
            services.AddSingleton<AuditCleanupService>();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();

            using var cts = new CancellationTokenSource();
            var host = new LogCleanupBackgroundService(provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<LogCleanupBackgroundService>.Instance);
            await host.StartAsync(cts.Token);
            await Task.Delay(250);
            cts.Cancel();
            await host.StopAsync(CancellationToken.None);

            Assert.Single(_db.Queryable<SysOperationLog>().ToList()); // 永久保留：一条不删
        }
        finally
        {
            LogCleanupBackgroundService.FirstDelay = old;
            LogCleanupBackgroundService.Interval = oldInterval;
        }
    }

    [Fact]
    public async Task BackgroundService_MissingDependency_ShouldLogErrorAndKeepLooping()
    {
        var old = LogCleanupBackgroundService.FirstDelay;
        var oldInterval = LogCleanupBackgroundService.Interval;
        LogCleanupBackgroundService.FirstDelay = TimeSpan.FromMilliseconds(1);
        LogCleanupBackgroundService.Interval = TimeSpan.FromMilliseconds(50);
        try
        {
            // 不注册 AuditCleanupService → GetRequiredService 抛异常 → 记错误后下个周期重试
            var services = new ServiceCollection();
            services.AddSingleton<ISqlSugarClient>(_db);
            services.AddSingleton<SnowflakeIdGenerator>(new SnowflakeIdGenerator(1, 1));
            services.AddSingleton<IMemoryCache>(_cache);
            services.AddSingleton<LoginSecurityAppService>();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();

            using var cts = new CancellationTokenSource();
            var host = new LogCleanupBackgroundService(provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<LogCleanupBackgroundService>.Instance);
            await host.StartAsync(cts.Token);
            await Task.Delay(250); // 多轮异常后宿主仍存活
            cts.Cancel();
            await host.StopAsync(CancellationToken.None);
        }
        finally
        {
            LogCleanupBackgroundService.FirstDelay = old;
            LogCleanupBackgroundService.Interval = oldInterval;
        }
    }
}

/// <summary>
/// 角色服务补账：保存校验（空名/编码重复/更新缺失404）、带权限新增与更新、列表、权限查询与分配、
/// 级联删除、批量删除事务故障桩兜底。SQLite 真库实效断言。
/// </summary>
public class RoleAppServiceGapTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);

    public RoleAppServiceGapTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"role_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables(typeof(SysRole), typeof(SysRoleMenu), typeof(SysRoleApi), typeof(SysUserRole), typeof(SysUser));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private RoleAppService NewSvc() => new(_db, new SysRoleRepository(_db, _snowflake), _snowflake);

    [Fact]
    public async Task Save_BlankNameOrCode_ShouldThrow()
    {
        var svc = NewSvc();
        await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new RoleSaveRequest { name = " ", code = "c" }));
        await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new RoleSaveRequest { name = "n", code = "" }));
    }

    [Fact]
    public async Task Save_DuplicateCode_ShouldThrow()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new RoleSaveRequest { name = "经理", code = "mgr" });
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new RoleSaveRequest { name = "经理2", code = "mgr" }));
        Assert.Contains("已存在", ex.Message);
    }

    [Fact]
    public async Task Save_AddWithPermissions_ShouldPersistRoleAndLinks()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new RoleSaveRequest
        {
            name = "经理", code = "mgr", description = "备注", status = 1,
            menuIds = new List<string> { "100", "101" }, apiIds = new List<string> { "200" }
        });

        var role = _db.Queryable<SysRole>().Single(r => r.RoleCode == "mgr");
        Assert.Equal("备注", role.Remark);
        Assert.Equal(2, _db.Queryable<SysRoleMenu>().Count(rm => rm.RoleId == role.Id));
        Assert.Single(_db.Queryable<SysRoleApi>().ToList());
    }

    [Fact]
    public async Task Save_UpdateMissing_ShouldThrow404()
    {
        var svc = NewSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new RoleSaveRequest { id = "31337", name = "n", code = "c" }));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Save_Update_ShouldPersistAndReplacePermissions()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new RoleSaveRequest { name = "旧", code = "old" });
        var id = _db.Queryable<SysRole>().Single().Id;
        _db.Insertable(new SysRoleMenu { Id = 1, RoleId = id, MenuId = 100 }).ExecuteCommand();

        await svc.SaveAsync(new RoleSaveRequest
        {
            id = id.ToString(), name = "新", code = "new", status = 0,
            menuIds = new List<string> { "200" }, apiIds = new List<string>()
        });

        var role = _db.Queryable<SysRole>().Single(r => r.Id == id);
        Assert.Equal("新", role.RoleName);
        Assert.Equal(0, role.Status);
        Assert.Equal(0, _db.Queryable<SysRoleMenu>().Count(rm => rm.RoleId == id && rm.MenuId == 100)); // 旧授权被重建
        Assert.Single(_db.Queryable<SysRoleMenu>().ToList());
    }

    [Fact]
    public async Task List_ShouldReturnAllNotDeleted()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new RoleSaveRequest { name = "甲", code = "a" });
        await svc.SaveAsync(new RoleSaveRequest { name = "乙", code = "b" });
        var b = _db.Queryable<SysRole>().Single(r => r.RoleCode == "b");
        await svc.DeleteAsync(b.Id);

        var list = await svc.ListAsync();
        Assert.Single(list);
        Assert.Equal("a", list[0].code);
    }

    [Fact]
    public async Task GetPermissions_AndAssignPermissions_ShouldRoundTrip()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new RoleSaveRequest { name = "r", code = "r" });
        var id = _db.Queryable<SysRole>().Single().Id;

        await svc.AssignPermissionsAsync(id, new List<string> { "11", "12" }, new List<string> { "21" });

        var (menuIds, apiIds) = await svc.GetPermissionsAsync(id);
        Assert.Equal(new List<string> { "11", "12" }, menuIds);
        Assert.Equal(new List<string> { "21" }, apiIds);
    }

    [Fact]
    public async Task Delete_ShouldCascadeLinks()
    {
        var svc = NewSvc();
        await svc.SaveAsync(new RoleSaveRequest { name = "r", code = "r" });
        var id = _db.Queryable<SysRole>().Single().Id;
        _db.Insertable(new SysRoleMenu { Id = 1, RoleId = id, MenuId = 9 }).ExecuteCommand();
        _db.Insertable(new SysRoleApi { Id = 2, RoleId = id, ApiId = 9 }).ExecuteCommand();
        _db.Insertable(new SysUserRole { Id = 3, UserId = 9, RoleId = id }).ExecuteCommand();

        await svc.DeleteAsync(id);

        Assert.Equal(0, _db.Queryable<SysRole>().Count(r => r.IsDeleted == false));
        Assert.Empty(_db.Queryable<SysRoleMenu>().ToList());
        Assert.Empty(_db.Queryable<SysRoleApi>().ToList());
        Assert.Empty(_db.Queryable<SysUserRole>().ToList());
    }

    [Fact]
    public async Task BatchDelete_TransportFailure_ShouldConvertToBusinessException()
    {
        // UseTranAsync 自身抛异常 → 走兜底 catch(Exception) 转业务异常
        var client = (ISqlSugarClient)System.Reflection.DispatchProxy.Create<ISqlSugarClient, ThrowingAdoClient>();
        var svc = new RoleAppService(client, new SysRoleRepository(_db, _snowflake), _snowflake);
        _db.Insertable(new SysRole { Id = 1, RoleName = "r", RoleCode = "r", Status = 1 }).ExecuteCommand();

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.BatchDeleteAsync(new List<long> { 1 }));
        Assert.Contains("回滚", ex.Message);
        Assert.Equal(1, _db.Queryable<SysRole>().Count(r => r.IsDeleted == false)); // 数据未被误删
    }

    private class ThrowingAdoClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Ado")
                return System.Reflection.DispatchProxy.Create<IAdo, ThrowingAdo>();
            return targetMethod!.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private class ThrowingAdo : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "UseTranAsync")
                throw new InvalidOperationException("boom");
            return targetMethod!.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}

/// <summary>
/// 用户服务补账：保存校验（空账号/重复/策略不合规）、带角色新增与更新、分页角色聚合、
/// 删自己拒绝、批量启停容错（跳过自己/缺失/单条失败不中断）、批量删除跳过自己与事务兜底、
/// 重置密码策略校验、分配角色。SQLite 真库实效断言。
/// </summary>
public class UserAppServiceGapTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);
    private readonly RecordingMq _mq = new();

    public UserAppServiceGapTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"user_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables(typeof(SysUser), typeof(SysUserRole), typeof(SysRole));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private UserAppService NewSvc(ISysUserRepository? repo = null, IPasswordPolicyProvider? policy = null)
        => new(_db, repo ?? new FakeUserRepository(), _snowflake, _mq,
            policy ?? new DefaultPolicyStub());

    private static SysUser NewUser(long id, string name, int status = 1) => new()
    {
        Id = id, UserName = name, Password = BCrypt.Net.BCrypt.HashPassword("Aa@123456"),
        NickName = name, Status = status, CreatedAt = DateTime.Now
    };

    [Fact]
    public async Task Save_BlankUsername_ShouldThrow()
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            NewSvc().SaveAsync(new UserSaveRequest { username = " ", password = "Aa@123456" }));
    }

    [Fact]
    public async Task Save_DuplicateUsername_ShouldThrow()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = NewUser(1, "tao");
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            NewSvc(repo).SaveAsync(new UserSaveRequest { username = "tao", password = "Aa@123456" }));
        Assert.Contains("已存在", ex.Message);
    }

    [Fact]
    public async Task Save_PolicyViolation_ShouldThrow()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            NewSvc().SaveAsync(new UserSaveRequest { username = "new1", password = "123" }));
        Assert.Contains("密码", ex.Message);
    }

    [Fact]
    public async Task Save_AddWithRoles_ShouldPersistUserRolesAndPublishEvent()
    {
        var repo = new FakeUserRepository();
        await NewSvc(repo).SaveAsync(new UserSaveRequest
        {
            username = "new1", password = "Aa@123456", nickname = "新人", email = "a@b.c", phone = "138",
            roleIds = new List<string> { "5", "6" }
        });

        var user = repo.Users.Values.Single(u => u.UserName == "new1");
        Assert.True(BCrypt.Net.BCrypt.Verify("Aa@123456", user.Password));
        Assert.NotNull(user.PasswordUpdatedAt);
        Assert.Equal(2, repo.RoleReplacements[user.Id].Count); // 角色已分配
        Assert.Contains("user.created", _mq.Topics); // 创建事件已发布
    }

    [Fact]
    public async Task Save_AddWithoutRoles_ShouldSkipRoleAssignment()
    {
        var repo = new FakeUserRepository();
        await NewSvc(repo).SaveAsync(new UserSaveRequest { username = "new2", password = "Aa@123456" });
        Assert.Empty(repo.RoleReplacements); // 未带角色不触发分配
    }

    [Fact]
    public async Task Save_UpdateMissing_ShouldThrow404()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            NewSvc().SaveAsync(new UserSaveRequest { id = "31337", username = "x" }));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Save_Update_WithAndWithoutRoleIds()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = NewUser(1, "tao");
        var svc = NewSvc(repo);

        await svc.SaveAsync(new UserSaveRequest { id = "1", username = "tao", nickname = "新昵称", status = 0 });
        Assert.Null(repo.RoleReplacements.GetValueOrDefault(1)); // 未带 roleIds 不动角色
        Assert.Equal("新昵称", repo.Users[1].NickName);
        Assert.Equal(0, repo.Users[1].Status);

        await svc.SaveAsync(new UserSaveRequest { id = "1", username = "tao", roleIds = new List<string> { "7" } });
        Assert.Equal(new List<long> { 7 }, repo.RoleReplacements[1]);
    }

    [Fact]
    public async Task Page_ShouldAggregateRoles_AndFilterByKeyword()
    {
        _db.Insertable(new SysUser { Id = 1, UserName = "tao", Password = "x", NickName = "t", Status = 1 }).ExecuteCommand();
        _db.Insertable(new SysUser { Id = 2, UserName = "lee", Password = "x", NickName = "l", Status = 1 }).ExecuteCommand();
        _db.Insertable(new SysRole { Id = 5, RoleName = "经理", RoleCode = "mgr", Status = 1 }).ExecuteCommand();
        _db.Insertable(new SysRole { Id = 6, RoleName = "已删角色", RoleCode = "gone", Status = 1, IsDeleted = true }).ExecuteCommand();
        _db.Insertable(new SysUserRole { Id = 1, UserId = 1, RoleId = 5 }).ExecuteCommand();
        _db.Insertable(new SysUserRole { Id = 2, UserId = 1, RoleId = 6 }).ExecuteCommand(); // 已删角色不聚合
        var svc = NewSvc();

        var all = await svc.PageAsync(1, 10, null);
        Assert.Equal(2, all.Total);
        var tao = all.Items.Single(i => i.username == "tao");
        Assert.Equal(new List<string> { "5" }, tao.roleIds);
        Assert.Equal(new List<string> { "经理" }, tao.roleNames);

        var filtered = await svc.PageAsync(1, 10, "ta");
        Assert.Single(filtered.Items);
    }

    [Fact]
    public async Task Delete_Self_ShouldThrow()
    {
        var svc = NewSvc();
        await Assert.ThrowsAsync<DomainException>(() => svc.DeleteAsync(1, currentUserId: 1));
    }

    [Fact]
    public async Task Delete_Other_ShouldSoftDeleteAndClearLinks()
    {
        var repo = new FakeUserRepository();
        repo.Users[2] = NewUser(2, "lee");
        await NewSvc(repo).DeleteAsync(2, currentUserId: 1);
        Assert.False(repo.Users.ContainsKey(2)); // 已软删
        Assert.Contains(2L, repo.DeletedRoleLinks); // 角色关联已清
    }

    [Fact]
    public async Task BatchSetStatus_ShouldSkipSelfAndMissing_AndIsolateFailures()
    {
        var repo = new FakeUserRepository();
        repo.Users[2] = NewUser(2, "lee");
        repo.Users[3] = NewUser(3, "cat");
        repo.ThrowOnUpdate = 3; // 更新 3 号时注入故障

        var (skipped, failed) = await NewSvc(repo).BatchSetStatusAsync(
            new List<long> { 1, 2, 3, 999, 2 }, status: 0, currentUserId: 1);

        Assert.Equal(2, skipped); // 自己 + 缺失的 999
        Assert.Equal(0, repo.Users[2].Status); // 正常条目已生效
        Assert.Single(failed); // 3 号失败明细随返回
        Assert.StartsWith("3:", failed[0]);
    }

    [Fact]
    public async Task BatchDelete_ShouldSkipSelf_AndConvertTransportFailure()
    {
        var repo = new FakeUserRepository();
        repo.Users[2] = NewUser(2, "lee");
        var svc = NewSvc(repo);

        var skipped = await svc.BatchDeleteAsync(new List<long> { 1, 2, 1 }, currentUserId: 1);
        Assert.Equal(1, skipped); // 自己被跳过
        Assert.False(repo.Users.ContainsKey(2));

        // 事务层故障 → 兜底 catch 转业务异常
        var client = (ISqlSugarClient)System.Reflection.DispatchProxy.Create<ISqlSugarClient, ThrowingAdoClient>();
        var broken = new UserAppService(client, repo, _snowflake, _mq, new DefaultPolicyStub());
        var ex = await Assert.ThrowsAsync<DomainException>(() => broken.BatchDeleteAsync(new List<long> { 2 }, currentUserId: 1));
        Assert.Contains("回滚", ex.Message);
    }

    [Fact]
    public async Task ResetPassword_PolicyViolation_ShouldThrow()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = NewUser(1, "tao");
        await Assert.ThrowsAsync<DomainException>(() => NewSvc(repo).ResetPasswordAsync(1, "123"));
        Assert.True(BCrypt.Net.BCrypt.Verify("Aa@123456", repo.Users[1].Password)); // 原密码未动
    }

    [Fact]
    public async Task ResetPassword_Valid_ShouldUpdateHash()
    {
        var repo = new FakeUserRepository();
        repo.Users[1] = NewUser(1, "tao");
        await NewSvc(repo).ResetPasswordAsync(1, "New@123456");
        Assert.True(BCrypt.Net.BCrypt.Verify("New@123456", repo.Users[1].Password));
    }

    [Fact]
    public async Task AssignRoles_ShouldReplace()
    {
        var repo = new FakeUserRepository();
        await NewSvc(repo).AssignRolesAsync(1, new List<string> { "3", "4" });
        Assert.Equal(new List<long> { 3, 4 }, repo.RoleReplacements[1]);
    }

    private class ThrowingAdoClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Ado")
                return System.Reflection.DispatchProxy.Create<IAdo, ThrowingAdo>();
            return targetMethod!.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private class ThrowingAdo : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "UseTranAsync")
                throw new InvalidOperationException("boom");
            return targetMethod!.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private sealed class RecordingMq : TaoAdmin.Common.Messaging.IMessageQueue
    {
        public readonly List<string> Topics = new();
        public string ProviderName => "recording";
        public Task PublishAsync(string topic, string body) { Topics.Add(topic); return Task.CompletedTask; }
        public Task SubscribeAsync(string topic, Func<TaoAdmin.Common.Messaging.MessageEnvelope, Task> onMessage, CancellationToken ct) => Task.CompletedTask;
        public Dictionary<string, long> GetStats() => new();
    }

    /// <summary>内存用户仓储替身：记录角色替换与删除调用，可注入更新故障。</summary>
    private class FakeUserRepository : ISysUserRepository
    {
        public readonly Dictionary<long, SysUser> Users = new();
        public readonly Dictionary<long, List<long>> RoleReplacements = new();
        public readonly List<long> DeletedRoleLinks = new();
        public long? ThrowOnUpdate;

        public Task<SysUser?> FindByIdAsync(long id)
            => Task.FromResult(Users.TryGetValue(id, out var u) ? u : null);

        public Task<SysUser?> FindByNameAsync(string userName)
            => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));

        public Task<bool> ExistsNameAsync(string userName)
            => Task.FromResult(Users.Values.Any(u => u.UserName == userName));

        public Task AddAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }

        public Task UpdateProfileAsync(SysUser user)
        {
            if (ThrowOnUpdate == user.Id) throw new InvalidOperationException("注入故障");
            Users[user.Id] = user;
            return Task.CompletedTask;
        }

        public Task UpdateSelfProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task SoftDeleteAsync(long id) { Users.Remove(id); return Task.CompletedTask; }
        public Task TouchLoginAsync(long id) => Task.CompletedTask;
        public Task ResetPasswordAsync(long id, string passwordHash) { Users[id].Password = passwordHash; return Task.CompletedTask; }

        public Task ReplaceRolesAsync(long userId, IReadOnlyCollection<long> roleIds)
        {
            RoleReplacements[userId] = roleIds.ToList();
            return Task.CompletedTask;
        }

        public Task DeleteRoleLinksAsync(long userId) { DeletedRoleLinks.Add(userId); return Task.CompletedTask; }
    }
}

/// <summary>
/// 公告服务补账：发布人空白回退、标题/内容长度校验、上下线补发布时间、删除缺失404、
/// 批量删除计数、批量上下线容错（触发器注入单条失败不中断）。SQLite 真库实效断言。
/// </summary>
public class AnnouncementAppServiceGapTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);

    public AnnouncementAppServiceGapTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"ann_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables(typeof(SysAnnouncement));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private AnnouncementAppService NewSvc() => new(_db, _snowflake);

    private long Seed(string title = "公告", int status = 1, DateTime? publishTime = null)
    {
        var row = new SysAnnouncement
        {
            Id = _snowflake.NextId(), Title = title, Status = status, PublishTime = publishTime ?? DateTime.Now
        };
        _db.Insertable(row).ExecuteCommand();
        return row.Id;
    }

    [Fact]
    public async Task Publish_BlankPublisherName_ShouldFallBackToId()
    {
        await NewSvc().PublishAsync(new AnnouncementSaveRequest { title = "  标题  ", content = " 内容 " }, 42, "  ");
        var row = _db.Queryable<SysAnnouncement>().Single();
        Assert.Equal("标题", row.Title); // 去首尾空白
        Assert.Equal("42", row.PublisherName); // 发布人空白回退为 ID
        Assert.Equal(1, row.Status);
        Assert.NotNull(row.PublishTime);
    }

    [Fact]
    public async Task Publish_TitleTooLongOrContentTooLong_ShouldThrow400()
    {
        var svc = NewSvc();
        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            svc.PublishAsync(new AnnouncementSaveRequest { title = new string('长', 101) }, 1, "p"));
        Assert.Equal(400, ex1.Code);
        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            svc.PublishAsync(new AnnouncementSaveRequest { title = "t", content = new string('c', 20001) }, 1, "p"));
        Assert.Equal(400, ex2.Code);
    }

    [Fact]
    public async Task SetStatus_Online_ShouldBackfillPublishTime_OfflineKeepsIt()
    {
        var svc = NewSvc();
        var offlineId = Seed(title: "下线", status: 1, publishTime: DateTime.Now.AddDays(-1));
        await svc.SetStatusAsync(offlineId, online: false);
        var offline = _db.Queryable<SysAnnouncement>().Single(a => a.Id == offlineId);
        Assert.Equal(0, offline.Status);

        var onlineId = Seed(title: "上线", status: 0, publishTime: null);
        await svc.SetStatusAsync(onlineId, online: true);
        var online = _db.Queryable<SysAnnouncement>().Single(a => a.Id == onlineId);
        Assert.Equal(1, online.Status);
        Assert.NotNull(online.PublishTime); // 上线补发布时间
    }

    [Fact]
    public async Task SetStatus_Missing_ShouldThrow404()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewSvc().SetStatusAsync(31337, online: true));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Delete_Missing_ShouldThrow404()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => NewSvc().DeleteAsync(31337));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task BatchDelete_ShouldCountOnlyNotDeleted()
    {
        var a = Seed();
        var b = Seed();
        var c = Seed();
        await NewSvc().DeleteAsync(c); // 先软删一条

        var deleted = await NewSvc().BatchDeleteAsync(new List<long> { a, b, c });

        Assert.Equal(2, deleted); // 已删的不重复计数
    }

    [Fact]
    public async Task BatchSetStatus_ShouldIsolateSingleFailure_AndBackfillPublishTime()
    {
        var a = Seed(title: "甲", status: 0, publishTime: null);
        var b = Seed(title: "乙", status: 0, publishTime: null);
        _db.Ado.ExecuteCommand($"CREATE TRIGGER boom BEFORE UPDATE ON sys_announcement WHEN NEW.Id = {b} BEGIN SELECT RAISE(ABORT, '注入故障'); END");

        var (updated, failed) = await NewSvc().BatchSetStatusAsync(new List<long> { a, b, 999999 }, online: true);

        Assert.Equal(1, updated); // 甲成功
        Assert.Single(failed);  // 乙失败明细随返回
        Assert.StartsWith($"{b}:", failed[0]);
        Assert.Equal(1, _db.Queryable<SysAnnouncement>().Count(x => x.Id == a && x.Status == 1));
        Assert.NotNull(_db.Queryable<SysAnnouncement>().First(x => x.Id == a).PublishTime); // 上线补发布时间
        Assert.Equal(0, _db.Queryable<SysAnnouncement>().First(x => x.Id == b).Status); // 失败条目未被误改
    }
}
