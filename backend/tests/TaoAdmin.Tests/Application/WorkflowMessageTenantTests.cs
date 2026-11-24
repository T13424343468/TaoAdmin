using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Messaging;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 工作流/租户/消息三服务补账：审批全链路（发起→逐级通过→驳回→完成）、
/// 租户增删改与批量容错、消息分页已读与队列演示。SQLite 真库实效断言。
/// </summary>
public class WorkflowMessageTenantTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly string _dbFile;
    private readonly SnowflakeIdGenerator _snowflake = new(1, 1);

    public WorkflowMessageTenantTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), $"wfmt_{Guid.NewGuid():N}.db");
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource={_dbFile}",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysWorkflow, SysWorkflowInstance, SysWorkflowTask, SysTenant, SysMessage>();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }

    private WorkflowAppService NewWorkflowSvc() => new(_db, _snowflake);
    private TenantAppService NewTenantSvc() => new(_db, _snowflake);
    private MessageAppService NewMessageSvc(IMessageQueue? mq = null) => new(_db, mq ?? new RecordingMq());

    private static WorkflowNodeDto Node(string name, string role) => new() { name = name, approverRole = role };

    private long SeedWorkflow(string code = "leave", int nodeCount = 2, int status = 1)
    {
        var wf = new SysWorkflow
        {
            Id = _snowflake.NextId(),
            WorkflowName = "请假审批",
            WorkflowCode = code,
            Status = status,
            NodesJson = System.Text.Json.JsonSerializer.Serialize(
                Enumerable.Range(0, nodeCount).Select(i => Node($"节点{i + 1}", $"role{i}")).ToList())
        };
        _db.Insertable(wf).ExecuteCommand();
        return wf.Id;
    }

    private long SeedInstance(long workflowId, string title = "张三的请假", int status = 1, int nodeIndex = 0, long applicantId = 100)
    {
        var inst = new SysWorkflowInstance
        {
            Id = _snowflake.NextId(), WorkflowId = workflowId, WorkflowName = "请假审批",
            BizTitle = title, ApplicantId = applicantId, ApplicantName = "张三",
            CurrentNodeIndex = nodeIndex, Status = status
        };
        _db.Insertable(inst).ExecuteCommand();
        return inst.Id;
    }

    private long SeedTask(long instanceId, int nodeIndex, string role = "role0", int status = 0, long tenantId = 0)
    {
        var task = new SysWorkflowTask
        {
            Id = _snowflake.NextId(), InstanceId = instanceId, NodeName = $"节点{nodeIndex + 1}",
            NodeIndex = nodeIndex, AssigneeRoleCode = role, TenantId = tenantId, Status = status
        };
        _db.Insertable(task).ExecuteCommand();
        return task.Id;
    }

    // ==================== WorkflowAppService ====================

    [Fact]
    public async Task Submit_BlankCodeOrTitle_ShouldThrow400()
    {
        var svc = NewWorkflowSvc();
        await Assert.ThrowsAsync<DomainException>(() => svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "", bizTitle = "t" }, 1, "u"));
        await Assert.ThrowsAsync<DomainException>(() => svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "c", bizTitle = " " }, 1, "u"));
    }

    [Fact]
    public async Task Submit_WorkflowMissingOrDisabled_ShouldThrow404()
    {
        var svc = NewWorkflowSvc();
        SeedWorkflow("leave", status: 0); // 停用
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "nope", bizTitle = "t" }, 1, "u"));
        Assert.Equal(404, ex.Code);
        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "leave", bizTitle = "t" }, 1, "u"));
        Assert.Equal(404, ex2.Code);
    }

    [Fact]
    public async Task Submit_WithNoNodes_ShouldThrow400()
    {
        var svc = NewWorkflowSvc();
        var wfId = _snowflake.NextId();
        _db.Insertable(new SysWorkflow { Id = wfId, WorkflowName = "空流程", WorkflowCode = "empty", Status = 1, NodesJson = null }).ExecuteCommand();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SubmitAsync(new WorkflowSubmitRequest { workflowCode = "empty", bizTitle = "t" }, 1, "u"));
        Assert.Equal(400, ex.Code);
    }

    [Fact]
    public async Task Submit_Valid_ShouldCreateInstanceAndFirstTask()
    {
        var svc = NewWorkflowSvc();
        SeedWorkflow("leave", nodeCount: 2);

        var instanceId = await svc.SubmitAsync(
            new WorkflowSubmitRequest { workflowCode = "leave", bizTitle = "李四的请假", remark = "备注" }, 42, "李四", tenantId: 7);

        var inst = _db.Queryable<SysWorkflowInstance>().First(i => i.Id == long.Parse(instanceId));
        Assert.Equal("李四的请假", inst.BizTitle);
        Assert.Equal(42, inst.ApplicantId);
        Assert.Equal(7, inst.TenantId);
        Assert.Equal(1, inst.Status);
        var task = _db.Queryable<SysWorkflowTask>().First(t => t.InstanceId == inst.Id);
        Assert.Equal(0, task.NodeIndex);
        Assert.Equal("role0", task.AssigneeRoleCode);
        Assert.Equal(7, task.TenantId);
    }

    [Fact]
    public async Task GetMyTasks_SuperAdminSeesAll_NonSuperFilteredByRoleAndTenant()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow();
        var i1 = SeedInstance(wfId, applicantId: 1);
        SeedTask(i1, 0, role: "roleA", tenantId: 0);
        SeedTask(i1, 0, role: "roleB", tenantId: 0);
        var i2 = SeedInstance(wfId, applicantId: 2);
        SeedTask(i2, 0, role: "roleA", tenantId: 9);

        var super = await svc.GetMyTasksAsync(1, new List<string>(), isSuperAdmin: true);
        Assert.Equal(3, super.Count);

        var nonSuper = await svc.GetMyTasksAsync(1, new List<string> { "roleA" }, isSuperAdmin: false, tenantId: 9);
        Assert.Equal(2, nonSuper.Count); // roleA 且（公共租户 0 或本租户 9）的两条都可见
    }

    [Fact]
    public async Task Approve_TaskMissing_ShouldThrow404()
    {
        var svc = NewWorkflowSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.ApproveAsync(999, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string> { "r" }, false));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Approve_AlreadyHandled_ShouldThrow400()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow();
        var instId = SeedInstance(wfId);
        var taskId = SeedTask(instId, 0, status: 1);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.ApproveAsync(taskId, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string>(), true));
        Assert.Equal(400, ex.Code);
    }

    [Fact]
    public async Task Approve_WithoutRole_ShouldThrow403()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow();
        var instId = SeedInstance(wfId);
        var taskId = SeedTask(instId, 0, role: "needRole");
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.ApproveAsync(taskId, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string> { "other" }, false));
        Assert.Equal(403, ex.Code);
        Assert.Contains("needRole", ex.Message);
    }

    [Fact]
    public async Task Approve_OtherTenantTask_ShouldThrow403()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow();
        var instId = SeedInstance(wfId);
        var taskId = SeedTask(instId, 0, role: "role0", tenantId: 5);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.ApproveAsync(taskId, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string> { "role0" }, false, tenantId: 6));
        Assert.Equal(403, ex.Code);
    }

    [Fact]
    public async Task Approve_InstanceMissing_ShouldThrow404()
    {
        var svc = NewWorkflowSvc();
        // 任务存在但实例未落库（实例查询无软删过滤，只能靠缺行构造 404）
        var task = new SysWorkflowTask { Id = _snowflake.NextId(), InstanceId = 888888, NodeIndex = 0, AssigneeRoleCode = "r", Status = 0 };
        _db.Insertable(task).ExecuteCommand();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.ApproveAsync(task.Id, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string>(), true));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Approve_Reject_ShouldTerminateInstance()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow(nodeCount: 2);
        var instId = SeedInstance(wfId);
        var taskId = SeedTask(instId, 0);

        await svc.ApproveAsync(taskId, new WorkflowApproveRequest { approved = false, comment = "不行" }, 1, "组长", new List<string> { "role0" }, true);

        var inst = _db.Queryable<SysWorkflowInstance>().First(i => i.Id == instId);
        Assert.Equal(3, inst.Status); // 已驳回
        Assert.NotNull(inst.FinishedAt);
        var task = _db.Queryable<SysWorkflowTask>().First(t => t.Id == taskId);
        Assert.Equal(2, task.Status);
        Assert.Equal("不行", task.Comment);
        Assert.Equal("组长", task.HandlerName);
    }

    [Fact]
    public async Task Approve_MidNode_ShouldAdvanceAndCreateNextTaskWithTenantInherited()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow(nodeCount: 2);
        var instId = SeedInstance(wfId);
        var taskId = SeedTask(instId, 0, tenantId: 3);

        await svc.ApproveAsync(taskId, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string>(), true, tenantId: 9);

        var inst = _db.Queryable<SysWorkflowInstance>().First(i => i.Id == instId);
        Assert.Equal(1, inst.Status); // 仍进行中
        Assert.Equal(1, inst.CurrentNodeIndex);
        var next = _db.Queryable<SysWorkflowTask>().First(t => t.NodeIndex == 1 && t.InstanceId == instId);
        Assert.Equal(3, next.TenantId); // 从表任务租户非 0 时继承任务租户
    }

    [Fact]
    public async Task Approve_FinalNode_ShouldCompleteInstance()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow(nodeCount: 1);
        var instId = SeedInstance(wfId);
        var taskId = SeedTask(instId, 0);

        await svc.ApproveAsync(taskId, new WorkflowApproveRequest { approved = true }, 1, "h", new List<string>(), true, tenantId: 4);

        var inst = _db.Queryable<SysWorkflowInstance>().First(i => i.Id == instId);
        Assert.Equal(2, inst.Status); // 已通过
        Assert.NotNull(inst.FinishedAt);
    }

    [Fact]
    public async Task GetInstances_FiltersAndMapping()
    {
        var svc = NewWorkflowSvc();
        var wfId = SeedWorkflow();
        SeedInstance(wfId, title: "甲", status: 1, applicantId: 1);
        SeedInstance(wfId, title: "乙", status: 2, applicantId: 1);
        SeedInstance(wfId, title: "丙", status: 2, applicantId: 2);
        // 孤儿实例：流程定义被删后 nodesByWf 无此键 → currentNode 兜底空串
        SeedInstance(999999, title: "孤儿", status: 5, applicantId: 1);

        var all = await svc.GetInstancesAsync(1, 10, userId: 1, isSuperAdmin: true);
        Assert.Equal(4, all.Total);

        var mine = await svc.GetInstancesAsync(1, 10, userId: 1, isSuperAdmin: false);
        Assert.Equal(3, mine.Total);

        var passed = await svc.GetInstancesAsync(1, 10, userId: 1, isSuperAdmin: true, status: 2);
        Assert.Equal(2, passed.Total);

        var orphan = all.Items.First(x => x.bizTitle == "孤儿");
        Assert.Equal("", orphan.currentNode);
        Assert.Equal("未知", orphan.statusText); // 状态 5 不在映射表

        var running = all.Items.First(x => x.bizTitle == "甲");
        Assert.Equal("进行中", running.statusText);
        Assert.Equal("节点1", running.currentNode);
    }

    [Fact]
    public async Task GetDetail_MissingOrFound()
    {
        var svc = NewWorkflowSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.GetDetailAsync(424242));
        Assert.Equal(404, ex.Code);

        var wfId = SeedWorkflow(nodeCount: 2);
        var instId = SeedInstance(wfId, title: "详情单");
        SeedTask(instId, 1, role: "role1");
        SeedTask(instId, 0, role: "role0");
        var (dto, tasks) = await svc.GetDetailAsync(instId);
        Assert.Equal("详情单", dto.bizTitle);
        Assert.Equal(2, tasks.Count);
        Assert.Equal("节点1", tasks[0].nodeName); // 按 NodeIndex 排序
    }

    [Fact]
    public async Task GetMyTasks_EmptyInstanceMap_ShouldNotThrow()
    {
        // ToDtos 空任务列表：instanceIds 为空走空实例分支
        var svc = NewWorkflowSvc();
        var empty = await svc.GetMyTasksAsync(1, new List<string>(), true);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task GetDefinitions_ShouldReturnParsedNodes()
    {
        var svc = NewWorkflowSvc();
        SeedWorkflow("a", nodeCount: 2);
        _db.Insertable(new SysWorkflow { Id = _snowflake.NextId(), WorkflowName = "坏JSON", WorkflowCode = "bad", Status = 1, NodesJson = "{not json" }).ExecuteCommand();
        _db.Insertable(new SysWorkflow { Id = _snowflake.NextId(), WorkflowName = "停用", WorkflowCode = "off", Status = 0, NodesJson = "[]" }).ExecuteCommand();

        var defs = await svc.GetDefinitionsAsync();
        Assert.Equal(2, defs.Count);
        var ok = defs.First(d => d.workflowCode == "a");
        Assert.Equal(2, ok.nodes.Count);
        Assert.Equal("节点1", ok.nodes[0].name);
        var bad = defs.First(d => d.workflowCode == "bad");
        Assert.Empty(bad.nodes); // 坏 JSON 兜底空列表
    }

    // ==================== TenantAppService ====================

    [Fact]
    public async Task Tenant_Save_BlankFields_ShouldThrow400()
    {
        var svc = NewTenantSvc();
        await Assert.ThrowsAsync<DomainException>(() => svc.SaveAsync(0, "", "c", null, null, 1));
        await Assert.ThrowsAsync<DomainException>(() => svc.SaveAsync(0, "n", " ", null, null, 1));
    }

    [Fact]
    public async Task Tenant_Save_DuplicateCode_ShouldThrow400()
    {
        var svc = NewTenantSvc();
        await svc.SaveAsync(0, "甲租户", "tA", null, null, 1);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.SaveAsync(0, "乙租户", "tA", null, null, 1));
        Assert.Equal(400, ex.Code);
    }

    [Fact]
    public async Task Tenant_Save_AddAndUpdate()
    {
        var svc = NewTenantSvc();
        await svc.SaveAsync(0, "甲租户", "tA", "联系人", "备注", 1);
        var added = _db.Queryable<SysTenant>().First(t => t.TenantCode == "tA");
        Assert.Equal("甲租户", added.TenantName);

        await svc.SaveAsync(added.Id, "甲改", "tA", null, null, 0);
        var updated = _db.Queryable<SysTenant>().First(t => t.Id == added.Id);
        Assert.Equal("甲改", updated.TenantName);
        Assert.Equal(0, updated.Status);
    }

    [Fact]
    public async Task Tenant_Save_UpdateMissing_ShouldThrow404()
    {
        var svc = NewTenantSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.SaveAsync(31337, "n", "c", null, null, 1));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Tenant_Delete_Missing_ShouldThrow404()
    {
        var svc = NewTenantSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.DeleteAsync(777));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Tenant_Delete_ShouldSoftDelete()
    {
        var svc = NewTenantSvc();
        await svc.SaveAsync(0, "甲", "tA", null, null, 1);
        var id = _db.Queryable<SysTenant>().First(t => t.TenantCode == "tA").Id;
        await svc.DeleteAsync(id);
        Assert.Equal(0, _db.Queryable<SysTenant>().Count(t => t.IsDeleted == false));
    }

    [Fact]
    public async Task Tenant_List_ShouldReturnOnlyActive()
    {
        var svc = NewTenantSvc();
        await svc.SaveAsync(0, "甲", "tA", null, null, 1);
        await svc.SaveAsync(0, "乙", "tB", null, null, 1);
        var id = _db.Queryable<SysTenant>().First(t => t.TenantCode == "tB").Id;
        await svc.DeleteAsync(id);
        var list = await svc.ListAsync();
        Assert.Single(list);
        Assert.Equal("tA", list[0].TenantCode);
    }

    [Fact]
    public async Task Tenant_BatchDelete_FailureIsolatedPerItem()
    {
        var svc = NewTenantSvc();
        await svc.SaveAsync(0, "甲", "tA", null, null, 1);
        await svc.SaveAsync(0, "乙", "tB", null, null, 1);
        var a = _db.Queryable<SysTenant>().First(t => t.TenantCode == "tA").Id;
        var b = _db.Queryable<SysTenant>().First(t => t.TenantCode == "tB").Id;
        // SQLite 触发器确定性注入：更新 tB 时中止
        _db.Ado.ExecuteCommand($"CREATE TRIGGER boom BEFORE UPDATE ON sys_tenant WHEN NEW.Id = {b} BEGIN SELECT RAISE(ABORT, '注入故障'); END");

        var (deleted, failed) = await svc.BatchDeleteAsync(new List<long> { a, b, a, 999999 });

        Assert.Equal(1, deleted); // tA 删除成功
        Assert.Single(failed);    // tB 更新被中止，失败明细随返回
        Assert.Contains(b.ToString(), failed[0]);
        Assert.Equal(0, _db.Queryable<SysTenant>().Count(t => t.Id == a && t.IsDeleted == false));
        Assert.Equal(1, _db.Queryable<SysTenant>().Count(t => t.Id == b && t.IsDeleted == false));
    }

    // ==================== MessageAppService ====================

    [Fact]
    public async Task Message_Page_SuperSeesAll_NonSuperSeesBroadcastAndOwn()
    {
        _db.Insertable(new SysMessage { Id = 1, Title = "广播", ReceiverId = 0 }).ExecuteCommand();
        _db.Insertable(new SysMessage { Id = 2, Title = "给9的", ReceiverId = 9 }).ExecuteCommand();
        _db.Insertable(new SysMessage { Id = 3, Title = "给8的", ReceiverId = 8 }).ExecuteCommand();
        var svc = NewMessageSvc();

        var super = await svc.PageAsync(1, 10, userId: 1, isSuperAdmin: true);
        Assert.Equal(3, super.Total);
        var normal = await svc.PageAsync(1, 10, userId: 9, isSuperAdmin: false);
        Assert.Equal(2, normal.Total);
        Assert.All(normal.Items, m => Assert.True(m.ReceiverId == 0 || m.ReceiverId == 9));
    }

    [Fact]
    public async Task Message_MarkRead_Missing_ShouldThrow404()
    {
        var svc = NewMessageSvc();
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.MarkReadAsync(555));
        Assert.Equal(404, ex.Code);
    }

    [Fact]
    public async Task Message_MarkRead_ShouldSetFlag()
    {
        _db.Insertable(new SysMessage { Id = 1, Title = "t" }).ExecuteCommand();
        var svc = NewMessageSvc();
        await svc.MarkReadAsync(1);
        Assert.True(_db.Queryable<SysMessage>().First(m => m.Id == 1).IsRead);
    }

    [Fact]
    public async Task Message_BatchDeleteAndMarkRead_ShouldReturnAffectedCounts()
    {
        _db.Insertable(new SysMessage { Id = 1, Title = "a" }).ExecuteCommand();
        _db.Insertable(new SysMessage { Id = 2, Title = "b" }).ExecuteCommand();
        _db.Insertable(new SysMessage { Id = 3, Title = "c", IsRead = true }).ExecuteCommand();
        var svc = NewMessageSvc();

        var deleted = await svc.BatchDeleteAsync(new List<long> { 1, 2, 999 });
        Assert.Equal(2, deleted);

        var marked = await svc.BatchMarkReadAsync(new List<long> { 3, 999 });
        Assert.Equal(0, marked); // 已读的不再重复标记
    }

    [Fact]
    public async Task Message_PublishDemo_BlankTopicFallsBack()
    {
        var mq = new RecordingMq();
        var svc = NewMessageSvc(mq);
        await svc.PublishDemoAsync("", "内容");
        Assert.Equal("demo.test", mq.LastTopic);
        Assert.Contains("manual", mq.LastBody); // 序列化默认转义非 ASCII，断言固定英文标记
    }

    [Fact]
    public void Message_GetStats_ShouldExposeProviderAndBacklog()
    {
        var mq = new RecordingMq();
        mq.Backlog["t1"] = 3;
        var svc = NewMessageSvc(mq);
        var stats = svc.GetStats();
        var json = System.Text.Json.JsonSerializer.Serialize(stats);
        Assert.Contains("recording", json);
        Assert.Contains("t1", json);
    }

    private sealed class RecordingMq : IMessageQueue
    {
        public string? LastTopic; public string? LastBody;
        public Dictionary<string, long> Backlog { get; } = new();
        public string ProviderName => "recording";
        public Task PublishAsync(string topic, string body) { LastTopic = topic; LastBody = body; return Task.CompletedTask; }
        public Task SubscribeAsync(string topic, Func<MessageEnvelope, Task> onMessage, CancellationToken ct) => Task.CompletedTask;
        public Dictionary<string, long> GetStats() => new(Backlog);
    }
}
