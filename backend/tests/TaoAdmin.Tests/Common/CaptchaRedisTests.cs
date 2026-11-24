using System.Reflection;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Security;
using Xunit;

namespace TaoAdmin.Tests.Common;

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

/// <summary>验证码：生成/校验/一次性消费/过期（真实 MemoryCache）。</summary>
public class CaptchaServiceTests
{
    [Fact]
    public void Generate_返回四字符SVG并入库()
    {
        var svc = new CaptchaService(new MemoryCache(new MemoryCacheOptions()));
        var (id, svg) = svc.Generate();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Contains("<svg", svg);
        Assert.NotNull(svc.Peek(id)); // 答案已入缓存
    }

    [Fact]
    public void Verify_正确输入一次性消费()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CaptchaService(cache);
        var (id, _) = svc.Generate();
        var answer = svc.Peek(id)!;

        Assert.True(svc.Verify(id, answer.ToLower())); // 大小写不敏感
        Assert.False(svc.Verify(id, answer));          // 已消费，第二次失败
        Assert.Null(svc.Peek(id));                      // 答案已删
    }

    [Fact]
    public void Verify_错误输入答案同删()
    {
        var svc = new CaptchaService(new MemoryCache(new MemoryCacheOptions()));
        var (id, _) = svc.Generate();
        Assert.False(svc.Verify(id, "XXXX"));
        Assert.Null(svc.Peek(id)); // 错误也消费
    }

    [Fact]
    public void Verify_空参数或未知id返回false()
    {
        var svc = new CaptchaService(new MemoryCache(new MemoryCacheOptions()));
        Assert.False(svc.Verify(null, "ABCD"));
        Assert.False(svc.Verify("", "ABCD"));
        Assert.False(svc.Verify("  ", "ABCD"));
        var (id, _) = svc.Generate();
        Assert.False(svc.Verify(id, null));
        Assert.False(svc.Verify(id, "  "));
        Assert.False(svc.Verify("unknown-id", "ABCD"));
    }
}

/// <summary>RedisHelper：桩 IDatabase 走真实 JSON 序列化与键操作逻辑。</summary>
public class RedisHelperTests
{
    private static bool SetString(FakeDb state, object?[] args)
    {
        state.Stored = args![1]!.ToString();
        return true;
    }

    private static bool SetTtl(FakeDb state, object?[] args)
    {
        state.Ttl = (TimeSpan)args![1]!;
        return true;
    }

    private static bool SetHash(FakeDb state, object?[] args)
    {
        state.Hash[args![1]!.ToString()] = args[2]!.ToString();
        return true;
    }

    private static long AddKeys(FakeDb state, object?[] args)
    {
        foreach (var a in args!)
        {
            switch (a)
            {
                case RedisKey[] arr: state.Keys.AddRange(arr.Select(k => k.ToString())); break;
                case RedisKey k: state.Keys.Add(k.ToString()); break;
            }
        }
        return state.Keys.Count;
    }

    private sealed class FakeDb
    {
        public string? Stored;
        public Dictionary<string, string> Hash = new();
        public List<string> Keys = new();
        public bool Exists;
        public TimeSpan? Ttl;
    }

    private static (RedisHelper Helper, FakeDb State) Create()
    {
        var state = new FakeDb();
        var db = LooseProxy.Of<IDatabase>((method, args) => method!.Name switch
        {
            nameof(IDatabase.StringGet) => state.Stored == null
                ? RedisValue.Null : (RedisValue)state.Stored,
            nameof(IDatabase.StringSet) => SetString(state, args!),
            nameof(IDatabase.KeyDelete) => AddKeys(state, args!),
            nameof(IDatabase.KeyExists) => state.Exists,
            nameof(IDatabase.KeyExpire) => SetTtl(state, args!),
            nameof(IDatabase.HashGet) => state.Hash.TryGetValue(args![1]!.ToString(), out var v) ? (RedisValue)v : RedisValue.Null,
            nameof(IDatabase.HashSet) => SetHash(state, args!),
            nameof(IDatabase.HashDelete) => state.Hash.Remove(args![1]!.ToString()),
            nameof(IDatabase.HashGetAll) => state.Hash.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray(),
            _ => method.ReturnType == typeof(bool) ? false : null
        });
        var multiplexer = LooseProxy.Of<IConnectionMultiplexer>((method, _) =>
            method!.Name == nameof(IConnectionMultiplexer.GetDatabase) ? db : null);
        return (new RedisHelper(multiplexer), state);
    }

    private sealed class Sample { public int Id { get; set; } public string Name { get; set; } = ""; }

    [Fact]
    public void GetSet_对象JSON序列化往返()
    {
        var (helper, state) = Create();
        Assert.True(helper.Set("k1", new Sample { Id = 1, Name = "中文" }));
        Assert.Contains("中文", state.Stored); // 非 ASCII 不转义
        var back = helper.Get<Sample>("k1");
        Assert.NotNull(back);
        Assert.Equal(1, back!.Id);
        Assert.Equal("中文", back.Name);
    }

    [Fact]
    public void Get_不存在返回default()
    {
        var (helper, _) = Create();
        Assert.Null(helper.Get<Sample>("missing"));
        Assert.Null(helper.GetString("missing"));
    }

    [Fact]
    public void SetString_GetString_原始字符串()
    {
        var (helper, state) = Create();
        Assert.True(helper.SetString("k", "raw"));
        Assert.Equal("raw", state.Stored);
        Assert.Equal("raw", helper.GetString("k"));
    }

    [Fact]
    public void Del_空数组返回0_多键删除()
    {
        var (helper, state) = Create();
        Assert.Equal(0, helper.Del(Array.Empty<string>()));
        Assert.Equal(2, helper.Del("a", "b"));
        Assert.Equal(new[] { "a", "b" }, state.Keys);
    }

    [Fact]
    public void Exists_Expire_透传()
    {
        var (helper, state) = Create();
        state.Exists = true;
        Assert.True(helper.Exists("k"));
        Assert.True(helper.Expire("k", TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), state.Ttl);
    }

    [Fact]
    public void Hash_读写删全遍历()
    {
        var (helper, _) = Create();
        Assert.True(helper.HSet("h", "f1", new Sample { Id = 2, Name = "哈希" }));
        var got = helper.HGet<Sample>("h", "f1");
        Assert.NotNull(got);
        Assert.Equal(2, got!.Id);

        Assert.True(helper.HDel("h", "f1"));
        Assert.Null(helper.HGet<Sample>("h", "f1"));

        helper.HSet("h", "a", new Sample { Id = 1, Name = "x" });
        helper.HSet("h", "b", new Sample { Id = 2, Name = "y" });
        var all = helper.HGetAll("h");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void HGet_不存在返回default()
    {
        var (helper, _) = Create();
        Assert.Null(helper.HGet<Sample>("h", "missing"));
    }
}
