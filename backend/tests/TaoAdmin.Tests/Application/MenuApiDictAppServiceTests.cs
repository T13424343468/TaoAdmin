using System.Reflection;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Repositories;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 菜单/接口/字典三服务补账：菜单保存校验（按钮权限码必填/父级不能是自己/parentId 缺省归零）与级联删除、
/// 接口资源增改查与强一致批量删除（触发器注入整批回滚 + 连接销毁走兜底 catch）、
/// 字典类型与数据的校验/幂等更新/软删级联。SQLite 真库实效断言。
/// </summary>
public class MenuApiDictAppServiceTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);

    public MenuApiDictAppServiceTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"menuapidict_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables(typeof(SysMenu), typeof(SysRoleMenu), typeof(SysApi),
            typeof(SysRoleApi), typeof(SysDictType), typeof(SysDictData));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private MenuAppService NewMenuSvc() => new(_db, new SysMenuRepository(_db), new PermissionAppService(_db), _snowflake);
    private ApiAppService NewApiSvc() => new(_db, new SysApiRepository(_db), _snowflake);
    private DictAppService NewDictSvc() => new(_db, _snowflake);

    // ==================== MenuAppService ====================

    [Fact]
    public async Task Menu_Save_ButtonWithoutPermission_ShouldThrow()
    {
        var svc = NewMenuSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new MenuSaveRequest { name = "删除按钮", type = "button", permission = "" }));
        Assert.Contains("权限码", ex.Message);
    }

    [Fact]
    public async Task Menu_Save_Add_ShouldDefaultParentToZero_AndPersistAllFields()
    {
        var svc = NewMenuSvc();
        await svc.SaveAsync(new MenuSaveRequest
        {
            name = "系统管理", type = "dir", path = "/sys", icon = "gear", sort = 3, status = 1, visible = true
        });

        var row = _db.Queryable<SysMenu>().Single();
        Assert.Equal("系统管理", row.MenuName);
        Assert.Equal(0, row.MenuType); // dir → 0
        Assert.Equal(0, row.ParentId); // parentId 缺省归零
        Assert.Equal("/sys", row.Path);
        Assert.Equal("gear", row.Icon);
        Assert.Equal(3, row.OrderNum);
        Assert.True(row.Visible);
        Assert.Equal(1, row.Status);
    }

    [Fact]
    public async Task Menu_Save_AddWithParentId_ShouldParseParent()
    {
        var svc = NewMenuSvc();
        await svc.SaveAsync(new MenuSaveRequest { name = "子菜单", type = "menu", parentId = "42" });
        Assert.Equal(42, _db.Queryable<SysMenu>().Single().ParentId);
    }

    [Fact]
    public async Task Menu_Save_UpdateSelfAsParent_ShouldThrow()
    {
        var svc = NewMenuSvc();
        await svc.SaveAsync(new MenuSaveRequest { name = "菜单", type = "menu" });
        var id = _db.Queryable<SysMenu>().Single().Id;
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new MenuSaveRequest { id = id.ToString(), parentId = id.ToString(), name = "菜单", type = "menu" }));
        Assert.Contains("父级不能是自己", ex.Message);
    }

    [Fact]
    public async Task Menu_Save_Update_ShouldPersistChanges()
    {
        var svc = NewMenuSvc();
        await svc.SaveAsync(new MenuSaveRequest { name = "旧名", type = "menu", status = 1 });
        var id = _db.Queryable<SysMenu>().Single().Id;

        await svc.SaveAsync(new MenuSaveRequest { id = id.ToString(), name = "新名", type = "button", permission = "sys:x", status = 0, visible = false });

        var row = _db.Queryable<SysMenu>().Single(m => m.Id == id);
        Assert.Equal("新名", row.MenuName);
        Assert.Equal(2, row.MenuType);
        Assert.Equal("sys:x", row.PermsCode);
        Assert.Equal(0, row.Status);
        Assert.False(row.Visible);
    }

    [Fact]
    public async Task Menu_Tree_ShouldNestChildren()
    {
        var svc = NewMenuSvc();
        await svc.SaveAsync(new MenuSaveRequest { name = "父", type = "dir" });
        var parentId = _db.Queryable<SysMenu>().Single().Id;
        await svc.SaveAsync(new MenuSaveRequest { name = "子", type = "menu", parentId = parentId.ToString() });

        var tree = await svc.TreeAsync();
        Assert.Single(tree);
        Assert.Single(tree[0].children);
        Assert.Equal("子", tree[0].children[0].name);
    }

    [Fact]
    public async Task Menu_Delete_ShouldCascadeChildrenAndClearRoleLinks()
    {
        var svc = NewMenuSvc();
        await svc.SaveAsync(new MenuSaveRequest { name = "父", type = "dir" });
        var parentId = _db.Queryable<SysMenu>().Single().Id;
        await svc.SaveAsync(new MenuSaveRequest { name = "子", type = "menu", parentId = parentId.ToString() });
        var childId = _db.Queryable<SysMenu>().Single(m => m.ParentId == parentId).Id;
        _db.Insertable(new SysRoleMenu { Id = 1, RoleId = 9, MenuId = childId }).ExecuteCommand();

        var deleted = await svc.DeleteAsync(parentId);

        Assert.Equal(2, deleted); // 父 + 子
        Assert.Equal(0, _db.Queryable<SysMenu>().Count(m => m.IsDeleted == false));
        Assert.Empty(_db.Queryable<SysRoleMenu>().ToList()); // 授权关联硬删
    }

    // ==================== ApiAppService ====================

    [Fact]
    public async Task Api_Save_DuplicatePathMethod_ShouldThrow()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "查询", path = "/api/u", method = "GET" });
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new ApiSaveRequest { name = "重复", path = "/api/u", method = "get" }));
        Assert.Contains("已存在", ex.Message);
    }

    [Fact]
    public async Task Api_Save_Add_NullMethodFallsBackToGet()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "查询", path = "/api/u", method = null!, permission = null });

        var row = _db.Queryable<SysApi>().Single();
        Assert.Equal("GET", row.HttpMethod); // method 缺省回退 GET
        Assert.Equal("", row.PermsCode);
        Assert.Equal(1, row.Status);
    }

    [Fact]
    public async Task Api_Save_Update_ShouldPersistChanges()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "旧", path = "/api/a", method = "GET" });
        var id = _db.Queryable<SysApi>().Single().Id;

        await svc.SaveAsync(new ApiSaveRequest { id = id.ToString(), name = "新", path = "/api/a", method = "post", permission = "api:a", status = 0 });

        var row = _db.Queryable<SysApi>().Single(a => a.Id == id);
        Assert.Equal("新", row.Name);
        Assert.Equal("POST", row.HttpMethod); // 方法统一大写
        Assert.Equal("api:a", row.PermsCode);
        Assert.Equal(0, row.Status);
    }

    [Fact]
    public async Task Api_Page_ShouldFilterByKeyword_AndPaginate()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "用户查询", path = "/api/user/page" });
        await svc.SaveAsync(new ApiSaveRequest { name = "订单查询", path = "/api/order/page" });
        await svc.SaveAsync(new ApiSaveRequest { name = "用户删除", path = "/api/user/delete" });

        var all = await svc.PageAsync(1, 10, null);
        Assert.Equal(3, all.Total);

        var userOnly = await svc.PageAsync(1, 10, "user");
        Assert.Equal(2, userOnly.Total); // 名称或路径命中

        var page2 = await svc.PageAsync(2, 2, null);
        Assert.Single(page2.Items);
    }

    [Fact]
    public async Task Api_ListActive_ShouldExcludeDisabledAndDeleted()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "启用", path = "/api/on" });
        await svc.SaveAsync(new ApiSaveRequest { name = "停用", path = "/api/off", status = 0 });
        var offId = _db.Queryable<SysApi>().Single(a => a.Path == "/api/off").Id;
        await svc.DeleteAsync(offId);

        var list = await svc.ListActiveAsync();
        Assert.Single(list);
        Assert.Equal("/api/on", list[0].path);
    }

    [Fact]
    public async Task Api_Delete_ShouldSoftDelete_AndClearRoleLinks()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "x", path = "/api/x" });
        var id = _db.Queryable<SysApi>().Single().Id;
        _db.Insertable(new SysRoleApi { Id = 1, RoleId = 5, ApiId = id }).ExecuteCommand();

        await svc.DeleteAsync(id);

        Assert.Equal(0, _db.Queryable<SysApi>().Count(a => a.IsDeleted == false));
        Assert.Empty(_db.Queryable<SysRoleApi>().ToList());
    }

    [Fact]
    public async Task Api_BatchDelete_ShouldSkipMissingAndDeduplicate()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "a", path = "/api/a" });
        await svc.SaveAsync(new ApiSaveRequest { name = "b", path = "/api/b" });
        var a = _db.Queryable<SysApi>().Single(x => x.Path == "/api/a").Id;
        var b = _db.Queryable<SysApi>().Single(x => x.Path == "/api/b").Id;

        var deleted = await svc.BatchDeleteAsync(new List<long> { a, b, a, 999999 });

        Assert.Equal(2, deleted); // 去重后两条，缺失跳过
        Assert.Equal(0, _db.Queryable<SysApi>().Count(x => x.IsDeleted == false));
    }

    [Fact]
    public async Task Api_BatchDelete_TriggerFailure_ShouldRollBackWholeBatch()
    {
        var svc = NewApiSvc();
        await svc.SaveAsync(new ApiSaveRequest { name = "a", path = "/api/a" });
        await svc.SaveAsync(new ApiSaveRequest { name = "b", path = "/api/b" });
        var a = _db.Queryable<SysApi>().Single(x => x.Path == "/api/a").Id;
        var b = _db.Queryable<SysApi>().Single(x => x.Path == "/api/b").Id;
        _db.Ado.ExecuteCommand($"CREATE TRIGGER boom BEFORE UPDATE ON sys_api WHEN NEW.Id = {b} BEGIN SELECT RAISE(ABORT, '注入故障'); END");

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.BatchDeleteAsync(new List<long> { a, b }));

        Assert.Contains("回滚", ex.Message);
        Assert.Equal(2, _db.Queryable<SysApi>().Count(x => x.IsDeleted == false)); // 整批回滚，a 的删除被撤销
    }

    [Fact]
    public async Task Api_BatchDelete_TransportFailure_ShouldConvertToBusinessException()
    {
        // UseTranAsync 自身抛异常（连接层故障）→ 走兜底 catch(Exception) 转业务异常
        var client = (ISqlSugarClient)System.Reflection.DispatchProxy.Create<ISqlSugarClient, ThrowingAdoClient>();
        var svc = new ApiAppService(client, new SysApiRepository(_db), _snowflake);
        await svc.SaveAsync(new ApiSaveRequest { name = "a", path = "/api/a" });
        var a = _db.Queryable<SysApi>().Single().Id;

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.BatchDeleteAsync(new List<long> { a }));
        Assert.Contains("回滚", ex.Message);
        Assert.Equal(1, _db.Queryable<SysApi>().Count(x => x.IsDeleted == false)); // 数据未被误删
    }

    /// <summary>连接层故障桩：Ado.UseTranAsync 直接抛异常，模拟事务基础设施故障。</summary>
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

    // ==================== DictAppService ====================

    [Fact]
    public async Task Dict_SaveType_BlankFields_ShouldThrow400()
    {
        var svc = NewDictSvc();
        await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "", dictCode = "c" }, null));
        await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "n", dictCode = " " }, null));
    }

    [Fact]
    public async Task Dict_SaveType_DuplicateCode_ShouldThrow400()
    {
        var svc = NewDictSvc();
        await svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "性别", dictCode = "gender" }, null);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "性别2", dictCode = "gender" }, null));
        Assert.Contains("已存在", ex.Message);
    }

    [Fact]
    public async Task Dict_SaveType_AddAndUpdate()
    {
        var svc = NewDictSvc();
        await svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "性别", dictCode = "gender", status = 1, remark = "r" }, null);
        var added = _db.Queryable<SysDictType>().Single();
        Assert.Equal("gender", added.DictCode);
        Assert.Equal("r", added.Remark);

        await svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "性别改", dictCode = "gender", status = 0, remark = null }, added.Id.ToString());
        var updated = _db.Queryable<SysDictType>().Single(t => t.Id == added.Id);
        Assert.Equal("性别改", updated.DictName); // 编码创建后不可改
        Assert.Equal(0, updated.Status);
        Assert.Null(updated.Remark);
    }

    [Fact]
    public async Task Dict_SaveType_UpdateMissing_ShouldThrow404()
    {
        var svc = NewDictSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "n", dictCode = "c" }, "31337"));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Dict_SaveData_BlankFields_ShouldThrow400()
    {
        var svc = NewDictSvc();
        await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "", label = "l", value = "v" }, null));
        await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "c", label = "l", value = " " }, null));
    }

    [Fact]
    public async Task Dict_SaveData_AddAndUpdate()
    {
        var svc = NewDictSvc();
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "男", value = "M", sort = 1 }, null);
        var added = _db.Queryable<SysDictData>().Single();
        Assert.Equal("M", added.Value);

        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "男改", value = "M2", sort = 5, status = 0 }, added.Id.ToString());
        var updated = _db.Queryable<SysDictData>().Single(d => d.Id == added.Id);
        Assert.Equal("男改", updated.Label);
        Assert.Equal("M2", updated.Value);
        Assert.Equal(5, updated.Sort);
        Assert.Equal(0, updated.Status);
    }

    [Fact]
    public async Task Dict_SaveData_UpdateMissing_ShouldThrow404()
    {
        var svc = NewDictSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "c", label = "l", value = "v" }, "31337"));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Dict_PageTypes_ShouldFilterByKeyword()
    {
        var svc = NewDictSvc();
        await svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "性别", dictCode = "gender" }, null);
        await svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "审批状态", dictCode = "approve" }, null);

        var all = await svc.PageTypesAsync(1, 10, null);
        Assert.Equal(2, all.Total);

        var hit = await svc.PageTypesAsync(1, 10, "approve");
        Assert.Equal(1, hit.Total);
        Assert.Equal("approve", hit.Items[0].dictCode);
    }

    [Fact]
    public async Task Dict_GetItems_ShouldReturnEnabledOnly_SortedBySort()
    {
        var svc = NewDictSvc();
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "男", value = "M", sort = 2 }, null);
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "女", value = "F", sort = 1 }, null);
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "停用", value = "X", sort = 0, status = 0 }, null);
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "other", label = "别家", value = "O" }, null);

        var items = await svc.GetItemsAsync("gender");
        Assert.Equal(2, items.Count); // 停用与其他字典不进
        Assert.Equal("F", items[0].value); // 按 Sort 升序
        Assert.Equal("1", items[0].status);
    }

    [Fact]
    public async Task Dict_DeleteType_ShouldSoftDeleteTypeAndItsData()
    {
        var svc = NewDictSvc();
        await svc.SaveTypeAsync(new DictTypeSaveRequest { dictName = "性别", dictCode = "gender" }, null);
        var typeId = _db.Queryable<SysDictType>().Single().Id;
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "男", value = "M" }, null);

        await svc.DeleteTypeAsync(typeId.ToString());

        Assert.Equal(0, _db.Queryable<SysDictType>().Count(t => t.IsDeleted == false));
        Assert.Equal(0, _db.Queryable<SysDictData>().Count(d => d.IsDeleted == false)); // 明细连带软删
    }

    [Fact]
    public async Task Dict_DeleteData_ShouldSoftDeleteSingleItem()
    {
        var svc = NewDictSvc();
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "男", value = "M" }, null);
        await svc.SaveDataAsync(new DictDataSaveRequest { dictCode = "gender", label = "女", value = "F" }, null);
        var firstId = _db.Queryable<SysDictData>().OrderBy(d => d.Id).First().Id;

        await svc.DeleteDataAsync(firstId.ToString());

        Assert.Equal(1, _db.Queryable<SysDictData>().Count(d => d.IsDeleted == false));
    }
}
