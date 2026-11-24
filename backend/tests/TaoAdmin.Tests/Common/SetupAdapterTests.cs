using Microsoft.Extensions.Configuration;
using SqlSugar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Messaging;
using TaoAdmin.Common.SqlSugar;
using TaoAdmin.Common.Storage;
using Xunit;

namespace TaoAdmin.Tests.Common;

/// <summary>
/// 消息队列装配实效测试：配置开关选择实现、处理器注册、泵启动。
/// </summary>
public class MessageQueueSetupTests
{
    private sealed class EchoHandler : IMessageHandler
    {
        public string Topic => "echo";
        public Task HandleAsync(MessageEnvelope message) => Task.CompletedTask;
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection>? extra = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageHandler, EchoHandler>();
        extra?.Invoke(services);
        services.AddMessageQueue(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddMessageQueue_默认走内存实现()
    {
        using var sp = Build(new Dictionary<string, string?>());
        var mq = sp.GetRequiredService<IMessageQueue>();
        Assert.Equal("memory", mq.ProviderName);
        Assert.IsType<MemoryMessageQueue>(mq);
    }

    [Fact]
    public void AddMessageQueue_开关打开走RabbitMQ实现()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["RabbitMq:Enabled"] = "true",
            ["RabbitMq:HostName"] = "mq.host",
            ["RabbitMq:Port"] = "5673",
            ["RabbitMq:UserName"] = "u",
            ["RabbitMq:Password"] = "p",
            ["RabbitMq:VirtualHost"] = "vh"
        });
        var mq = sp.GetRequiredService<IMessageQueue>();
        Assert.Equal("rabbitmq", mq.ProviderName);
        Assert.IsType<RabbitMqMessageQueue>(mq);
    }

    [Fact]
    public void AddMessageQueue_处理器已注册_泵已挂载()
    {
        using var sp = Build(new Dictionary<string, string?>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is MessageQueuePump);
    }

    [Fact]
    public void AddMessageQueue_开关打开但配置缺省_走默认连接参数()
    {
        // Enabled=true 但各配置项缺省：HostName/UserName/Password/VirtualHost 走 ?? 兜底、Port 走默认 5672
        // RabbitMqMessageQueue 构造不发起真实连接，实例化安全
        using var sp = Build(new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "true" });
        var mq = sp.GetRequiredService<IMessageQueue>();
        Assert.Equal("rabbitmq", mq.ProviderName);
        Assert.IsType<RabbitMqMessageQueue>(mq);
    }

    [Fact]
    public void AddMessageQueue_泵环境变量置零_不挂泵()
    {
        // MQPUMP=0：跳过 AddHostedService，无泵服务
        Environment.SetEnvironmentVariable("MQPUMP", "0");
        try
        {
            using var sp = Build(new Dictionary<string, string?>());
            Assert.DoesNotContain(sp.GetServices<IHostedService>(), s => s is MessageQueuePump);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MQPUMP", null);
        }
    }

    [Fact]
    public async Task 泵启动_处理器被订阅_消息投递到处理器()
    {
        var received = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessageHandler>(new EchoHandlerWithSink(m => received.TrySetResult(m)));
        services.AddMessageQueue(config);
        using var sp = services.BuildServiceProvider();

        var pump = (MessageQueuePump)sp.GetServices<IHostedService>().First(s => s is MessageQueuePump);
        using var cts = new CancellationTokenSource();
        await pump.StartAsync(cts.Token);

        var mq = sp.GetRequiredService<IMessageQueue>();
        await mq.PublishAsync("echo", "hello");
        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("echo", msg.Topic);
        Assert.Equal("hello", msg.Body);
        await pump.StopAsync(CancellationToken.None);
    }

    private sealed class EchoHandlerWithSink : IMessageHandler
    {
        private readonly Action<MessageEnvelope> _sink;
        public EchoHandlerWithSink(Action<MessageEnvelope> sink) => _sink = sink;
        public string Topic => "echo";
        public Task HandleAsync(MessageEnvelope message) { _sink(message); return Task.CompletedTask; }
    }
}

/// <summary>Redis 锁定账本适配：桩 IDatabase 验证最小操作面收敛。</summary>
public class RedisLockoutDatabaseAdapterTests
{
    private static bool SetStore(State state, object?[] args)
    {
        state.Store[args![0]!.ToString()] = args[1]!.ToString();
        return true;
    }

    private static bool SetTtl(State state, object?[] args)
    {
        state.Ttls[args![0]!.ToString()] = (TimeSpan)args[1]!;
        return true;
    }

    private sealed class State
    {
        public Dictionary<string, string> Store = new();
        public Dictionary<string, long> Counters = new();
        public Dictionary<string, TimeSpan> Ttls = new();
    }

