using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Common.Storage;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 控制器入口实效测试（第三批）：租户、站内消息、工作流、文件中心。
/// 真应用服务 + SQLite 内存库 + 内存消息队列/内存存储桩，断言 ApiResult 与落库实效。
/// </summary>
public class IdentityControllersBatch3Tests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly SnowflakeIdGenerator _snow = new(5, 5);

    public IdentityControllersBatch3Tests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysTenant, SysMessage, SysWorkflow, SysWorkflowInstance, SysWorkflowTask>();
        _db.CodeFirst.InitTables<SysRole, SysUserRole, SysFile>();
    }

    public void Dispose() => _db.Dispose();

    private static void AttachHttp(Microsoft.AspNetCore.Mvc.ControllerBase c, long userId = 1,
        string name = "admin", string[]? roles = null, long tenantId = 0)
    {
        var identity = new System.Security.Claims.ClaimsIdentity();
        identity.AddClaim(new System.Security.Claims.Claim("UserId", userId.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim("TenantId", tenantId.ToString()));
        identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, name));
        foreach (var r in roles ?? new[] { "admin" })
            identity.AddClaim(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, r));
        c.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) }
        };
    }

    private sealed class FakeMessageQueue : TaoAdmin.Common.Messaging.IMessageQueue
    {
        public string ProviderName => "memory";
        public Task PublishAsync(string topic, string body) => Task.CompletedTask;
        public Task SubscribeAsync(string topic, Func<TaoAdmin.Common.Messaging.MessageEnvelope, Task> onMessage, CancellationToken ct) => Task.CompletedTask;
        public Task<Dictionary<string, long>> GetBacklogAsync() => Task.FromResult(new Dictionary<string, long>());
    }

    private sealed class MemoryStorage : IFileStorageProvider
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public Task<string> SaveAsync(Stream content, string originalName, string contentType)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            var path = $"/uploads/test/{Guid.NewGuid():N}-{originalName}";
            Files[path] = ms.ToArray();
            return Task.FromResult(path);
        }
        public Task<Stream?> OpenAsync(string relativePath)
            => Task.FromResult<Stream?>(Files.TryGetValue(relativePath, out var b) ? new MemoryStream(b) : null);
        public Task DeleteAsync(string relativePath) { Files.Remove(relativePath); return Task.CompletedTask; }
    }

    // ---------- 租户 ----------

    [Fact]
    public async Task Tenant_Save_List_Delete_ShouldRoundTrip()
    {
        var controller = new TenantController(new TenantAppService(_db, _snow));
        AttachHttp(controller);

        var save = await controller.Save(new TenantSaveRequest { tenantName = "道映科技", tenantCode = "taoying", contact = "老板" });
        Assert.True(save.Success);
        Assert.Equal("新增成功", save.Message);

        var list = await controller.List();
        Assert.True(list.Success);
        Assert.Single(list.Data!);
        Assert.Equal("道映科技", list.Data![0].TenantName);
        var id = list.Data[0].Id.ToString();

        var edit = await controller.Save(new TenantSaveRequest { id = id, tenantName = "道映科技2", tenantCode = "taoying", status = 1 });
        Assert.True(edit.Success);
        Assert.Equal("修改成功", edit.Message);

        var batch = await controller.BatchDelete(new BatchIdsRequest { Ids = new List<long> { long.Parse(id), 999 } });
        Assert.True(batch.Success);
        Assert.Contains("批量删除完成", batch.Message);
        Assert.Empty((await controller.List()).Data!);
    }

    // ---------- 站内消息 ----------

    [Fact]
    public async Task Message_Page_MarkRead_BatchRead_ShouldReflectReadState()
    {
        var mq = new FakeMessageQueue();
        var controller = new MessageController(new MessageAppService(_db, mq));
        AttachHttp(controller, userId: 1, roles: new[] { "user" }); // 非超管，只看自己的

        await _db.Insertable(new SysMessage { Id = 11, Title = "告警", Content = "CPU 90%", ReceiverId = 1, IsRead = false }).ExecuteCommandAsync();
        await _db.Insertable(new SysMessage { Id = 12, Title = "私有", Content = "别人的", ReceiverId = 2, IsRead = false }).ExecuteCommandAsync();

        var page = await controller.Page();
        Assert.True(page.Success);
        Assert.Equal(1, page.Data!.Total); // 只见自己的
        Assert.Equal("告警", page.Data.Items[0].Title);

        var mark = await controller.MarkRead("11");
        Assert.True(mark.Success);
        Assert.Equal("已标记已读", mark.Message);

        var batch = await controller.BatchRead(new BatchIdsRequest { Ids = new List<long> { 11, 12 } });
        Assert.True(batch.Success);
        Assert.Contains("已批量标记已读", batch.Message);

        var demo = await controller.PublishDemo(new Dictionary<string, string> { ["topic"] = "demo.test", ["content"] = "hello" });
        Assert.True(demo.Success);
        var stats = controller.Stats();
        Assert.True(stats.Success);
    }

    // ---------- 工作流 ----------

    [Fact]
    public async Task Workflow_Submit_Instances_Detail_Tasks_Approve_ShouldCompleteFlow()
    {
        await _db.Insertable(new SysWorkflow
        {
            Id = 100, WorkflowName = "请假审批", WorkflowCode = "leave_audit", Status = 1, TenantId = 0,
            NodesJson = "[{\"name\":\"经理审批\",\"approverRole\":\"manager\"}]"
        }).ExecuteCommandAsync();

        var controller = new WorkflowController(new WorkflowAppService(_db, _snow));
        AttachHttp(controller, userId: 1, name: "tao", roles: new[] { "manager" });

        var defs = await controller.Definitions();
        Assert.True(defs.Success);
        Assert.Single(defs.Data!);
        Assert.Equal("leave_audit", defs.Data![0].workflowCode);

        var submit = await controller.Submit(new WorkflowSubmitRequest { workflowCode = "leave_audit", bizTitle = "张三的请假申请" });
        Assert.True(submit.Success);
        Assert.Contains("审批已提交", submit.Message);

        var instances = await controller.Instances();
        Assert.True(instances.Success);
        Assert.Equal(1, instances.Data!.Total);
        var instanceId = instances.Data.Items[0].id;

        var detail = await controller.Detail(instanceId);
        Assert.True(detail.Success);

        var tasks = await controller.MyTasks();
        Assert.True(tasks.Success);
        Assert.Single(tasks.Data!); // manager 角色能看到第一个待办

        var approve = await controller.Approve(tasks.Data![0].id, new WorkflowApproveRequest { approved = true, comment = "同意" });
        Assert.True(approve.Success);
        Assert.Equal("审批通过", approve.Message);

        var after = await controller.Instances();
        Assert.Equal(2, after.Data!.Items[0].status); // 已通过
    }

    // ---------- 文件中心 ----------

    [Fact]
    public async Task File_Upload_Stats_Download_Delete_ShouldRoundTrip()
    {
        var storage = new MemoryStorage();
        var controller = new FileController(new FileAppService(_db, storage, _snow, new MemoryCache(new MemoryCacheOptions())));
        AttachHttp(controller, userId: 1, name: "tao");

        // 上传：multipart 表单
        var http = new DefaultHttpContext();
        var form = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        var fileContent = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello file"));
        var formFile = new FormFile(fileContent, 0, fileContent.Length, "file", "note.txt")
        { Headers = new HeaderDictionary(), ContentType = "text/plain" };
        http.Request.Form = new FormCollection(form, new FormFileCollection { formFile });
        controller.ControllerContext.HttpContext = http;

        var upload = await controller.Upload(formFile);
        Assert.True(upload.Success);
        Assert.NotNull(upload.Data);
        Assert.Equal("note.txt", upload.Data!.fileName);

        // 空文件拒绝
        var empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.txt") { Headers = new HeaderDictionary() };
        Assert.False((await controller.Upload(empty)).Success);

        // 统计
        var stats = await controller.Stats();
        Assert.True(stats.Success);

        // 下载：合法路径 + 已登记文件
        var download = await controller.Download(upload.Data.fileUrl);
        Assert.NotNull(download);

        // 下载：非法路径 404
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Download("/etc/passwd"));

        // 删除
        var page = await controller.Page();
        var fileId = page.Data!.Items[0].Id.ToString();
        var del = await controller.Delete(fileId);
        Assert.True(del.Success);
        Assert.Equal("删除成功", del.Message);
    }
}
