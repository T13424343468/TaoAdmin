using SqlSugar;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Repositories;
using Xunit;

namespace TaoAdmin.Tests.Infrastructure;

/// <summary>
/// 系统仓储实效测试：内存 SQLite 走真实增删改查——
/// 用户/角色/菜单/接口四仓储全方法覆盖（含软删、级联、权限替换）。
/// </summary>
public class SysRepositoriesTests
{
    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables(typeof(SysUser), typeof(SysRole), typeof(SysMenu), typeof(SysUserRole), typeof(SysRoleMenu), typeof(SysRoleApi), typeof(SysApi));
        return db;
    }

    private static (SysUserRepository User, SysRoleRepository Role, SysMenuRepository Menu, SysApiRepository Api) CreateRepos(SqlSugarClient db)
    {
        var snowflake = new SnowflakeIdGenerator(1, 1);
        return (new SysUserRepository(db, snowflake), new SysRoleRepository(db, snowflake),
                new SysMenuRepository(db), new SysApiRepository(db));
    }

    private static SysUser NewUser(string name = "u1") => new()
    {
        UserName = name, Password = "hash", NickName = name, Phone = "13800000000",
        Email = $"{name}@t.cn", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
    };

    [Fact]
    public async Task 用户仓储_全方法实效()
    {
        using var db = CreateDb();
        var repo = CreateRepos(db).User;

        await repo.AddAsync(NewUser());
        var added = await repo.FindByNameAsync("u1");
        Assert.NotNull(added);
        Assert.Equal("u1", added!.UserName);
        Assert.Null(await repo.FindByIdAsync(999)); // 不存在

        added.NickName = "改名";
        await repo.UpdateProfileAsync(added);
        Assert.Equal("改名", (await repo.FindByIdAsync(added.Id))!.NickName);

        added.Avatar = "a.png";
        await repo.UpdateSelfProfileAsync(added);
        Assert.Equal("a.png", (await repo.FindByIdAsync(added.Id))!.Avatar);

        Assert.True(await repo.ExistsNameAsync("u1"));
        Assert.False(await repo.ExistsNameAsync("nobody"));

        await repo.TouchLoginAsync(added.Id);
        Assert.NotNull((await repo.FindByIdAsync(added.Id))!.LastLoginTime);

        await repo.ResetPasswordAsync(added.Id, "newhash");
        Assert.Equal("newhash", (await repo.FindByIdAsync(added.Id))!.Password);

        // 角色替换：空集合直接清空返回
        await repo.ReplaceRolesAsync(added.Id, new List<long> { 11, 22 });
        Assert.Equal(2, db.Queryable<SysUserRole>().Count(ur => ur.UserId == added.Id));
        await repo.ReplaceRolesAsync(added.Id, new List<long>());
        Assert.Equal(0, db.Queryable<SysUserRole>().Count(ur => ur.UserId == added.Id));
        await repo.DeleteRoleLinksAsync(added.Id);

        await repo.SoftDeleteAsync(added.Id);
        Assert.Null(await repo.FindByIdAsync(added.Id)); // 软删后查不到
    }

    [Fact]
    public async Task 角色仓储_全方法实效()
    {
        using var db = CreateDb();
        var (userRepo, roleRepo, _, _) = CreateRepos(db);

        var role = new SysRole
        {
            RoleName = "管理员", RoleCode = "admin", Status = 1,
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        await roleRepo.AddAsync(role);
        Assert.NotNull(await roleRepo.FindByIdAsync(role.Id));
        Assert.Null(await roleRepo.FindByIdAsync(999));
        Assert.True(await roleRepo.ExistsCodeAsync("admin"));
        Assert.False(await roleRepo.ExistsCodeAsync("nope"));

        role.RoleName = "超管";
        await roleRepo.UpdateAsync(role);
        Assert.Equal("超管", (await roleRepo.FindByIdAsync(role.Id))!.RoleName);

        // 权限替换：menu/api 均非空
        await roleRepo.ReplacePermissionsAsync(role.Id, new List<long> { 1, 2 }, new List<long> { 3 });
        Assert.Equal(2, db.Queryable<SysRoleMenu>().Count(rm => rm.RoleId == role.Id));
        Assert.Equal(1, db.Queryable<SysRoleApi>().Count(ra => ra.RoleId == role.Id));
        // menu 为 null 不动、api 空 collection 清空
        await roleRepo.ReplacePermissionsAsync(role.Id, null, new List<long>());
        Assert.Equal(2, db.Queryable<SysRoleMenu>().Count(rm => rm.RoleId == role.Id));
        Assert.Equal(0, db.Queryable<SysRoleApi>().Count(ra => ra.RoleId == role.Id));

        // 级联软删：角色软删 + 三张关联表清空
        await userRepo.AddAsync(NewUser());
        var user = await userRepo.FindByNameAsync("u1");
        db.Insertable(new SysUserRole { Id = 1, UserId = user!.Id, RoleId = role.Id }).ExecuteCommand();
        await roleRepo.SoftDeleteCascadeAsync(role.Id);
        Assert.Null(await roleRepo.FindByIdAsync(role.Id));
        Assert.Equal(0, db.Queryable<SysRoleMenu>().Count(rm => rm.RoleId == role.Id));
        Assert.Equal(0, db.Queryable<SysUserRole>().Count(ur => ur.RoleId == role.Id));
    }

    [Fact]
    public async Task 菜单仓储_级联软删返回节点数()
    {
        using var db = CreateDb();
        var (_, _, menuRepo, _) = CreateRepos(db);

        var root = new SysMenu { Id = 1001, MenuName = "系统", MenuType = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        await menuRepo.AddAsync(root);
        var child = new SysMenu { Id = 1002, ParentId = root.Id, MenuName = "用户", MenuType = 2, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        await menuRepo.AddAsync(child);
        var grand = new SysMenu { Id = 1003, ParentId = child.Id, MenuName = "按钮", MenuType = 3, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        await menuRepo.AddAsync(grand);

        var count = await menuRepo.SoftDeleteCascadeAsync(root.Id);
        Assert.Equal(3, count); // 根+子+孙
        Assert.Equal(0, db.Queryable<SysMenu>().Count(m => m.IsDeleted == false));
    }

    [Fact]
    public async Task 接口仓储_全方法实效()
    {
        using var db = CreateDb();
        var (_, _, _, apiRepo) = CreateRepos(db);

        var api = new SysApi
        {
            Name = "Demo-Page", Path = "/api/demo", HttpMethod = "GET",
            PermsCode = "demo:list", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        };
        await apiRepo.AddAsync(api);
        Assert.True(await apiRepo.ExistsAsync("/api/demo", "GET"));
        Assert.False(await apiRepo.ExistsAsync("/api/demo", "POST"));
        Assert.False(await apiRepo.ExistsAsync("/api/other", "GET"));

        api.Name = "Demo-List";
        await apiRepo.UpdateAsync(api);
        Assert.Equal("Demo-List", db.Queryable<SysApi>().First(a => a.Id == api.Id).Name);

        await apiRepo.SoftDeleteAsync(api.Id);
        Assert.False(await apiRepo.ExistsAsync("/api/demo", "GET")); // 软删后不存在
        Assert.Equal(0, db.Queryable<SysRoleApi>().Count(ra => ra.ApiId == api.Id)); // 关联清空
    }
}
