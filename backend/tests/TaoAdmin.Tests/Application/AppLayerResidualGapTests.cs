using System.Reflection;
using MiniExcelLibs;
using SqlSugar;
using StackExchange.Redis;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Security;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Domain.Repositories;
using TaoAdmin.Infrastructure.Repositories;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 应用层残留缺口清零（分支 100% 硬门禁）：
/// ①RegionWriteService.ReplaceAllCore 写入失败回滚重抛（catch/RollbackTran/throw 三行实效触达）；
/// ②RoleAppService.BatchDeleteAsync 事务失败转业务异常并实证整批回滚；
/// ③AuthAppService.GetCurrentUserAsync 租户名两侧（有租户行/无租户行）；
/// ④WorkflowAppService StatusText 全状态文案 + ParseNodes null 与坏 JSON 兜底；
/// ⑤UserExcelAppService.ImportAsync 整行空白跳过；
/// ⑥NullSmsSender/NullEmailSender 占位发送器实效调用；
/// ⑦AuditLogExcelAppService 导出含失败行（Success==1 两侧）；
/// ⑧DTO 属性初始化器与 setter 直测。
/// </summary>
public class AppLayerResidualGapTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);

    public AppLayerResidualGapTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"appgap2_{Guid.NewGuid():N}.db");
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

    // ==================== ① RegionWriteService：写入失败回滚重抛 ====================

    [Fact]
    public void ReplaceAll_MetaTableMissing_ShouldRollbackAndRethrow()
    {
        // 只建区划表、不建版本元数据表：写入区划成功后元数据查询必炸，触发 catch→回滚→重抛
        _db.CodeFirst.InitTables<SysRegion>();
        var writer = new RegionWriteService(_db);
        var snapshot = new RegionSnapshot
        {
            Version = "回滚测试版本",
            Source = "测试数据源",
            Nodes = [new RegionNodeDto { Code = "110000", Name = "北京市", Level = 1 }]
        };

        Assert.ThrowsAny<Exception>(() => writer.ReplaceAll(snapshot, _snowflake));

        // 整批回滚实证：区划表回到空库，不留半包数据
        Assert.Equal(0, _db.Queryable<SysRegion>().Count());
    }

    // ==================== ② RoleAppService：事务失败转业务异常并回滚 ====================

    [Fact]
    public async Task RoleBatchDelete_TransactionFails_ShouldThrowBusinessException_AndRollBack()
    {
        _db.CodeFirst.InitTables<SysRole, SysRoleMenu, SysRoleApi, SysUserRole>();
        _db.Insertable(new[]
        {
            new SysRole { Id = 1, RoleName = "r1", RoleCode = "r1", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now },
            new SysRole { Id = 2, RoleName = "r2", RoleCode = "r2", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }
        }).ExecuteCommand();
        _db.Insertable(new SysRoleMenu { Id = 11, RoleId = 1, MenuId = 100 }).ExecuteCommand();

        var repo = new FailOnSecondCascadeRepository(_db);
        var svc = new RoleAppService(_db, repo, _snowflake);

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.BatchDeleteAsync(new List<long> { 1, 2 }));

        Assert.Contains("回滚", ex.Message);
        // 全成全败实证：角色 1 的软删被整批撤销，授权关系原样保留
        Assert.Equal(2, _db.Queryable<SysRole>().Count(r => r.IsDeleted == false));
        Assert.Single(_db.Queryable<SysRoleMenu>().ToList());
    }

    private sealed class FailOnSecondCascadeRepository(SqlSugarClient db) : ISysRoleRepository
    {
        private readonly SysRoleRepository _real = new(db, new SnowflakeIdGenerator(1, 1));
        private int _calls;

        public Task<SysRole?> FindByIdAsync(long id) => _real.FindByIdAsync(id);
        public Task<bool> ExistsCodeAsync(string roleCode) => _real.ExistsCodeAsync(roleCode);
        public Task AddAsync(SysRole role) => _real.AddAsync(role);
        public Task UpdateAsync(SysRole role) => _real.UpdateAsync(role);

        public async Task SoftDeleteCascadeAsync(long id)
        {
            if (++_calls == 2)
                throw new InvalidOperationException("注入故障：第二条级联删除失败");
            await _real.SoftDeleteCascadeAsync(id);
        }

        public Task ReplacePermissionsAsync(long roleId, IReadOnlyCollection<long>? menuIds, IReadOnlyCollection<long>? apiIds)
            => _real.ReplacePermissionsAsync(roleId, menuIds, apiIds);
    }

    // ==================== ③ AuthAppService：租户名两侧 ====================

    [Fact]
    public async Task GetCurrentUserAsync_WithTenantRow_AndWithoutRow_BothSides()
    {
        _db.CodeFirst.InitTables(typeof(SysRole), typeof(SysUserRole), typeof(SysMenu), typeof(SysApi), typeof(SysRoleMenu), typeof(SysRoleApi), typeof(SysTenant));
        var users = new StubUserRepository
        {
            Users =
            {
                [1] = new SysUser { Id = 1, UserName = "t1", NickName = "租户甲员", TenantId = 5, Status = 1, CreatedAt = DateTime.Now },
                [2] = new SysUser { Id = 2, UserName = "t2", NickName = "悬空租户", TenantId = 6, Status = 1, CreatedAt = DateTime.Now }
            }
        };
        _db.Insertable(new SysTenant { Id = 5, TenantName = "租户甲", Status = 1, CreatedAt = DateTime.Now }).ExecuteCommand();
        var svc = NewAuthSvc(users);

        // 有租户行：租户名取自租户表
        var withTenant = await svc.GetCurrentUserAsync(1);
        Assert.Equal("租户甲", withTenant.tenantName);

        // 无租户行（悬空租户 ID）：?. 兜底为 null，落 DTO 时回退「平台」
        var missingTenant = await svc.GetCurrentUserAsync(2);
        Assert.Equal("平台", missingTenant.tenantName);
    }

    private AuthAppService NewAuthSvc(StubUserRepository users) => new(
        _db, users, new JwtTokenService(new JwtOptions()), new RedisHelper(StubRedis.Multiplexer),
        new PermissionAppService(_db), new OnlineSessionStore(), new DefaultPolicyStub(),
        new SecurityStub(), new LockoutStub(),
        new CaptchaService(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())));

    private sealed class StubUserRepository : ISysUserRepository
    {
        public readonly Dictionary<long, SysUser> Users = new();

        public Task<SysUser?> FindByIdAsync(long id)
            => Task.FromResult(Users.TryGetValue(id, out var u) ? u : null);
        public Task<SysUser?> FindByNameAsync(string userName)
            => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));
        public Task<bool> ExistsNameAsync(string userName) => Task.FromResult(Users.Values.Any(u => u.UserName == userName));
        public Task AddAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateSelfProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task SoftDeleteAsync(long id) { Users.Remove(id); return Task.CompletedTask; }
        public Task TouchLoginAsync(long id) => Task.CompletedTask;
        public Task ResetPasswordAsync(long id, string passwordHash) { Users[id].Password = passwordHash; return Task.CompletedTask; }
        public Task ReplaceRolesAsync(long userId, IReadOnlyCollection<long> roleIds) => Task.CompletedTask;
        public Task DeleteRoleLinksAsync(long userId) => Task.CompletedTask;
    }

    private sealed class SecurityStub : ILoginSecurityProvider
    {
        public Task<LoginSecuritySettings> GetAsync() => Task.FromResult(
            new LoginSecuritySettings(5, 15, LoginCaptchaRequired: false, CaptchaAfterFails: 0, 90, KickOnDuplicateLogin: false));
    }

    private sealed class LockoutStub : ILoginLockoutStore
    {
        public int? GetRemainingLockMinutes(string username, int lockMinutes) => null;
        public void RecordFailure(string username, int maxFails, int lockMinutes) { }
        public void Clear(string username) { }
        public int GetFails(string username) => 0;
        public void Reset() { }
    }

    /// <summary>Redis 连接桩：DispatchProxy 拦截全部调用返回默认值（本组用例不触达 Redis 写入）。</summary>
    private class StubRedis : DispatchProxy
    {
        public static readonly IConnectionMultiplexer Multiplexer =
            DispatchProxy.Create<IConnectionMultiplexer, StubRedis>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetDatabase")
                return DispatchProxy.Create<IDatabase, StubRedis>();
            return null;
        }
    }

    // ==================== ④ WorkflowAppService：状态文案 + 节点解析兜底 ====================

    [Fact]
    public async Task GetInstancesAsync_ShouldMapAllStatusTexts()
    {
        _db.CodeFirst.InitTables<SysWorkflow, SysWorkflowInstance, SysWorkflowTask>();
        var statuses = new[] { 1, 2, 3, 4, 99 };
        foreach (var s in statuses)
        {
            _db.Insertable(new SysWorkflowInstance
            {
                Id = _snowflake.NextId(), WorkflowId = 1, WorkflowName = "请假审批",
                BizTitle = $"状态{s}", ApplicantId = 7, ApplicantName = "申请人",
                Status = s, CurrentNodeIndex = 0, CreatedAt = DateTime.Now
            }).ExecuteCommand();
        }

        var page = await new WorkflowAppService(_db, _snowflake).GetInstancesAsync(1, 50, 0, isSuperAdmin: true);

        Assert.Equal(5, page.Items.Count);
        Assert.Contains(page.Items, i => i.status == 1 && i.statusText == "进行中");
        Assert.Contains(page.Items, i => i.status == 2 && i.statusText == "已通过");
        Assert.Contains(page.Items, i => i.status == 3 && i.statusText == "已驳回");
        Assert.Contains(page.Items, i => i.status == 4 && i.statusText == "已取消");
        Assert.Contains(page.Items, i => i.status == 99 && i.statusText == "未知"); // 越界状态兜底
    }

    [Fact]
    public async Task SubmitAsync_NodesJsonNullLiteral_AndBrokenJson_ShouldRejectWithNoNodes()
    {
        _db.CodeFirst.InitTables<SysWorkflow, SysWorkflowInstance, SysWorkflowTask>();
        var svc = new WorkflowAppService(_db, _snowflake);

        // "null" 字面量：反序列化返回 null → ?? 兜底为空列表
        _db.Insertable(new SysWorkflow
        {
            Id = _snowflake.NextId(), WorkflowName = "空节点", WorkflowCode = "wf-null",
            Status = 1, NodesJson = "null"
        }).ExecuteCommand();
        var exNull = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "wf-null", bizTitle = "标题" }, 1, "发起人"));
        Assert.Contains("流程未配置审批节点", exNull.Message);

        // 坏 JSON：解析异常 → catch 兜底为空列表
        _db.Insertable(new SysWorkflow
        {
            Id = _snowflake.NextId(), WorkflowName = "坏节点", WorkflowCode = "wf-bad",
            Status = 1, NodesJson = "{not json"
        }).ExecuteCommand();
        var exBad = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "wf-bad", bizTitle = "标题" }, 1, "发起人"));
        Assert.Contains("流程未配置审批节点", exBad.Message);
    }

    // ==================== ⑤ UserExcelAppService：整行空白跳过 ====================

    [Fact]
    public async Task ImportAsync_FullyBlankRow_ShouldBeSkipped_NotCounted()
    {
        _db.CodeFirst.InitTables<SysUser>();
        var svc = new UserExcelAppService(_db, _snowflake, new DefaultPolicyStub());

        var outMs = new MemoryStream();
        await MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "valid1", ["昵称"] = "有效一", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
            new() { ["用户名"] = " ", ["昵称"] = " ", ["邮箱"] = " ", ["手机号"] = " ", ["状态(启用/禁用)"] = " ", ["密码(仅导入,留空用默认密码)"] = " " }, // 整行空白
            new() { ["用户名"] = "valid2", ["昵称"] = "有效二", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");

        var result = await svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        // 空白行不计入 total，也不产生错误明细
        Assert.Equal(2, result.total);
        Assert.Equal(2, result.success);
        Assert.Empty(result.errors);
        Assert.Equal(2, _db.Queryable<SysUser>().Count());
    }

    // ==================== ⑥ 占位发送器：实效调用不抛且走空路径 ====================

    [Fact]
    public async Task NullSmsSender_SendAsync_ShouldReturnPlaceholderSuccess()
    {
        var (ok, message) = await new NullSmsSender().SendAsync("13800000000", "验证码 1234");

        Assert.True(ok);
        Assert.Equal(NullSmsSender.PlaceholderMessage, message); // 占位文案，不发真实请求
    }

    [Fact]
    public async Task NullEmailSender_SendAsync_ShouldReturnPlaceholderSuccess()
    {
        var (ok, message) = await new NullEmailSender().SendAsync("a@b.c", "主题", "正文");

        Assert.True(ok);
        Assert.Equal(NullEmailSender.PlaceholderMessage, message); // 占位文案，不发真实请求
    }

    // ==================== ⑦ AuditLogExcelAppService：导出含失败行 ====================

    [Fact]
    public async Task ExportAsync_WithFailedRow_ShouldMarkResultColumnBothWays()
    {
        _db.CodeFirst.InitTables<SysOperationLog>();
        var svc = new AuditLogExcelAppService(_db);
        var now = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        _db.Insertable(new List<SysOperationLog>
        {
            new() { Id = 1, Category = "operation", UserName = "ok", Success = 1, Path = "/api/a", CreatedAt = now },
            new() { Id = 2, Category = "operation", UserName = "bad", Success = 0, Path = "/api/b", CreatedAt = now.AddMinutes(1) },
            new() { Id = 3, UserName = "ghost", Success = 1, Detail = null, CreatedAt = now.AddMinutes(2) } // Detail 可空列空值：?? 兜底空串
        }).ExecuteCommand();

        var (content, count) = await svc.ExportAsync("operation", null, null, null, null);

        Assert.Equal(3, count);
        var rows = MiniExcel.Query(new MemoryStream(content), true)
            .Cast<IDictionary<string, object?>>().ToList();
        string Cell(IDictionary<string, object?> r, string col)
            => r.TryGetValue(col, out var v) && v != null ? v.ToString()!.Trim() : "";
        Assert.Equal("成功", Cell(rows.Single(r => Cell(r, "账号") == "ok"), "结果"));
        Assert.Equal("失败", Cell(rows.Single(r => Cell(r, "账号") == "bad"), "结果")); // Success==1 假侧实效触达
        var blank = rows.Single(r => Cell(r, "账号") == "ghost");
        Assert.Equal("", Cell(blank, "详情")); // Detail 可空列 null 侧兜底空串
    }

    // ==================== ⑧ DTO：属性初始化器与 setter 直测 ====================

    [Fact]
    public void ApiDtos_Defaults_AndNullableSetters()
    {
        var item = new ApiItemDto(); // 初始化器：method=GET
        Assert.Equal("GET", item.method);
        item.service = "svc-a";      // 190 setter
        item.description = "描述";   // 192 setter
        Assert.Equal("svc-a", item.service);
        Assert.Equal("描述", item.description);

        var save = new ApiSaveRequest(); // 初始化器：method=GET、status=1
        Assert.Equal("GET", save.method);
        Assert.Equal(1, save.status);
        save.service = "svc-b";      // 204 setter
        save.description = "说明";   // 206 setter
        Assert.Equal("svc-b", save.service);
        Assert.Equal("说明", save.description);
    }

    [Fact]
    public void LogAndJobDtos_Defaults_AndNullableSetters()
    {
        var log = new LogItemDto(); // 初始化器：level=INFO
        Assert.Equal("INFO", log.level);
        log.instance = "inst-1";    // 241 setter
        Assert.Equal("inst-1", log.instance);

        var job = new JobItemDto(); // 初始化器：status=RUNNING
        Assert.Equal("RUNNING", job.status);
        job.misfirePolicy = "FIRE_ONCE_NOW"; // 17 setter
        Assert.Equal("FIRE_ONCE_NOW", job.misfirePolicy);

        var save = new JobSaveRequest();
        save.id = "42";                      // 30 setter
        save.misfirePolicy = "DO_NOTHING";   // 36 setter
        Assert.Equal("42", save.id);
        Assert.Equal("DO_NOTHING", save.misfirePolicy);
    }
}
