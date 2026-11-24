using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Domain;
using TaoAdmin.Infrastructure.Repositories;
using TaoAdmin.Common.Security;
using TaoAdmin.IdentityService.Controllers;
using TaoAdmin.Tests.TaskService;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第七批）：边角分支补齐——
/// 公告匿名发布与上下线双向、登录来源 IP/无 jti 登出/重置密码成功流、
/// 用户重置缺参/分配缺参/导入空表与超限、角色页关键字三路、菜单与字典编辑分支、
/// 演示发布缺参、通知渠道测试成功路、工作流审批人昵称与匿名上下文、文件下载四态。
/// 真应用服务 + SQLite 内存库，断言 ApiResult 实效。
/// </summary>
public class IdentityControllersBatch7Tests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly SnowflakeIdGenerator _snow = new(11, 11);

    public IdentityControllersBatch7Tests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysAnnouncement, SysDictType, SysDictData, SysOperationLog>();
        _db.CodeFirst.InitTables<SysUser, SysRole, SysUserRole, SysRoleMenu, SysRoleApi>();
        _db.CodeFirst.InitTables<SysMenu, SysApi, SysTenant, SysPasswordResetToken>();
        _db.CodeFirst.InitTables<SysMessage, SysWorkflow, SysWorkflowInstance, SysWorkflowTask>();
        _db.CodeFirst.InitTables<SysSiteConfig, SysNotifyLog, SysFile>();
    }

    public void Dispose() => _db.Dispose();

    private const string GoodPassword = "Aa@123456";

    /// <summary>附加完整用户上下文（含 Name 与可选 NickName）。</summary>
    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId = 1,
        string? name = "admin", bool withName = true, string? nickname = null, bool withIdentity = true,
        string[]? roles = null)
    {
        var principal = new System.Security.Claims.ClaimsPrincipal();
        if (withIdentity)
        {
            var identity = new System.Security.Claims.ClaimsIdentity();
            identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
            if (withName)
                identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name!));
            if (nickname != null)
                identity.AddClaim(new System.Security.Claims.Claim("NickName", nickname));
            foreach (var r in roles ?? new[] { "admin" })
                identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r));
            principal.AddIdentity(identity);
        }
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // ---------- 公告：匿名发布与上下线双向 ----------

    [Fact]
    public async Task Announcement_匿名上下文发布与上线切换()
    {
        var controller = new AnnouncementController(new AnnouncementAppService(_db, _snow));
        AttachHttp(controller, withIdentity: false); // User.Identity 为 null 的分支

        var publish = await controller.Publish(new AnnouncementSaveRequest { title = "无署名公告", content = "内容" });
        Assert.True(publish.Success);

        var id = _db.Queryable<SysAnnouncement>().First().Id.ToString();
        var online = await controller.SetStatus(id, new Dictionary<string, bool> { ["online"] = true });
        Assert.True(online.Success);
        Assert.Equal("公告已上线", online.Message); // 上线侧文案分支
    }

    // ---------- 认证：来源 IP / 无 jti 登出 / 重置密码成功流 ----------

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
        public int? GetRemainingLockMinutes(string username, int lockMinutes) => null;
        public void RecordFailure(string username, int maxFails, int lockMinutes) { }
        public void Clear(string username) { }
        public void Reset() { }
        public int GetFails(string username) => 0;
    }

    private sealed class FakeEmail : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();
        public Task<(bool Ok, string Message)> SendAsync(string to, string subject, string body)
        {
            Sent.Add((to, subject, body));
            return Task.FromResult((true, "ok"));
        }
    }

    /// <summary>Redis 松散代理桩：全部成员返回默认值。</summary>
    private static RedisHelper FakeRedis()
    {
        var database = LooseProxy.Of<StackExchange.Redis.IDatabase>((method, _) =>
            method.ReturnType == typeof(bool) ? false
            : method.ReturnType == typeof(long) ? 0L
            : null);
        var multiplexer = LooseProxy.Of<StackExchange.Redis.IConnectionMultiplexer>((method, _) =>
            method.Name == nameof(StackExchange.Redis.IConnectionMultiplexer.GetDatabase) ? database : null);
        return new RedisHelper(multiplexer);
    }

    private static SysUser NewUser(long id, string name, string password = GoodPassword) => new()
    {
        Id = id, UserName = name, Password = BCrypt.Net.BCrypt.HashPassword(password),
        NickName = name, Email = $"{name}@tao.local", Status = 1, TenantId = 0,
        CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
    };

    private (AuthController Controller, AuthAppService Auth, PasswordResetAppService Reset, FakeEmail Email) BuildAuth()
    {
        var opts = new JwtOptions
        {
            Issuer = "TaoAdmin", Audience = "TaoAdmin",
            SecretKey = "unit-test-secret-key-must-be-32+chars!!", ExpireMinutes = 120
        };
        var sessions = new OnlineSessionStore(opts.ExpireMinutes);
        var auth = new AuthAppService(_db, new SysUserRepository(_db, _snow), new JwtTokenService(opts), FakeRedis(),
            new PermissionAppService(_db), sessions, new FixedPolicy(), new FixedSecurity(), new FakeLockout(),
            new CaptchaService(new MemoryCache(new MemoryCacheOptions())));
        var email = new FakeEmail();
        var reset = new PasswordResetAppService(_db, email, new FixedPolicy(), sessions);
        var controller = new AuthController(auth, reset);
        AttachHttp(controller);
        return (controller, auth, reset, email);
    }

    [Fact]
    public async Task Login_带来源IP_仍走完整认证链()
    {
        var (controller, _, _, _) = BuildAuth();
        controller.ControllerContext.HttpContext.Connection.RemoteIpAddress =
            System.Net.IPAddress.Loopback; // RemoteIpAddress 非空的分支

        await Assert.ThrowsAsync<DomainException>(
            () => controller.Login(new LoginRequest { username = "ghost", password = "x" }));
    }

    [Fact]
    public async Task Logout_无jti会话_仍清理刷新令牌()
    {
        var (controller, _, _, _) = BuildAuth();
        AttachHttp(controller, userId: 11, withName: false); // 不带 jti 的分支

        var got = await controller.Logout();
        Assert.True(got.Success);
        Assert.Equal("退出成功", got.Message);
    }

    [Fact]
    public async Task ResetPassword_有效令牌_重置成功()
    {
        var (controller, _, _, email) = BuildAuth();
        _db.Insertable(NewUser(11, "tao")).ExecuteCommand();

        var forgot = await controller.ForgotPassword(new ForgotPasswordRequest { username = "tao" });
        Assert.True(forgot.Success);
        var body = email.Sent[0].Body;
        var start = body.IndexOf("<code>") + "<code>".Length;
        var token = body[start..body.IndexOf("</code>", start)];

        var reset = await controller.ResetPassword(
            new ResetPasswordRequest { username = "tao", token = token, newPassword = "Cc@777888" });
        Assert.True(reset.Success);
        Assert.Equal("密码重置成功，请使用新密码登录", reset.Message); // 成功返回分支
        Assert.True(BCrypt.Net.BCrypt.Verify("Cc@777888", _db.Queryable<SysUser>().First(u => u.Id == 11).Password));
    }

    // ---------- 用户：缺参重置/分配、空表与超限导入 ----------

    private UserController BuildUser()
    {
        var svc = new UserAppService(_db, new SysUserRepository(_db, _snow), _snow, FakeMq(), new FixedPolicy());
        var controller = new UserController(svc, new UserExcelAppService(_db, _snow, new FixedPolicy()));
        AttachHttp(controller);
        return controller;
    }

    private static TaoAdmin.Common.Messaging.IMessageQueue FakeMq() =>
        LooseProxy.Of<TaoAdmin.Common.Messaging.IMessageQueue>((method, _) =>
            method.ReturnType == typeof(Task) ? Task.CompletedTask : null);

    [Fact]
    public async Task User_ResetPassword_缺password键_走空串校验()
    {
        var controller = BuildUser();
        _db.Insertable(NewUser(2, "u2")).ExecuteCommand();

        await Assert.ThrowsAsync<DomainException>(
            () => controller.ResetPassword("2", new Dictionary<string, string>())); // password ?? "" 的空串侧
    }

    [Fact]
    public async Task User_AssignRoles_缺roleIds键_清空角色关联()
    {
        var controller = BuildUser();
        _db.Insertable(NewUser(2, "u2")).ExecuteCommand();
        _db.Insertable(new SysUserRole { Id = 1, UserId = 2, RoleId = 9 }).ExecuteCommand();

        var ok = await controller.AssignRoles("2", new Dictionary<string, List<string>>()); // roleIds ?? 空列表分支
        Assert.True(ok.Success);
        Assert.Empty(_db.Queryable<SysUserRole>().Where(ur => ur.UserId == 2).ToList());
    }

    private sealed class BigEmptyFormFile : IFormFile
    {
        public Stream OpenReadStream() => new MemoryStream();
        public long Length => 6 * 1024 * 1024; // 超过 5MB 上限
        public string FileName => "big.xlsx";
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => "form-data";
        public string Name => "file";
        public Microsoft.AspNetCore.Http.IHeaderDictionary Headers => new Microsoft.AspNetCore.Http.HeaderDictionary();
        public void CopyTo(Stream target) => throw new NotImplementedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task User_Import_空文件与超限文件均拒绝()
    {
        var controller = BuildUser();

        var empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.xlsx"); // Length == 0 分支
        await Assert.ThrowsAsync<DomainException>(() => controller.Import(empty));

        await Assert.ThrowsAsync<DomainException>(() => controller.Import(new BigEmptyFormFile())); // 超限分支
    }

    // ---------- 角色：关键字三路匹配 ----------

    [Fact]
    public async Task Role_Page_关键字按编码命中与全不命中()
    {
        _db.Insertable(new SysRole
        { Id = 1, RoleName = "运营", RoleCode = "ops", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }).ExecuteCommand();
        var controller = new RoleController(new RoleAppService(_db, new SysRoleRepository(_db, _snow), _snow));
        AttachHttp(controller);

        var byCode = await controller.Page(1, 10, keyword: "ops"); // 名称不中、编码命中
        Assert.Single(byCode.Data!.Items);

        var none = await controller.Page(1, 10, keyword: "zzz"); // 双双不中
        Assert.Empty(none.Data!.Items);
    }

    // ---------- 菜单：编辑分支 ----------

    [Fact]
    public async Task Menu_Save_带id_返回修改成功()
    {
        var controller = new MenuController(new MenuAppService(_db, new SysMenuRepository(_db),
            new PermissionAppService(_db), _snow));
        AttachHttp(controller);

        var add = await controller.Save(new MenuSaveRequest { name = "系统管理", path = "/sys", type = "dir" });
        Assert.Equal("新增成功", add.Message);
        var id = _db.Queryable<SysMenu>().First(m => m.MenuName == "系统管理").Id.ToString();

        var update = await controller.Save(new MenuSaveRequest { id = id, name = "系统管理2", path = "/sys2", type = "dir" });
        Assert.True(update.Success);
        Assert.Equal("修改成功", update.Message); // 带 id 的编辑分支
    }

    // ---------- 字典：编辑分支 ----------

    [Fact]
    public async Task Dict_SaveData_带id_返回修改成功()
    {
        var controller = new DictController(new DictAppService(_db, _snow));
        AttachHttp(controller);

        await controller.SaveType(new DictTypeSaveRequest { dictCode = "order_status", dictName = "订单状态" });
        var add = await controller.SaveData(new DictDataSaveRequest { dictCode = "order_status", label = "待支付", value = "1" });
        Assert.Equal("新增成功", add.Message);
        var id = _db.Queryable<SysDictData>().First(d => d.DictCode == "order_status").Id.ToString();

        var update = await controller.SaveData(
            new DictDataSaveRequest { dictCode = "order_status", label = "已支付", value = "2" }, id);
        Assert.True(update.Success);
        Assert.Equal("修改成功", update.Message); // 带 id 的编辑分支
    }

    // ---------- 消息：演示发布缺参 ----------

    [Fact]
    public async Task Message_PublishDemo_缺参_回退默认主题与空内容()
    {
        var mq = LooseProxy.Of<TaoAdmin.Common.Messaging.IMessageQueue>((method, _) =>
            method.ReturnType == typeof(Task) ? Task.CompletedTask : null);
        var controller = new MessageController(new MessageAppService(_db, mq));
        AttachHttp(controller);

        var got = await controller.PublishDemo(new Dictionary<string, string>()); // topic/content 双空分支
        Assert.True(got.Success);
        Assert.Contains("消息已发布", got.Message);
    }

    // ---------- 通知渠道：测试发送成功路 ----------

    [Fact]
    public async Task NotifyChannel_Test_发送成功返回ApiOk()
    {
        _db.Insertable(new SysSiteConfig
        { Id = 1, ConfigKey = "notify_alert_receivers", ConfigValue = "ops@tao.local", UpdatedAt = DateTime.Now }).ExecuteCommand();
        var notifier = new AlertNotifier(_db, _snow, new FakeEmail(),
            new DingTalkSender(_db, new System.Net.Http.HttpClient()));
        var controller = new NotifyChannelController(new NotifyChannelAppService(_db, _snow), notifier,
            _db, new AuditTrail(_db, _snow, NullLogger<AuditTrail>.Instance));
        AttachHttp(controller);

        var test = await controller.Test(new NotifyTestRequest { channel = "email" });
        Assert.True(test.Success); // ok=true 的 ApiOk 分支
        Assert.True(_db.Queryable<SysNotifyLog>().Single().Success);
    }

    // ---------- 工作流：审批人昵称与匿名上下文 ----------

    [Fact]
    public async Task Workflow_Approve_带昵称与缺Name_参数先行求值()
    {
        var controller = new WorkflowController(new WorkflowAppService(_db, _snow));

        // 昵称非空：user.nickname ?? user.username 走昵称侧；任务不存在仍先完成参数求值
        AttachHttp(controller, userId: 1, nickname: "老板");
        await Assert.ThrowsAsync<DomainException>(
            () => controller.Approve("999", new WorkflowApproveRequest { approved = true }));

        // 无 Name 声明：Identity?.Name ?? "" 走空串兜底
        AttachHttp(controller, userId: 1, withName: false);
        await Assert.ThrowsAsync<DomainException>(
            () => controller.Approve("999", new WorkflowApproveRequest { approved = false }));

        // 无任何身份：Identity 为 null 的分支
        AttachHttp(controller, withIdentity: false);
        await Assert.ThrowsAsync<DomainException>(
            () => controller.Approve("999", new WorkflowApproveRequest { approved = true }));
    }

    [Fact]
    public async Task Workflow_Approve_驳回_返回已驳回文案()
    {
        await _db.Insertable(new SysWorkflow
        {
            Id = 100, WorkflowName = "请假审批", WorkflowCode = "leave_audit", Status = 1, TenantId = 0,
            NodesJson = "[{\"name\":\"经理审批\",\"approverRole\":\"manager\"}]"
        }).ExecuteCommandAsync();
        var controller = new WorkflowController(new WorkflowAppService(_db, _snow));
        AttachHttp(controller, userId: 1, name: "tao", roles: new[] { "manager" });

        var submit = await controller.Submit(new WorkflowSubmitRequest { workflowCode = "leave_audit", bizTitle = "李四的请假申请" });
        Assert.True(submit.Success);
        var tasks = await controller.MyTasks();

        var reject = await controller.Approve(tasks.Data![0].id, new WorkflowApproveRequest { approved = false, comment = "不批" });
        Assert.True(reject.Success);
        Assert.Equal("已驳回", reject.Message); // approved=false 的文案分支
    }

    // ---------- 文件：下载四态 ----------

    [Fact]
    public async Task File_Download_非法路径_未登记_与正常文件()
    {
        var root = Path.Combine(Path.GetTempPath(), "tao-file7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // 路径映射规则：/uploads/xxx → {root}/xxx
        var physical = Path.Combine(root, "real.txt");
        await File.WriteAllTextAsync(physical, "hello tao");

        var storage = new TaoAdmin.Common.Storage.LocalFileStorageProvider(root, _snow.NextId);
        var controller = new FileController(new FileAppService(_db, storage, _snow,
            new MemoryCache(new MemoryCacheOptions())));
        AttachHttp(controller);

        Assert.True(await controller.Download("") is Microsoft.AspNetCore.Mvc.NotFoundResult
            or Microsoft.AspNetCore.Mvc.NotFoundObjectResult); // 空路径分支
        Assert.True(await controller.Download("http://evil/x") is Microsoft.AspNetCore.Mvc.NotFoundResult
            or Microsoft.AspNetCore.Mvc.NotFoundObjectResult); // 非 /uploads 前缀分支
        Assert.True(await controller.Download("/uploads/missing.txt") is Microsoft.AspNetCore.Mvc.NotFoundResult
            or Microsoft.AspNetCore.Mvc.NotFoundObjectResult); // 未登记记录分支

        _db.Insertable(new SysFile
        {
            Id = _snow.NextId(), FileName = "real.txt", FileUrl = "/uploads/real.txt",
            ContentType = "text/plain", Size = 8, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        }).ExecuteCommand();
        var file = await controller.Download("/uploads/real.txt"); // 正常下载分支
        var content = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(file);
        Assert.Equal("real.txt", content.FileDownloadName);
        Assert.Equal("text/plain", content.ContentType);

        // ContentType 为空的记录：下载时回退 application/octet-stream
        await File.WriteAllTextAsync(Path.Combine(root, "noct.txt"), "hello tao");
        _db.Insertable(new SysFile
        {
            Id = _snow.NextId(), FileName = "noct.txt", FileUrl = "/uploads/noct.txt",
            Size = 8, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        }).ExecuteCommand();
        var fallback = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(
            await controller.Download("/uploads/noct.txt")); // ContentType ?? 兜底分支
        Assert.Equal("application/octet-stream", fallback.ContentType);

        Directory.Delete(root, true);
    }
}
