using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using TaoAdmin.Common.Messaging;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Handlers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 消息处理器实效测试（SQLite 内存库）：用户创建事件生成欢迎站内消息、演示主题消息原样落库。
/// </summary>
public class DemoMessageHandlersTests
{
    private readonly SqlSugarClient _db;
    private readonly SnowflakeIdGenerator _snow = new(9, 9);

    public DemoMessageHandlersTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysMessage>();
    }

    [Fact]
    public async Task UserCreated_生成欢迎站内消息()
    {
        var handler = new UserCreatedMessageHandler(_db, _snow, NullLogger<UserCreatedMessageHandler>.Instance);
        var body = JsonSerializer.Serialize(new { userId = 42L, userName = "tao", nickname = "老板" });
        var message = new MessageEnvelope { Topic = "user.created", Body = body };

        await handler.HandleAsync(message);

        var msg = _db.Queryable<SysMessage>().Single();
        Assert.Equal(42, msg.ReceiverId);
        Assert.Equal("user.created", msg.Topic);
        Assert.Equal("欢迎加入 TaoAdmin", msg.Title);
        Assert.Contains("tao", msg.Content);
        Assert.Contains("老板", msg.Content);
        Assert.False(msg.IsRead);
    }

    [Fact]
    public async Task UserCreated_昵称缺省用占位符()
    {
        var handler = new UserCreatedMessageHandler(_db, _snow, NullLogger<UserCreatedMessageHandler>.Instance);
        var body = JsonSerializer.Serialize(new { userId = 7L, userName = "lee", nickname = (string?)null });

        await handler.HandleAsync(new MessageEnvelope { Topic = "user.created", Body = body });

        var msg = _db.Queryable<SysMessage>().Single();
        Assert.Contains("lee（-）", msg.Content);
        Assert.Equal(7, msg.ReceiverId);
    }

    [Fact]
    public async Task DemoTopic_消息原样落库为站内信()
    {
        var handler = new DemoTopicMessageHandler(_db, _snow);
        var message = new MessageEnvelope { Topic = "demo.test", Body = "{\"hello\":\"world\"}" };

        await handler.HandleAsync(message);

        var msg = _db.Queryable<SysMessage>().Single();
        Assert.Equal("【演示】收到消息：demo.test", msg.Title);
        Assert.Equal(message.Body, msg.Content);
        Assert.Equal("demo.test", msg.Topic);
        Assert.Equal(0, msg.ReceiverId); // 0 = 全员/系统
        Assert.False(msg.IsRead);
    }
}
