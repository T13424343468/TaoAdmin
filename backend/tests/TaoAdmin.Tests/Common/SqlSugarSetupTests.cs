using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using TaoAdmin.Common.Entities;
using TaoAdmin.Common.MultiTenancy;
using Xunit;

namespace TaoAdmin.Tests.Common;

/// <summary>
/// SqlSugar 装配实效测试：真实 DI 容器解析 ISqlSugarClient——
/// 单库/读写分离/租户过滤/AOP 补租户/RunCodeFirst 钩子全覆盖。
/// </summary>
public class SqlSugarSetupTests
{
    private sealed class TenantRow : ITenantEntity
    {
        public long Id { get; set; }
        public long TenantId { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// SQLite :memory: 随连接关闭即丢库，而装配默认 IsAutoCloseConnection=true；
    /// 用共享缓存命名库 + 锚定连接保活，等价于真实库的持久语义。
    /// </summary>
    private static string SharedMemDb()
    {
        var connStr = $"DataSource=file:memdb{Guid.NewGuid():N}?cache=shared";
        var anchor = new SqliteConnection(connStr);
        anchor.Open();
        _anchors.Add(anchor); // 进程结束统一释放
        return connStr;
    }

    private static readonly List<SqliteConnection> _anchors = new();

    private static (ServiceProvider Sp, SqlSugarScope Db) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSqlSugar(config);
        var sp = services.BuildServiceProvider();
        return (sp, (SqlSugarScope)sp.GetRequiredService<ISqlSugarClient>());
    }

    [Fact]
    public void AddSqlSugar_默认单库注册成功()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySql"] = "server=localhost;database=t;uid=u;pwd=p;"
        });
        using var _ = sp;
        Assert.NotNull(db);
        Assert.False(db.CurrentConnectionConfig.SlaveConnectionConfigs?.Count > 0);
    }

    [Fact]
    public void AddSqlSugar_缺省连接串走默认值()
    {
        var (sp, db) = Build(new Dictionary<string, string?>());
        using var _ = sp;
        Assert.NotNull(db);
        Assert.Contains("taoadmin", db.CurrentConnectionConfig.ConnectionString);
    }

    [Fact]
    public void AddSqlSugar_读写分离启用_从库生效()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySql"] = "server=localhost;database=t;uid=u;pwd=p;",
            ["ConnectionStrings:Slaves:0"] = "server=slave1;database=t;",
            ["ConnectionStrings:Slaves:1"] = "server=slave2;database=t;",
            ["ReadWriteSplitting:Enabled"] = "true"
        });
        using var _ = sp;
        Assert.Equal(2, db.CurrentConnectionConfig.SlaveConnectionConfigs!.Count);
    }

    [Fact]
    public void AddSqlSugar_读写分离启用但无从库_仅告警不生效()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ReadWriteSplitting:Enabled"] = "true"
        });
        using var _ = sp;
        Assert.True(db.CurrentConnectionConfig.SlaveConnectionConfigs == null
                    || db.CurrentConnectionConfig.SlaveConnectionConfigs.Count == 0);
    }

    [Fact]
    public void AddSqlSugar_租户过滤启用_查询按租户隔离()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySql"] = SharedMemDb(),
            ["Database:Provider"] = "SQLite",
            ["MultiTenant:Enabled"] = "true"
        });
        using var _ = sp;
        db.CodeFirst.InitTables<TenantRow>();
        db.Insertable(new TenantRow { Id = 1, TenantId = 0, Name = "平台" }).ExecuteCommand();
        db.Insertable(new TenantRow { Id = 2, TenantId = 5, Name = "五号租户" }).ExecuteCommand();

        // 平台层（CurrentId=0）：可见全部
        TenantContext.Set(0);
        Assert.Equal(2, db.Queryable<TenantRow>().Count());

        // 租户 5：可见平台公共 + 本租户
        TenantContext.Set(5);
        Assert.Equal(2, db.Queryable<TenantRow>().Count());
        TenantContext.Set(6);
        Assert.Equal(1, db.Queryable<TenantRow>().Count()); // 仅平台公共
        TenantContext.Set(0);
    }

    [Fact]
    public void AddSqlSugar_租户过滤关闭_查询不过滤()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySql"] = SharedMemDb(),
            ["Database:Provider"] = "SQLite",
            ["MultiTenant:Enabled"] = "false"
        });
        using var _ = sp;
        db.CodeFirst.InitTables<TenantRow>();
        db.Insertable(new TenantRow { Id = 1, TenantId = 5, Name = "五号租户" }).ExecuteCommand();
        TenantContext.Set(6);
        Assert.Equal(1, db.Queryable<TenantRow>().Count()); // 过滤未启用
        TenantContext.Set(0);
    }

    [Fact]
    public void AOP_新增租户实体缺省自动补当前租户()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySql"] = SharedMemDb(),
            ["Database:Provider"] = "SQLite",
            ["MultiTenant:Enabled"] = "true"
        });
        using var _ = sp;
        db.CodeFirst.InitTables<TenantRow>();
        TenantContext.Set(7);
        db.Insertable(new TenantRow { Id = 1, TenantId = 0, Name = "自动补" }).ExecuteCommand();
        Assert.Equal(7, db.Queryable<TenantRow>().First(r => r.Id == 1).TenantId);
        TenantContext.Set(0);
    }

    [Fact]
    public void RunCodeFirst_建表并执行种子回调()
    {
        var (sp, db) = Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MySql"] = SharedMemDb(),
            ["Database:Provider"] = "SQLite"
        });
        using var _ = sp;
        var seeded = false;
        db.RunCodeFirst(d => seeded = true, typeof(TenantRow));
        Assert.True(seeded);
        Assert.Equal(0, db.Queryable<TenantRow>().Count()); // 表已建
    }
}
