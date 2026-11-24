using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Infrastructure.Persistence;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 终账收尾缺口清零（分支 100% 硬门禁）：
/// ①AlertNotifier：邮件 await 真实挂起侧（假体 Task.FromResult 永同步完成，补 Task.Yield 假体）；
/// ②SerilogLineParser.ShortLevel：11 个级别标签全侧；
/// ③NotifyChannelAppService.SaveAsync：配置值为 null 落空串侧；
/// ④UserAppService：新增/更新带空角色列表与带角色两侧；
/// ⑤Announcement/Api：方法组委托缓存两侧（同进程二次调用）；
/// ⑥RegionSnapshot.CountByLevel：switch 全臂含越界默认侧。
/// </summary>
public class FinalGapSweepTests
{
    // ==================== AlertNotifier：邮件 await 挂起侧 ====================

    /// <summary>先让出再完成的邮件假体：await IsCompleted=false 侧真实挂起</summary>
    private class YieldingEmailSender : IEmailSender
    {
        public int Calls;
        public async Task<(bool Ok, string Message)> SendAsync(string to, string subject, string body)
        {
            await Task.Yield(); // 强制异步完成，覆盖 await 同步/挂起两侧
            Calls++;
            return (true, "发送成功");
        }
    }

    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysSiteConfig, SysAlertRule, SysNotifyLog>();
        return db;
    }

    private static readonly SnowflakeIdGenerator Snow = new(7, 7);

    [Fact]
    public async Task NotifyAsync_邮件渠道_await挂起侧_发送成功留痕()
    {
        var db = CreateDb();
        await db.Insertable(new SysAlertRule
        {
            Id = Snow.NextId(), Name = "r1", EventType = "JOB_FAIL", Threshold = 1, Channels = "email", Enabled = true
        }).ExecuteCommandAsync();
        await db.Insertable(new SysSiteConfig
        {
            Id = Snow.NextId(), ConfigKey = "notify_alert_receivers", ConfigValue = "admin@tao.dev"
        }).ExecuteCommandAsync();

        var email = new YieldingEmailSender();
        var notifier = new AlertNotifier(db, Snow, email,
            new DingTalkSender(db, new System.Net.Http.HttpClient(new YieldingDingTalkHandler())));

        var sent = await notifier.NotifyAsync("JOB_FAIL", "标题", "内容", 1);

        Assert.Equal(1, sent);
        Assert.Equal(1, email.Calls);
        Assert.True(db.Queryable<SysNotifyLog>().First().Success);
    }

    private class YieldingDingTalkHandler : System.Net.Http.HttpMessageHandler
    {
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("{\"errcode\":0,\"errmsg\":\"ok\"}")
            };
        }
    }

    // ==================== SerilogLineParser.ShortLevel：11 个级别标签 ====================

    [Theory]
    [InlineData("DBG", "DEBUG")]
    [InlineData("DEBUG", "DEBUG")]
    [InlineData("Verbose", "DEBUG")]
    [InlineData("Debug", "DEBUG")]
    [InlineData("WRN", "WARN")]
    [InlineData("WARNING", "WARN")]
    [InlineData("Warning", "WARN")]
    [InlineData("ERR", "ERROR")]
    [InlineData("ERROR", "ERROR")]
    [InlineData("Fatal", "ERROR")]
    [InlineData("FTL", "ERROR")]
    [InlineData("Whatever", "INFO")]
    public void Parse_级别标签全侧_映射正确(string raw, string expected)
    {
        var line = $"{{\"@t\":\"2026-11-24T10:00:00.000Z\",\"@l\":\"{raw}\",\"@m\":\"消息\"}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal(expected, entry!.Level);
    }

    // ==================== NotifyChannelAppService：配置值 null 侧 ====================

    [Fact]
    public async Task SaveAsync_配置值为null_按空串落库()
    {
        var db = CreateDb();
        var svc = new NotifyChannelAppService(db, new SnowflakeIdGenerator(3, 3));

        await svc.SaveAsync(new Dictionary<string, string> { ["notify_smtp_host"] = null! });

        var row = db.Queryable<SysSiteConfig>().First(c => c.ConfigKey == "notify_smtp_host");
        Assert.NotNull(row);
        Assert.Equal(string.Empty, row.ConfigValue);
    }

    // ==================== UserAppService：空角色列表与带角色两侧 ====================

    private sealed class FakeUserRepo : TaoAdmin.Domain.Repositories.ISysUserRepository
    {
        public Dictionary<long, SysUser> Users { get; } = new();
        public Dictionary<long, List<long>> RoleReplacements { get; } = new();
        public Task<SysUser?> FindByIdAsync(long id) => Task.FromResult(Users.GetValueOrDefault(id));
        public Task<SysUser?> FindByNameAsync(string userName) =>
            Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));
        public Task<bool> ExistsNameAsync(string userName) => Task.FromResult(Users.Values.Any(u => u.UserName == userName));
        public Task AddAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateProfileAsync(SysUser user) { Users[user.Id] = user; return Task.CompletedTask; }
        public Task UpdateSelfProfileAsync(SysUser user) => Task.CompletedTask;
        public Task SoftDeleteAsync(long id) => Task.CompletedTask;
        public Task TouchLoginAsync(long id) => Task.CompletedTask;
        public Task ResetPasswordAsync(long id, string passwordHash) => Task.CompletedTask;
        public Task ReplaceRolesAsync(long userId, IReadOnlyCollection<long> roleIds)
        { RoleReplacements[userId] = roleIds.ToList(); return Task.CompletedTask; }
        public Task DeleteRoleLinksAsync(long userId) => Task.CompletedTask;
    }

    private sealed class StubMessageQueue : TaoAdmin.Common.Messaging.IMessageQueue
    {
        public string ProviderName => "memory";
        public List<string> Topics { get; } = new();
        public Task PublishAsync(string topic, string body) { Topics.Add(topic); return Task.CompletedTask; }
        public Task SubscribeAsync(string topic, Func<TaoAdmin.Common.Messaging.MessageEnvelope, Task> onMessage,
            CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Save_新增带空角色列表_不触发分配()
    {
        var repo = new FakeUserRepo();
        var svc = new UserAppService(CreateUserDb(), repo, new SnowflakeIdGenerator(4, 4),
            new StubMessageQueue(), new DefaultPolicyStub());

        await svc.SaveAsync(new UserSaveRequest
        {
            username = "emptyroles", password = "Aa@123456", roleIds = new List<string>()
        });

        Assert.Empty(repo.RoleReplacements); // Count==0 与 null 同走跳过侧
    }

    [Fact]
    public async Task Save_更新带空角色列表与带角色_两侧()
    {
        var repo = new FakeUserRepo();
        repo.Users[1] = new SysUser
        {
            Id = 1, UserName = "tao", Password = BCrypt.Net.BCrypt.HashPassword("Aa@123456"),
            NickName = "t", Status = 1, CreatedAt = DateTime.Now
        };
        var svc = new UserAppService(CreateUserDb(), repo, new SnowflakeIdGenerator(4, 4),
            new StubMessageQueue(), new DefaultPolicyStub());

        // 空列表：roleIds != null 为真 → 仍触发分配（空集合）
        await svc.SaveAsync(new UserSaveRequest { id = "1", username = "tao", roleIds = new List<string>() });
        Assert.Empty(repo.RoleReplacements[1]);

        // 非空列表：分配侧
        await svc.SaveAsync(new UserSaveRequest { id = "1", username = "tao", roleIds = new List<string> { "7" } });
        Assert.Equal(new List<long> { 7 }, repo.RoleReplacements[1]);
    }

    private static SqlSugarClient CreateUserDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysUser, SysRole, SysUserRole>();
        return db;
    }

    // ==================== Announcement/Api：委托缓存两侧（二次调用） ====================

    [Fact]
    public async Task LatestVisibleAsync_二次调用_缓存两侧均覆盖()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysAnnouncement>();
        var svc = new AnnouncementAppService(db, new SnowflakeIdGenerator(5, 5));

        // 第一次：缓存未建（创建侧）；第二次：缓存命中（复用侧）
        Assert.Empty(await svc.LatestVisibleAsync());
        await svc.PublishAsync(new AnnouncementSaveRequest { title = "上线", content = "内容" }, 1, "admin");
        var latest = await svc.LatestVisibleAsync();
        _ = latest.Count;
        Assert.Single(latest);
    }

    [Fact]
    public async Task ListActiveAsync_二次调用_缓存两侧均覆盖()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysApi, SysRoleApi>();
        var svc = new ApiAppService(db,
            new TaoAdmin.Infrastructure.Repositories.SysApiRepository(db), new SnowflakeIdGenerator(6, 6));

        // 第一次：空列表（创建侧）；第二次：有数据（复用侧）
        Assert.Empty(await svc.ListActiveAsync());
        await svc.SaveAsync(new ApiSaveRequest { name = "启用", path = "/api/on" });
        var list = await svc.ListActiveAsync();
        Assert.Single(list);
    }

    // ==================== RegionSnapshot.CountByLevel：switch 全臂含越界侧 ====================

    [Fact]
    public void CountByLevel_四级命中与越界默认侧()
    {
        var (l1, l2, l3, l4) = RegionSnapshot.CountByLevel(new[]
        {
            (Code: "110000", Name: "北京市", ParentCode: (string?)"", Level: 1),
            (Code: "110100", Name: "苏州市", ParentCode: (string?)"110000", Level: 2),
            (Code: "110105", Name: "姑苏区", ParentCode: (string?)"110100", Level: 3),
            (Code: "110105001", Name: "街道", ParentCode: (string?)"110105", Level: 4),
            (Code: "9", Name: "越界", ParentCode: (string?)null, Level: 9) // default 侧：不计入任何层
        });
        Assert.Equal(1, l1);
        Assert.Equal(1, l2);
        Assert.Equal(1, l3);
        Assert.Equal(1, l4);
    }

    // ==================== RegionAppService.SearchAsync：命中路径二次调用（缓存两侧） ====================

    [Fact]
    public async Task SearchAsync_命中关键词二次调用_缓存两侧均覆盖()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysRegion>();
        db.Insertable(new SysRegion
        {
            Id = 1, Code = "320500", Name = "苏州市", ParentCode = "320000", Level = 2,
            FullPath = "江苏省 / 苏州市", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        }).ExecuteCommand();
        var svc = new RegionAppService(db);

        // 第一次命中：缓存创建侧；第二次命中：缓存复用侧
        var first = await svc.SearchAsync("苏州");
        var second = await svc.SearchAsync("苏州");
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal("320500", second[0].code);
    }

    // ==================== RegionDataImporter.ParseSnapshot：二次调用（ToDto 缓存两侧） ====================

    [Fact]
    public void ParseSnapshot_二次调用_委托缓存两侧均覆盖()
    {
        const string json = """
            {"version":"v1","source":"测试源","nodes":[
              {"code":"110000","name":"北京市","level":1,"children":null},
              {"code":"320000","name":"江苏省","level":1,"children":[
                {"code":"320500","name":"苏州市","level":2}
              ]}
            ]}
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var first = RegionDataImporter.ParseSnapshot(stream);

        using var stream2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var second = RegionDataImporter.ParseSnapshot(stream2);

        Assert.Equal(2, first.Nodes.Count);
        Assert.Equal(2, second.Nodes.Count);
        Assert.Empty(second.Nodes[0].Children);
        Assert.Single(second.Nodes[1].Children);
    }
}
