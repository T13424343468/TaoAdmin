using Microsoft.AspNetCore.Http;
using Quartz.Impl;
using SqlSugar;
using TaoAdmin.Application.Dtos;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.TaskService.Controllers;
using TaoAdmin.TaskService.Entities;
using Xunit;

namespace TaoAdmin.Tests.TaskService;

/// <summary>
/// 任务控制器测试（SQLite 内存库真库）：
/// 执行历史分页接口、任务新增/编辑带重试次数、参数校验。
/// </summary>
public class JobControllerTests
{
    private static int _logId = 7000;
    private readonly SqlSugarClient _db;
    private readonly JobController _controller;

    public JobControllerTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysJob, SysJobLog>();
        _controller = new JobController(_db, new StdSchedulerFactory(), new SnowflakeIdGenerator(1, 1))
        {
            ControllerContext = { HttpContext = new DefaultHttpContext() }
        };
    }

    private async Task<SysJob> SeedJob(string name = "cleanJob", int retryCount = 0)
    {
        var job = new SysJob
        {
            Id = 1000, Name = name, Group = "default", Cron = "0/30 * * * * ?",
            Handler = "heartbeatJob", RetryCount = retryCount
        };
        await _db.Insertable(job).ExecuteCommandAsync();
        return job;
    }

    private static SysJobLog Log(long jobId, DateTime start, bool success, string? message = null, int retryIndex = 0)
        => new()
        {
            Id = Interlocked.Increment(ref _logId), JobId = jobId, JobName = "cleanJob", Group = "default", StartTime = start,
            EndTime = start.AddSeconds(1), DurationMs = 1000, Success = success,
            Message = message, RetryIndex = retryIndex
        };

    [Fact]
    public async Task Logs_ShouldReturnPagedHistory_Descending()
    {
        var job = await SeedJob();
        var now = DateTime.Now;
        for (var i = 0; i < 3; i++)
            await _db.Insertable(Log(job.Id, now.AddMinutes(-i), success: i == 0, message: i == 0 ? null : "错误")).ExecuteCommandAsync();

        var result = await _controller.Logs(job.Id.ToString());

        Assert.True(result.Success);
        Assert.Equal(3, result.Data!.Total);
        Assert.Equal(3, result.Data.Items.Count);
        // 倒序：最新一条在前（i=0 成功）
        Assert.True(result.Data.Items[0].success);
        Assert.False(result.Data.Items[1].success);
        Assert.Equal("错误", result.Data.Items[1].message);
        Assert.Equal(job.Id.ToString(), result.Data.Items[0].jobId);
        Assert.Equal(1000, result.Data.Items[0].durationMs);
        Assert.Equal(0, result.Data.Items[0].retryIndex);
    }

    [Fact]
    public async Task Logs_EmptyHistory_ShouldReturnZeroTotal()
    {
        var job = await SeedJob();

        var result = await _controller.Logs(job.Id.ToString());

        Assert.Equal(0, result.Data!.Total);
        Assert.Empty(result.Data.Items);
    }

    [Fact]
    public async Task Logs_PageSize_ShouldLimitItems()
    {
        var job = await SeedJob();
        var now = DateTime.Now;
        for (var i = 0; i < 5; i++)
            await _db.Insertable(Log(job.Id, now.AddMinutes(-i), true)).ExecuteCommandAsync();

        var result = await _controller.Logs(job.Id.ToString(), pageIndex: 2, pageSize: 2);

        Assert.Equal(5, result.Data!.Total);
        Assert.Equal(2, result.Data.Items.Count);
    }

    [Fact]
    public async Task Page_ShouldIncludeRetryCount()
    {
        await SeedJob(retryCount: 3);

        var result = await _controller.Page();

        var item = Assert.Single(result.Data!.Items);
        Assert.Equal(3, item.retryCount);
    }

    [Fact]
    public async Task Page_WithFireTimes_ShouldFormatNullableDates()
    {
        // 覆盖 DTO 投影中 LastFireTime/NextFireTime 非空向（?. 的 HasValue=true 分支）
        await _db.Insertable(new SysJob
        {
            Id = 2003, Name = "firedJob", Group = "default", Cron = "0/30 * * * * ?", Handler = "heartbeatJob",
            LastFireTime = new DateTime(2026, 1, 15, 8, 0, 0),
            NextFireTime = new DateTime(2026, 1, 15, 8, 0, 30)
        }).ExecuteCommandAsync();

        var result = await _controller.Page();

        var item = Assert.Single(result.Data!.Items);
        Assert.Equal("2026-01-15 08:00:00", item.lastFireTime);
        Assert.Equal("2026-01-15 08:00:30", item.nextFireTime);
    }

    [Fact]
    public async Task Add_WithRetryCount_ShouldPersist()
    {
        var req = new JobSaveRequest
        {
            name = "backupJobRetry", cron = "0 0 2 * * ?", handler = "heartbeatJob", retryCount = 5
        };

        var result = await _controller.Add(req);

        Assert.True(result.Success);
        var job = _db.Queryable<SysJob>().Single();
        Assert.Equal(5, job.RetryCount);
        Assert.Equal("backupJobRetry", job.Name);
    }

    [Fact]
    public async Task Add_NegativeRetryCount_ShouldClampToZero()
    {
        var req = new JobSaveRequest
        {
            name = "backupJobClamp", cron = "0 0 2 * * ?", handler = "heartbeatJob", retryCount = -3
        };

        await _controller.Add(req);

        Assert.Equal(0, _db.Queryable<SysJob>().Single().RetryCount);
    }

    [Fact]
    public async Task Update_WithRetryCount_ShouldPersist()
    {
        var job = await SeedJob();
        var req = new JobSaveRequest
        {
            name = job.Name, cron = job.Cron, handler = job.Handler, retryCount = 2
        };

        var result = await _controller.Update(job.Id.ToString(), req);

        Assert.True(result.Success);
        Assert.Equal(2, _db.Queryable<SysJob>().Single().RetryCount);
    }

    [Fact]
    public async Task Add_BlankName_ShouldThrow()
    {
        var req = new JobSaveRequest { name = " ", cron = "0 0 2 * * ?", handler = "heartbeatJob" };

        var ex = await Assert.ThrowsAsync<DomainException>(() => _controller.Add(req));
        Assert.Equal("任务名、Cron、处理器均不能为空", ex.Message);
    }

    [Fact]
    public async Task Add_InvalidCron_ShouldThrow()
    {
        var req = new JobSaveRequest { name = "x", cron = "not-a-cron", handler = "heartbeatJob" };

        var ex = await Assert.ThrowsAsync<DomainException>(() => _controller.Add(req));
        Assert.Equal("Cron 表达式不合法", ex.Message);
    }

    [Fact]
    public async Task Add_UnknownHandler_ShouldThrow()
    {
        var req = new JobSaveRequest { name = "x", cron = "0 0 2 * * ?", handler = "noSuchJob" };

        var ex = await Assert.ThrowsAsync<DomainException>(() => _controller.Add(req));
        Assert.Contains("未注册的处理器", ex.Message);
    }

    [Fact]
    public async Task Update_BlankCron_ShouldThrow()
    {
        var job = await SeedJob();
        var req = new JobSaveRequest { name = job.Name, cron = " ", handler = job.Handler };

        var ex = await Assert.ThrowsAsync<DomainException>(() => _controller.Update(job.Id.ToString(), req));
        Assert.Equal("任务名、Cron、处理器均不能为空", ex.Message);
    }

    [Fact]
    public async Task Update_InvalidCron_ShouldThrow()
    {
        var job = await SeedJob();
        var req = new JobSaveRequest { name = job.Name, cron = "not-a-cron", handler = job.Handler };

        var ex = await Assert.ThrowsAsync<DomainException>(() => _controller.Update(job.Id.ToString(), req));
        Assert.Equal("Cron 表达式不合法", ex.Message);
    }

    // ---------- Cron 校验接口 ----------

    /// <summary>控制器返回匿名对象跨程序集不可 dynamic 访问，用反射取属性。</summary>
    private static object? Prop(object? obj, string name)
        => obj?.GetType().GetProperty(name)?.GetValue(obj);

    [Fact]
    public void ValidateCron_ValidExpression_ShouldReturnNextFireTime()
    {
        var result = _controller.ValidateCron("0 0 2 * * ?");

        Assert.True(result.Success);
        Assert.True((bool)Prop(result.Data, "valid")!);
        Assert.NotNull((string?)Prop(result.Data, "nextFireTime")); // 下次触发时间已算出
    }

    [Fact]
    public void ValidateCron_InvalidExpression_ShouldReturnNotValid()
    {
        var result = _controller.ValidateCron("not-a-cron");

        Assert.False((bool)Prop(result.Data, "valid")!);
        Assert.Equal("Cron 表达式不合法", (string)Prop(result.Data, "message")!);
    }

    [Fact]
    public void ValidateCron_NullExpression_ShouldReturnExceptionMessage()
    {
        // null 表达式：IsValidExpression 抛 ArgumentException（探针实测），走 catch 返回异常消息
        var result = _controller.ValidateCron(null!);

        Assert.False((bool)Prop(result.Data, "valid")!);
        Assert.Contains("cronExpression", (string)Prop(result.Data, "message")!);
    }

    [Fact]
    public void ValidateCron_PastYearOnly_ShouldReturnNullNextFireTime()
    {
        // 探针实测：仅过去年份的合法 cron，GetNextValidTimeAfter 返回 null → nextFireTime 为空
        var result = _controller.ValidateCron("0 0 0 1 1 ? 2020");

        Assert.True(result.Success);
        Assert.True((bool)Prop(result.Data, "valid")!);
        Assert.Null((string?)Prop(result.Data, "nextFireTime"));
    }

    // ---------- 分组取值两向 ----------

    [Fact]
    public async Task Add_WithExplicitGroup_ShouldPersist()
    {
        var req = new JobSaveRequest
        {
            name = "groupedJob-" + Guid.NewGuid().ToString("N"), cron = "0 0 2 * * ?",
            handler = "heartbeatJob", group = "ops"
        };

        await _controller.Add(req);

        Assert.Equal("ops", _db.Queryable<SysJob>().Single().Group); // 显式分组保留
    }

    [Fact]
    public async Task Update_WithExplicitGroup_ShouldPersist()
    {
        var job = await SeedJob();
        var req = new JobSaveRequest
        {
            name = job.Name, cron = job.Cron, handler = job.Handler, group = "ops"
        };

        await _controller.Update(job.Id.ToString(), req);

        Assert.Equal("ops", _db.Queryable<SysJob>().Single().Group); // 编辑显式分组保留
    }

    // ---------- 生命周期接口（暂停/恢复/立即执行/删除） ----------

    /// <summary>经 Add 真实调度后返回任务名（Quartz 默认调度器全局共享，名字须唯一）。</summary>
    private async Task<string> AddScheduledJob()
    {
        var name = "lifeJob-" + Guid.NewGuid().ToString("N");
        await _controller.Add(new JobSaveRequest
        {
            name = name, cron = "0 0 2 * * ?", handler = "heartbeatJob"
        });
        return name;
    }

    [Fact]
    public async Task Pause_ShouldMarkPaused()
    {
        var name = await AddScheduledJob();
        var id = _db.Queryable<SysJob>().Single(j => j.Name == name).Id.ToString();

        var result = await _controller.Pause(id);

        Assert.True(result.Success);
        Assert.Equal("PAUSED", _db.Queryable<SysJob>().Single(j => j.Name == name).Status);
    }

    [Fact]
    public async Task Resume_ShouldMarkRunning()
    {
        var name = await AddScheduledJob();
        var id = _db.Queryable<SysJob>().Single(j => j.Name == name).Id.ToString();
        await _controller.Pause(id);

        var result = await _controller.Resume(id);

        Assert.True(result.Success);
        Assert.Equal("RUNNING", _db.Queryable<SysJob>().Single(j => j.Name == name).Status);
    }

    [Fact]
    public async Task Run_AfterScheduled_ShouldSucceed()
    {
        // 未调度的任务 TriggerJob 会抛 JobPersistenceException（探针实测），先经 Add 调度
        var name = await AddScheduledJob();
        var id = _db.Queryable<SysJob>().Single(j => j.Name == name).Id.ToString();

        var result = await _controller.Run(id);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Delete_ShouldRemoveJobFromDb()
    {
        var name = await AddScheduledJob();
        var id = _db.Queryable<SysJob>().Single(j => j.Name == name).Id.ToString();

        var result = await _controller.Delete(id);

        Assert.True(result.Success);
        // 软删口径：行仍在库但 IsDeleted 置位，业务查询不再可见
        Assert.Equal(1, _db.Queryable<SysJob>().Count());
        Assert.True(_db.Queryable<SysJob>().First().IsDeleted);
    }

    // ---------- 分页关键字过滤 ----------

    [Fact]
    public async Task Page_Keyword_ShouldFilterByNameOrHandler()
    {
        await _db.Insertable(new SysJob
        {
            Id = 2001, Name = "cleanJob", Group = "default", Cron = "0/30 * * * * ?", Handler = "heartbeatJob"
        }).ExecuteCommandAsync();
        await _db.Insertable(new SysJob
        {
            Id = 2002, Name = "backupJob", Group = "default", Cron = "0 0 2 * * ?", Handler = "heartbeatJob"
        }).ExecuteCommandAsync();

        // 按名称命中
        var byName = await _controller.Page(keyword: "backup");
        Assert.Equal(1, byName.Data!.Total);
        Assert.Equal("backupJob", byName.Data.Items[0].name);

        // 按处理器命中（两个任务同处理器）
        var byHandler = await _controller.Page(keyword: "heartbeat");
        Assert.Equal(2, byHandler.Data!.Total);

        // 无命中
        var noMatch = await _controller.Page(keyword: "nomatch");
        Assert.Equal(0, noMatch.Data!.Total);
        Assert.Empty(noMatch.Data.Items);
    }
}
