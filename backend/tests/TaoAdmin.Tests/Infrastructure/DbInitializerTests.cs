using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Persistence;
using TaoAdmin.Common.Snowflake;
using SqlSugar;
using Xunit;

namespace TaoAdmin.Tests.Infrastructure;

/// <summary>
/// DbInitializer 实效测试：真实 SQLite 内存库走完整初始化链——
/// 建表、基础种子（admin/角色/菜单/接口资源）、幂等重跑、接口资源增量同步。
/// </summary>
public class DbInitializerTests
{
    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        return db;
    }

    private static void InitializeAll(SqlSugarClient db)
        => DbInitializer.Initialize(db, new SnowflakeIdGenerator(1, 1), typeof(DbInitializerTests).Assembly);

    [Fact]
    public void Initialize_空库完整建表并种子()
    {
        using var db = CreateDb();
        InitializeAll(db);

        // 基础种子：admin 账号、内置角色、菜单、接口资源（含演示用户等）
        Assert.True(db.Queryable<SysUser>().Count() >= 1);
        var admin = db.Queryable<SysUser>().First(u => u.UserName == DbInitializer.AdminUserName);
        Assert.NotNull(admin);
        Assert.True(db.Queryable<SysRole>().Count() > 0);
        Assert.True(db.Queryable<SysMenu>().Count() > 0);
        Assert.True(db.Queryable<SysApi>().Count() > 0);
        // 字典、租户、工作流模板等新功能种子
        Assert.True(db.Queryable<SysDictType>().Count() > 0);
        Assert.True(db.Queryable<SysTenant>().Count() > 0);
    }

    [Fact]
    public void Initialize_重复执行幂等不重复种子()
    {
        using var db = CreateDb();
        InitializeAll(db);
        var users = db.Queryable<SysUser>().Count();
        var menus = db.Queryable<SysMenu>().Count();
        var apis = db.Queryable<SysApi>().Count();

        InitializeAll(db);

        Assert.Equal(users, db.Queryable<SysUser>().Count());
        Assert.Equal(menus, db.Queryable<SysMenu>().Count());
        Assert.Equal(apis, db.Queryable<SysApi>().Count());
    }

    [Fact]
    public void Initialize_已有用户跳过基础种子()
    {
        using var db = CreateDb();
        // 先建表再手工放一个非 admin 用户，验证基础种子被跳过
        db.CodeFirst.InitTables<SysUser>();
        db.CodeFirst.InitTables<SysRole>();
        db.Insertable(new SysUser { UserName = "someone", Password = "x", NickName = "s" }).ExecuteCommand();
        // 租户种子依赖基础种子产生的 common 角色，跳过基础种子时需已存在
        db.Insertable(new SysRole { RoleName = "普通用户", RoleCode = "common", Status = 1 }).ExecuteCommand();

        InitializeAll(db);

        Assert.DoesNotContain(db.Queryable<SysUser>().ToList(), u => u.UserName == DbInitializer.AdminUserName); // 基础种子被跳过
    }
}
