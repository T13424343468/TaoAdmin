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

    [Fact]
    public void Initialize_老库示例菜单挂错父级_重跑归并到示例演示()
    {
        using var db = CreateDb();
        InitializeAll(db); // 首次初始化，示例菜单挂在「示例演示」

        // 模拟老库增量：把租户菜单改挂到「系统管理」下
        var sysDir = db.Queryable<SysMenu>().First(m => m.MenuName == "系统管理" && m.ParentId == 0);
        var tenant = db.Queryable<SysMenu>().First(m => m.Component == "system/tenant/index");
        tenant.ParentId = sysDir.Id;
        db.Updateable(tenant).ExecuteCommand();

        InitializeAll(db); // 幂等重跑，归并逻辑应把它挪回「示例演示」

        var demoDir = db.Queryable<SysMenu>().First(m => m.MenuName == "示例演示" && m.ParentId == 0);
        var after = db.Queryable<SysMenu>().First(m => m.Component == "system/tenant/index");
        Assert.Equal(demoDir.Id, after.ParentId); // 归并完成
    }

    [Fact]
    public void Initialize_老库区划菜单缺更新权限_重跑补插权限并绑管理员()
    {
        using var db = CreateDb();
        InitializeAll(db); // 首次初始化，区划菜单与权限齐全

        // 模拟老库：菜单在、更新权限按钮被删
        var perm = db.Queryable<SysMenu>().First(m => m.PermsCode == "sys:region:manage");
        db.Deleteable<SysRoleMenu>().Where(rm => rm.MenuId == perm.Id).ExecuteCommand();
        db.Deleteable(perm).ExecuteCommand();

        InitializeAll(db); // 重跑，走老库补权限分支

        var restored = db.Queryable<SysMenu>().First(m => m.PermsCode == "sys:region:manage");
        Assert.True(restored.Id > 0); // 权限已补回
        var menu = db.Queryable<SysMenu>().First(m => m.Component == "system/region/index");
        Assert.Equal(menu.Id, restored.ParentId); // 挂在区划菜单下
        var adminRole = db.Queryable<SysRole>().First(r => r.RoleCode == "admin");
        Assert.True(db.Queryable<SysRoleMenu>().Any(rm => rm.RoleId == adminRole.Id && rm.MenuId == restored.Id)); // 已绑管理员
    }
}
