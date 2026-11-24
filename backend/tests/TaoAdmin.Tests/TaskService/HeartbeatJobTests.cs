using System.Net;
using System.Reflection;
using Quartz;
using Quartz.Impl;
using Quartz.Simpl;
using Quartz.Spi;
using SqlSugar;
using StackExchange.Redis;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.TaskService.Controllers;
using TaoAdmin.TaskService.Entities;
using TaoAdmin.TaskService.Jobs;
using Xunit;

namespace TaoAdmin.Tests.TaskService;

#region 动态代理桩：只拦截用到的成员，其余返回默认值（误用即空，防假绿靠断言）

/// <summary>接口动态桩：拦截器返回值优先，未拦截成员返回类型默认值。</summary>
internal class LooseProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?>? Intercept { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => Intercept?.Invoke(targetMethod!, args!);

    public static T Of<T>(Func<MethodInfo, object?[], object?>? intercept = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, LooseProxy>()!;
        ((LooseProxy)(object)proxy).Intercept = intercept;
        return proxy;
    }
}

#endregion

/// <summary>心跳作业：向 Redis 写 heartbeat:{jobName}，TTL 10 分钟（实效断言键名与过期）。</summary>
public class HeartbeatJobTests
{
    [Fact]
    public void Execute_ShouldWriteHeartbeatKey_WithTenMinuteTtl()
    {
        string? writtenKey = null, writtenValue = null;
        object? writtenExpiry = null;

        var database = LooseProxy.Of<IDatabase>((method, args) =>
        {
            if (method.Name == nameof(IDatabase.StringSet))
            {
                writtenKey = args![0]!.ToString();
                writtenValue = args[1]!.ToString();
                writtenExpiry = args[2]; // SE.Redis 3.x：第三参为 Expiration 结构，与 TimeSpan 同源隐式转换
                return true;
            }
            return method.ReturnType == typeof(bool) ? false : null;
        });
        var multiplexer = LooseProxy.Of<IConnectionMultiplexer>((method, args) =>
            method.Name == nameof(IConnectionMultiplexer.GetDatabase) ? database : null);

        var job = new HeartbeatJob(new RedisHelper(multiplexer));
        job.Execute(TestJobContext.Create("heartbeatJob")).Wait();

        Assert.Equal("heartbeat:heartbeatJob", writtenKey);
        Assert.True(Equals((Expiration)TimeSpan.FromMinutes(10), writtenExpiry), $"TTL 应为 10 分钟，实际 {writtenExpiry}");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", writtenValue);
    }
}

/// <summary>测试用 Quartz 上下文：最小构造 JobDetail。</summary>
internal static class TestJobContext
{
    public static IJobExecutionContext Create(string name)
    {
        var detail = JobBuilder.Create<HeartbeatJob>().WithIdentity(name, "group").Build();
        var trigger = (IOperableTrigger)TriggerBuilder.Create().WithIdentity($"{name}.trigger", "group").StartNow().Build();
        var fired = new TriggerFiredBundle(detail, trigger, null, false, DateTimeOffset.UtcNow, null, null, null);
        return new JobExecutionContextImpl(null!, fired, new HeartbeatJob(new RedisHelper(LooseProxy.Of<IConnectionMultiplexer>())));
    }
}
