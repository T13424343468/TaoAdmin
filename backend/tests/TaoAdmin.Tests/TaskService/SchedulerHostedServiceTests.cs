using System.Collections.Specialized;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;
using SqlSugar;
using StackExchange.Redis;
using TaoAdmin.Common.Cache;
using TaoAdmin.TaskService.Entities;
using TaoAdmin.TaskService.Jobs;
using Xunit;

namespace TaoAdmin.Tests.TaskService;

/// <summary>
/// 调度宿主服务测试（真 Quartz 内存调度器，独立实例名防串扰）：
/// RUNNING 任务恢复注册、非 RUNNING 跳过、未知处理器跳过不抛、StopAsync 关停。
/// </summary>
public class SchedulerHostedServiceTests
{
    private sealed class StubJobFactory : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler)
            => new HeartbeatJob(new RedisHelper(LooseProxy.Of<IConnectionMultiplexer>()));

        public void ReturnJob(IJob job) => (job as IDisposable)?.Dispose();
    }

    private readonly SqlSugarClient _db;
    private readonly StdSchedulerFactory _factory;
    private readonly SchedulerHostedService _service;

    public SchedulerHostedServiceTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysJob>();
        // 每个用例独立实例名，避免默认调度器名在并行用例间共享
        _factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"sched-{Guid.NewGuid():N}"
        });
        _service = new SchedulerHostedService(_factory, _db, new StubJobFactory());
    }

    private async Task SeedJob(string name, string handler, string status = "RUNNING", string group = "default")
    {
        await _db.Insertable(new SysJob
        {
            Id = name.GetHashCode(), Name = name, Group = group,
            Cron = "0 0 0 1 1 ? 2099", Handler = handler, Status = status
        }).ExecuteCommandAsync();
    }

    [Fact]
    public async Task StartAsync_RunningJob_ShouldStartScheduler_AndRegisterJobWithCron()
    {
        await SeedJob("heartbeatJob", "heartbeatJob");

        await _service.StartAsync(CancellationToken.None);
        var scheduler = await _factory.GetScheduler();

        Assert.True(scheduler.IsStarted);
        var detail = await scheduler.GetJobDetail(new JobKey("heartbeatJob", "default"));
        Assert.NotNull(detail);
        var trigger = (await scheduler.GetTriggersOfJob(new JobKey("heartbeatJob", "default"))).Single();
        var cron = Assert.IsType<Quartz.Impl.Triggers.CronTriggerImpl>(trigger);
        Assert.Equal("0 0 0 1 1 ? 2099", cron.CronExpressionString);

        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_PausedJob_ShouldNotRegister()
    {
        await SeedJob("pausedJob", "heartbeatJob", status: "PAUSED");

        await _service.StartAsync(CancellationToken.None);
        var scheduler = await _factory.GetScheduler();

        Assert.Null(await scheduler.GetJobDetail(new JobKey("pausedJob", "default")));

        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_UnknownHandler_ShouldSkipWithoutThrowing_AndRegisterKnownOnes()
    {
        await SeedJob("brokenJob", "no-such-handler");
        await SeedJob("goodJob", "heartbeatJob");

        await _service.StartAsync(CancellationToken.None); // 不抛即通过
        var scheduler = await _factory.GetScheduler();

        Assert.Null(await scheduler.GetJobDetail(new JobKey("brokenJob", "default")));
        Assert.NotNull(await scheduler.GetJobDetail(new JobKey("goodJob", "default")));

        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ShouldShutdownScheduler()
    {
        await _service.StartAsync(CancellationToken.None);
        var scheduler = await _factory.GetScheduler();

        await _service.StopAsync(CancellationToken.None);

        Assert.True(scheduler.IsShutdown);
    }
}
