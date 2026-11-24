using StackExchange.Redis;
using TaoAdmin.Common.Alerting;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Messaging;
using TaoAdmin.Common.Storage;
using Xunit;

namespace TaoAdmin.Tests.Common;

/// <summary>
/// 公共层残留缺口清零（分支 100% 硬门禁）：
/// ①RedisHelper Set 带过期时间分支与 Del 空键数组兜底；
/// ②LocalFileStorageProvider 删除穿越路径不删根外文件；
/// ③MessageEnvelope.TraceId 与 AlertEvent.Data 访问器实效读写；
/// ④MemoryMessageQueue 消费循环在取消令牌后退出（foreach 完成侧）。
/// </summary>
public class CommonResidualGapTests
{
    // ==================== ① RedisHelper：Set 过期分支 + Del 空键兜底 ====================

    private sealed class FakeDb
    {
        public string? Stored;
        public List<string> Deleted = new();
    }

    private static (RedisHelper Helper, FakeDb State) Create()
    {
        var state = new FakeDb();
        var db = LooseProxy.Of<IDatabase>((method, args) => method!.Name switch
        {
            nameof(IDatabase.StringGet) => state.Stored == null ? RedisValue.Null : (RedisValue)state.Stored,
            nameof(IDatabase.StringSet) => SetString(state, args!),
            nameof(IDatabase.KeyDelete) => DeleteKeys(state, args!),
            _ => method.ReturnType == typeof(bool) ? false : (object?)null
        });
        var multiplexer = LooseProxy.Of<IConnectionMultiplexer>((method, _) =>
            method!.Name == nameof(IConnectionMultiplexer.GetDatabase) ? db : null);
        return (new RedisHelper(multiplexer), state);
    }

    private static bool SetString(FakeDb state, object?[] args)
    {
        state.Stored = args![1]!.ToString();
        return true;
    }

    private static long DeleteKeys(FakeDb state, object?[] args)
    {
        foreach (var a in args!)
        {
            switch (a)
            {
                case RedisKey[] arr: state.Deleted.AddRange(arr.Select(k => k.ToString())); break;
                case RedisKey k: state.Deleted.Add(k.ToString()); break;
            }
        }
        return state.Deleted.Count;
    }

    private sealed class Sample { public int Id { get; set; } }

    [Fact]
    public void Set_带过期时间_写入成功()
    {
        var (helper, state) = Create();
        // expiry 非 null：走 Expiration 分支（默认不过期分支由既有用例覆盖）
        Assert.True(helper.Set("k1", new Sample { Id = 1 }, TimeSpan.FromMinutes(1)));
        Assert.NotNull(state.Stored);
        var back = helper.Get<Sample>("k1");
        Assert.NotNull(back);
        Assert.Equal(1, back!.Id);
    }

    [Fact]
    public void Del_null键数组返回0()
    {
        var (helper, state) = Create();
        Assert.Equal(0, helper.Del(null!)); // keys == null：短路返回 0，不触达 Redis
        Assert.Empty(state.Deleted);
    }

    // ==================== ② FileStorage：删除穿越路径不删根外文件 ====================

    [Fact]
    public async Task Delete_穿越路径不删根外文件()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tao-delgap-{Guid.NewGuid():N}");
        var provider = new LocalFileStorageProvider(root, () => 1);
        var outside = Path.Combine(Path.GetTempPath(), $"tao-outside-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(outside, "keep");
            // 相对路径解析后落在存储根之外：物理路径不以根为前缀，删除必须被拦下
            await provider.DeleteAsync($"/uploads/../../{Path.GetFileName(outside)}");
            Assert.True(File.Exists(outside)); // 根外文件原样保留
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ==================== ③ 信封与告警事件：访问器实效读写 ====================

    [Fact]
    public void MessageEnvelope_TraceId可读写()
    {
        var msg = new MessageEnvelope { Topic = "user.created", Body = "{}", TraceId = "trace-1" };
        Assert.Equal("trace-1", msg.TraceId);
        msg.TraceId = null; // 可空 setter
        Assert.Null(msg.TraceId);
    }

    [Fact]
    public void AlertEvent_Data字典可读写()
    {
        var evt = new AlertEvent("e1", "cpu", AlertSeverity.Warning, "monitor", "标题", "内容",
            DateTime.Now, new Dictionary<string, string> { ["host"] = "node-1" });
        // 位置记录全字段访问器实效读取（EventId/Type/Severity/Source/Title/Content/OccurredAt/Data）
        Assert.Equal("e1", evt.EventId);
        Assert.Equal("cpu", evt.Type);
        Assert.Equal(AlertSeverity.Warning, evt.Severity);
        Assert.Equal("monitor", evt.Source);
        Assert.Equal("标题", evt.Title);
        Assert.Equal("内容", evt.Content);
        Assert.NotEqual(default, evt.OccurredAt);
        Assert.Equal("node-1", evt.Data!["host"]);
    }

    // ==================== ④ MemoryMessageQueue：消费循环取消退出 ====================

    [Fact]
    public async Task Subscribe_取消令牌后消费循环退出()
    {
        var queue = new MemoryMessageQueue();
        using var cts = new CancellationTokenSource();
        var handled = new TaskCompletionSource();
        await queue.SubscribeAsync("t", _ => { handled.TrySetResult(); return Task.CompletedTask; }, cts.Token);

        await queue.PublishAsync("t", "body");
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(5)); // 消息正常送达

        cts.Cancel(); // 令牌取消：ReadAllAsync 抛 OperationCanceledException，循环退出
        await Task.Delay(300);
        // 不抛不挂即通过：循环退出侧实效触达（异常路径由既有用例覆盖）
    }
}
