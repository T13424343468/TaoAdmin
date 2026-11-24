using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第二批）：公告、字典、操作日志。
/// 真应用服务 + SQLite 内存库，断言 ApiResult 与落库实效。
/// </summary>
public class IdentityControllersBatch2Tests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly SnowflakeIdGenerator _snow = new(4, 4);

    public IdentityControllersBatch2Tests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysAnnouncement, SysDictType, SysDictData, SysOperationLog>();
    }

    public void Dispose() => _db.Dispose();

    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId = 1, string name = "admin")
    {
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim("UserName", name));
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) }
        };
    }

    // ---------- 公告 ----------

    [Fact]
    public async Task Announcement_Publish_Page_SetStatus_Delete_ShouldRoundTrip()
    {
        var controller = new AnnouncementController(new AnnouncementAppService(_db, _snow));
        AttachHttp(controller);

        var publish = await controller.Publish(new AnnouncementSaveRequest { title = "停机维护", content = "周六 02:00-04:00" });
        Assert.True(publish.Success);
        Assert.Equal("发布成功", publish.Message);

        var page = await controller.Page();
        Assert.True(page.Success);
        Assert.Equal(1, page.Data!.Total);
        Assert.Equal("停机维护", page.Data.Items[0].title);

        var id = page.Data.Items[0].id;
        var offline = await controller.SetStatus(id, new Dictionary<string, bool> { ["online"] = false });
        Assert.True(offline.Success);
        Assert.Equal("公告已下线", offline.Message);

        var latest = await controller.Latest();
        Assert.True(latest.Success);
        Assert.Empty(latest.Data!); // 已下线不可见

        var del = await controller.Delete(id);
        Assert.True(del.Success);
        Assert.Equal("删除成功", del.Message);
        Assert.Equal(0, (await controller.Page()).Data!.Total);
    }

    [Fact]
    public async Task Announcement_BatchDelete_And_BatchStatus_ShouldReportCounts()
    {
        var controller = new AnnouncementController(new AnnouncementAppService(_db, _snow));
        AttachHttp(controller);
        foreach (var t in new[] { "甲", "乙", "丙" })
            await controller.Publish(new AnnouncementSaveRequest { title = t, content = "内容" });
        var ids = (await controller.Page(pageSize: 10)).Data!.Items.Select(i => long.Parse(i.id)).ToList();

        var batchStatus = await controller.BatchStatus(new BatchStatusRequest { Ids = ids.Take(2).ToList(), status = 0 });
        Assert.True(batchStatus.Success);
        Assert.Contains("批量操作完成", batchStatus.Message);

        var batchDelete = await controller.BatchDelete(new BatchIdsRequest { Ids = ids });
        Assert.True(batchDelete.Success);
        Assert.Contains("批量删除完成", batchDelete.Message);
        Assert.Equal(0, (await controller.Page(pageSize: 10)).Data!.Total);
    }

    // ---------- 字典 ----------

    [Fact]
    public async Task Dict_SaveType_SaveData_Items_Delete_ShouldRoundTrip()
    {
        var controller = new DictController(new DictAppService(_db, _snow));
        AttachHttp(controller);

        var saveType = await controller.SaveType(new DictTypeSaveRequest { dictCode = "order_status", dictName = "订单状态" });
        Assert.True(saveType.Success);
        Assert.Equal("新增成功", saveType.Message);

        var typeId = (await controller.Types()).Data!.Items[0].id;
        var editType = await controller.SaveType(new DictTypeSaveRequest { dictCode = "order_status", dictName = "订单状态2" }, typeId);
        Assert.True(editType.Success);
        Assert.Equal("修改成功", editType.Message);

        var saveData = await controller.SaveData(new DictDataSaveRequest { dictCode = "order_status", label = "待支付", value = "1" });
        Assert.True(saveData.Success);
        Assert.Equal("新增成功", saveData.Message);

        var items = await controller.Items("order_status");
        Assert.True(items.Success);
        Assert.Single(items.Data!);
        Assert.Equal("待支付", items.Data![0].label);

        var types = await controller.Types();
        Assert.True(types.Success);
        Assert.Equal(1, types.Data!.Total);

        var dataId = (await controller.Items("order_status")).Data![0].id;
        Assert.True((await controller.DeleteData(dataId)).Success);
        Assert.True((await controller.DeleteType(typeId)).Success);
        Assert.Equal(0, (await controller.Types()).Data!.Total);
    }

    // ---------- 操作日志 ----------

    [Fact]
    public async Task OperationLog_Page_ShouldFilterByCategoryAndUser()
    {
        await _db.Insertable(new SysOperationLog
        {
            Id = _snow.NextId(), Category = "login", UserName = "tao", Success = 1,
            Method = "POST", Path = "/login", CreatedAt = DateTime.Now
        }).ExecuteCommandAsync();
        await _db.Insertable(new SysOperationLog
        {
            Id = _snow.NextId(), Category = "operation", UserName = "lee", Success = 0,
            Method = "DELETE", Path = "/users/1", CreatedAt = DateTime.Now
        }).ExecuteCommandAsync();

        var controller = new OperationLogController(new OperationLogAppService(_db, new MemoryCache(new MemoryCacheOptions())));
        AttachHttp(controller);

        var all = await controller.Page();
        Assert.True(all.Success);
        Assert.Equal(2, all.Data!.Total);

        var onlyLogin = await controller.Page(category: "login");
        Assert.Equal(1, onlyLogin.Data!.Total);
        Assert.Equal("tao", onlyLogin.Data.Items[0].UserName);

        var onlyFailed = await controller.Page(success: 0);
        Assert.Equal(1, onlyFailed.Data!.Total);
        Assert.Equal("lee", onlyFailed.Data.Items[0].UserName);
    }
}
