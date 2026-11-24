using System.Reflection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TaoAdmin.Common.Messaging;
using Xunit;

namespace TaoAdmin.Tests.Messaging;

/// <summary>
/// RabbitMqMessageQueue.HandleDeliveryAsync 实效测试：成功回调 Ack、回调异常 Nack 进死信、
/// MessageId 缺省回退、信封内容保真。连接建立等真实 broker 路径不在桩测范围。
/// </summary>
public class RabbitMqDeliveryTests
{
    /// <summary>IChannel 桩：DispatchProxy 拦截 Ack/Nack 调用，其余成员返回默认值。</summary>
    private class FakeChannel : DispatchProxy
    {
        public static ulong? Acked;
        public static (ulong Tag, bool Requeue)? Nacked;
        public static void Reset() { Acked = null; Nacked = null; }

        public static readonly IChannel Channel = DispatchProxy.Create<IChannel, FakeChannel>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IChannel.BasicAckAsync))
            {
                Acked = (ulong)args![0]!;
                return ValueTask.CompletedTask;
            }
            if (targetMethod?.Name == nameof(IChannel.BasicNackAsync))
            {
                Nacked = ((ulong)args![0]!, (bool)args[2]!);
                return ValueTask.CompletedTask;
            }
            var rt = targetMethod!.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
                return Activator.CreateInstance(rt.GetGenericArguments()[0]);
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }

    private static BasicDeliverEventArgs MakeArgs(string? messageId, string body) =>
        new("ct", 7, false, "", "orders",
            new BasicProperties { MessageId = messageId },
            new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task 回调成功_确认Ack_信封内容保真()
    {
        FakeChannel.Reset();
        MessageEnvelope? got = null;
        await RabbitMqMessageQueue.HandleDeliveryAsync(FakeChannel.Channel, MakeArgs("mid-1", "hello"), "orders", m => { got = m; return Task.CompletedTask; });

        Assert.Equal(7u, FakeChannel.Acked);
        Assert.Null(FakeChannel.Nacked);
        Assert.NotNull(got);
        Assert.Equal("mid-1", got!.MessageId);
        Assert.Equal("orders", got.Topic);
        Assert.Equal("hello", got.Body);
    }

    [Fact]
    public async Task 回调抛异常_确认Nack且不重投()
    {
        FakeChannel.Reset();
        await RabbitMqMessageQueue.HandleDeliveryAsync(FakeChannel.Channel, MakeArgs("mid-2", "x"), "t",
            _ => throw new InvalidOperationException("handler boom"));

        Assert.Null(FakeChannel.Acked);
        Assert.NotNull(FakeChannel.Nacked);
        Assert.Equal(7u, FakeChannel.Nacked!.Value.Tag);
        Assert.False(FakeChannel.Nacked.Value.Requeue); // 进死信，避免无限重投
    }

    [Fact]
    public async Task MessageId缺省_回退生成非空Id()
    {
        FakeChannel.Reset();
        MessageEnvelope? got = null;
        await RabbitMqMessageQueue.HandleDeliveryAsync(FakeChannel.Channel, MakeArgs(null, "b"), "t", m => { got = m; return Task.CompletedTask; });

        Assert.Equal(7u, FakeChannel.Acked);
        Assert.NotNull(got);
        Assert.False(string.IsNullOrEmpty(got!.MessageId));
    }
}