    [Fact]
    public void 全方法透传实效()
    {
        var state = new State();
        var db = LooseProxy.Of<IDatabase>((method, args) => method!.Name switch
        {
            nameof(IDatabase.StringGet) => state.Store.TryGetValue(args![0]!.ToString(), out var v) ? (RedisValue)v : RedisValue.Null,
            nameof(IDatabase.StringIncrement) => state.Counters[args![0]!.ToString()] =
                state.Counters.TryGetValue(args[0]!.ToString(), out var c) ? c + 1 : 1,
            nameof(IDatabase.StringSet) => SetStore(state, args!),
            nameof(IDatabase.KeyExpire) => SetTtl(state, args!),
            _ => method.ReturnType == typeof(bool) ? false : (object?)null
        });
        var multiplexer = LooseProxy.Of<IConnectionMultiplexer>((method, _) =>
            method!.Name == nameof(IConnectionMultiplexer.GetDatabase) ? db : null);
        var adapter = new RedisLockoutDatabaseAdapter(multiplexer);

        Assert.Null(adapter.StringGet("missing"));
        Assert.Equal(1, adapter.StringIncrement("cnt"));
        Assert.Equal(2, adapter.StringIncrement("cnt"));
        Assert.True(adapter.StringSetWithExpiry("k", "v", TimeSpan.FromMinutes(1)));
        Assert.Equal("v", adapter.StringGet("k"));
        Assert.True(adapter.KeyExpire("k", TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.FromMinutes(2), state.Ttls["k"]);
    }

    [Fact]
    public void 键删除_存活时间与扫描键_透传实效()
    {
        var keys = new RedisKey[] { "lock:user:1", "lock:user:2" };
        var db = LooseProxy.Of<IDatabase>((method, _) => method!.Name switch
        {
            nameof(IDatabase.KeyDelete) => true,
            nameof(IDatabase.KeyTimeToLive) => TimeSpan.FromMinutes(3),
            "get_Database" => 0, // ScanKeys 取 _db.Database 传给 Keys
            _ => method.ReturnType == typeof(bool) ? false : (object?)null
        });
        var server = LooseProxy.Of<IServer>((method, _) =>
            method!.Name == nameof(IServer.Keys) ? keys : null);
        var multiplexer = LooseProxy.Of<IConnectionMultiplexer>((method, args) => method!.Name switch
        {
            nameof(IConnectionMultiplexer.GetDatabase) => db,
            nameof(IConnectionMultiplexer.GetEndPoints) =>
                new System.Net.EndPoint[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6379) },
            nameof(IConnectionMultiplexer.GetServer) => server,
            _ => null
        });
        var adapter = new RedisLockoutDatabaseAdapter(multiplexer);

        Assert.True(adapter.KeyDelete("lock:user:1"));                       // 删除透传
        Assert.Equal(TimeSpan.FromMinutes(3), adapter.KeyTimeToLive("k"));   // 存活时间透传
        Assert.Equal(new[] { "lock:user:1", "lock:user:2" },                 // 扫描键转字符串
            adapter.ScanKeys("lock:*").ToArray());
    }
}

/// <summary>读写分离扩展：ForceMaster 临时禁用分离并恢复。</summary>
public class ReadWriteExtensionsTests
{
    [Fact]
    public void ForceMaster_回调内禁用_回调后恢复()
    {
        var db = new SqlSugarScope(new SqlSugar.ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        var before = db.Ado.IsDisableMasterSlaveSeparation;
        var result = db.ForceMaster(() => db.Ado.IsDisableMasterSlaveSeparation);
        Assert.True(result); // 回调内已禁用分离（强制主库）
        Assert.Equal(before, db.Ado.IsDisableMasterSlaveSeparation); // 回调后恢复
    }

    [Fact]
    public void ForceMaster_无返回值_回调内禁用_回调后恢复()
    {
        var db = new SqlSugarScope(new SqlSugar.ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        var before = db.Ado.IsDisableMasterSlaveSeparation;
        var seen = false;
        db.ForceMaster(() => { seen = db.Ado.IsDisableMasterSlaveSeparation; });
        Assert.True(seen); // 回调内已禁用分离
        Assert.Equal(before, db.Ado.IsDisableMasterSlaveSeparation); // 回调后恢复
    }

    [Fact]
    public void ForceMaster_无返回值_回调抛异常_仍恢复原值()
    {
        var db = new SqlSugarScope(new SqlSugar.ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        var ex = Record.Exception(() =>
            db.ForceMaster((Action)(() => throw new InvalidOperationException("boom"))));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.False(db.Ado.IsDisableMasterSlaveSeparation); // finally 恢复，不残留禁用态
    }

    [Fact]
    public void ForceMaster_带返回值_回调抛异常_仍恢复原值()
    {
        var db = new SqlSugarScope(new SqlSugar.ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        var ex = Record.Exception(() =>
            db.ForceMaster<object?>(() => throw new InvalidOperationException("boom")));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.False(db.Ado.IsDisableMasterSlaveSeparation);
    }
}

/// <summary>文件存储配置：默认值与属性赋值。</summary>
public class FileStorageOptionsTests
{
    [Fact]
    public void 默认值为本地存储()
    {
        var options = new FileStorageOptions();
        Assert.Equal("local", options.Provider);
        Assert.Equal("uploads", options.Root);
    }

    [Fact]
    public void 属性可赋值()
    {
        var options = new FileStorageOptions { Provider = "minio", Root = "data" };
        Assert.Equal("minio", options.Provider);
        Assert.Equal("data", options.Root);
    }
}
