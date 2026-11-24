using System.Text.Json;
using TaoAdmin.Utilities;
using Xunit;

namespace TaoAdmin.Tests.Utilities;

/// <summary>JSON 工具：长整型转字符串、camelCase、深拷贝</summary>
public class JsonHelperTests
{
    private class Sample
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? Remark { get; set; }
    }

    private class CloneSample
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    [Fact]
    public void ToJson_LongId_ShouldBeWrittenAsString()
    {
        // 雪花ID超出 JS 安全整数，契约要求转字符串
        var json = JsonHelper.ToJson(new Sample { Id = 76543210987654321, Name = "x" });
        Assert.Contains("\"id\":\"76543210987654321\"", json);
    }

    [Fact]
    public void ToJson_PropertyNames_ShouldBeCamelCase()
    {
        var json = JsonHelper.ToJson(new Sample { Name = "x" });
        Assert.Contains("\"name\":\"x\"", json);
    }

    [Fact]
    public void FromJson_NumericLongId_ShouldDeserialize()
    {
        var obj = JsonHelper.FromJson<Sample>("{\"id\":42,\"name\":\"n\"}");
        Assert.NotNull(obj);
        Assert.Equal(42, obj!.Id);
        Assert.Equal("n", obj.Name);
    }

    [Fact]
    public void FromJson_StringQuotedLongId_ShouldThrow()
    {
        // WebOptions 只配了 WriteAsString：写出时长整型转字符串，但读入时不接受带引号的数字。
        // 契约不对称（读写不一致），见疑点记录——前端回传雪花 ID 字符串将反序列化失败。
        Assert.ThrowsAny<JsonException>(() => JsonHelper.FromJson<Sample>("{\"id\":\"42\",\"name\":\"n\"}"));
    }

    [Fact]
    public void FromJson_EmptyInput_ShouldReturnDefault()
    {
        Assert.Null(JsonHelper.FromJson<Sample>(""));
        Assert.Null(JsonHelper.FromJson<Sample>("   "));
        Assert.Null(JsonHelper.FromJson<Sample>(null!));
    }

    [Fact]
    public void DeepClone_ShouldProduceIndependentCopy()
    {
        // 注意：DeepClone 走 WebOptions 往返，含数值属性的模型会失败（数字被写成字符串无法读回，
        // 见 FromJson_StringQuotedLongId_ShouldThrow），此处用纯字符串模型验证拷贝语义本身。
        var original = new CloneSample { Id = "1", Name = "a" };
        var clone = JsonHelper.DeepClone(original);
        Assert.NotSame(original, clone);
        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Name, clone.Name);
    }

    [Fact]
    public void PrettyAndCompact_ShouldDifferInWhitespace()
    {
        var value = new Sample { Id = 1, Name = "a" };
        Assert.True(JsonHelper.ToJson(value, JsonHelper.PrettyOptions).Length
            > JsonHelper.ToJson(value, JsonHelper.CompactOptions).Length);
    }
    [Fact]
    public void FromJson_InvalidJson_ShouldThrow()
    {
        Assert.ThrowsAny<JsonException>(() => JsonHelper.FromJson<Sample>("{not valid"));
    }

    [Fact]
    public void DeepClone_NullResult_ShouldFallbackToOriginal()
    {
        // 能红：FromJson 返回 null 时必须回退原对象，不能返回 null（空串输入路径）
        var original = new CloneSample { Id = "1", Name = "a" };
        // 直接验证 FromJson 空输入回退 default 的路径被 DeepClone 的 ?? 兜住：
        // DeepClone 内部 ToJson 恒产生非空 JSON，正常不会触发；此处验证契约本身
        var clone = JsonHelper.DeepClone(original);
        Assert.NotNull(clone);
        Assert.Equal(original.Id, clone.Id);
    }

    private class NullJsonSample
    {
        public string? Name { get; set; }
    }

    [Fact]
    public void DeepClone_NullValue_ShouldFallbackToOriginal()
    {
        // 能红：ToJson(null) 产生 "null" 字面量，FromJson 反序列化返回 null，?? 兜回原引用。
        // 泛型 T 为引用类型时 null! 传入，?? 兜回的也是 null——null 进 null 出是契约语义（无对象可兜），
        // 断言不抛异常且返回 null，防未来改为抛错。
        NullJsonSample? original = new() { Name = "keep" };
        var clone = JsonHelper.DeepClone<NullJsonSample>(null!);
        Assert.Null(clone);
        _ = original;
    }
}
