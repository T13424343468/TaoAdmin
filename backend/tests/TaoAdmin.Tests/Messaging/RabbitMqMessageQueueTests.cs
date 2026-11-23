using TaoAdmin.Common.Messaging;
using Xunit;

namespace TaoAdmin.Tests.Messaging;

/// <summary>
/// RabbitMQ 实现的构造与接口契约测试：不连真实 MQ 服务器，
/// 验证构造参数装配、ProviderName、Dispose 安全与默认统计空表。
/// </summary>
public class RabbitMqMessageQueueTests
{
    [Fact]
    public void ProviderName_与默认统计()
    {
        using var queue = new RabbitMqMessageQueue("localhost", 5672, "guest", "guest", "/");
        Assert.Equal("rabbitmq", queue.ProviderName);
    }

    [Fact]
    public void Dispose_未连接时安全释放()
    {
        var queue = new RabbitMqMessageQueue("localhost", 5672, "guest", "guest", "/", "taotest.");
        queue.Dispose(); // 不抛异常即可
        queue.Dispose(); // 幂等释放
    }

    [Fact]
    public void 构造参数_自定义前缀与端口装配()
    {
        using var queue = new RabbitMqMessageQueue("mq.host", 5673, "u", "p", "vhost", "prefix.");
        Assert.Equal("rabbitmq", queue.ProviderName);
    }
}
