using TaoAdmin.Common.Snowflake;
using Xunit;

namespace TaoAdmin.Tests;

/// <summary>雪花ID：唯一性与趋势递增</summary>
public class SnowflakeTests
{
    [Fact]
    public void NextId_ShouldBeUnique()
    {
        var gen = new SnowflakeIdGenerator(1, 1);
        var ids = new HashSet<long>();
        for (var i = 0; i < 100_000; i++)
            Assert.True(ids.Add(gen.NextId()));
    }

    [Fact]
    public void NextId_ShouldBeMonotonic()
    {
        var gen = new SnowflakeIdGenerator(1, 1);
        long last = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var id = gen.NextId();
            Assert.True(id > last);
            last = id;
        }
    }

    [Fact]
    public void NextId_MultiWorker_ShouldNotCollide()
    {
        var a = new SnowflakeIdGenerator(1, 1);
        var b = new SnowflakeIdGenerator(1, 2);
        var ids = new HashSet<long>();
        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(ids.Add(a.NextId()));
            Assert.True(ids.Add(b.NextId()));
        }
    }

    [Fact]
    public void NextId_ClockShiftBackwards_ShouldThrow()
    {
        // 能红：时钟回拨必须拒绝（若漏判会生成重复ID）
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var gen = new SnowflakeIdGenerator(1, 1);
        gen.TimeSource = () => now;
        gen.NextId(); // 记录 _lastTimestamp = now
        gen.TimeSource = () => now - 5; // 模拟回拨 5 毫秒
        var ex = Assert.Throws<InvalidOperationException>(() => gen.NextId());
        Assert.Contains("时钟回拨", ex.Message);
    }

    [Fact]
    public void NextId_ClockShiftForwards_ShouldRecover()
    {
        // 能红：回拨后时钟恢复到 >= lastTimestamp 必须能继续出号（若卡死则超时）
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var gen = new SnowflakeIdGenerator(1, 1);
        gen.TimeSource = () => now;
        gen.NextId();
        gen.TimeSource = () => now - 5;
        Assert.Throws<InvalidOperationException>(() => gen.NextId());
        gen.TimeSource = () => now + 10; // 时钟恢复
        var id = gen.NextId();
        Assert.True(id > 0);
    }
}
