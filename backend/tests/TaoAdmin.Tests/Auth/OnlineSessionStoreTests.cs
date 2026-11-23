using TaoAdmin.Common.Auth;
using Xunit;

namespace TaoAdmin.Tests.Auth;

/// <summary>在线会话仓库：登记、名单、强制下线、全端下线与过期清理。</summary>
public class OnlineSessionStoreTests
{
    private static OnlineSession NewSession(string id, long userId = 1, string name = "tao")
        => new() { SessionId = id, UserId = userId, UserName = name, NickName = name, Ip = "127.0.0.1", UserAgent = "x" };

    [Fact]
    public void Register_ShouldAppearInList()
    {
        var store = new OnlineSessionStore();
        store.Register(NewSession("s1"));
        store.Register(NewSession("s2", 2, "lee"));

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.All(list, s => Assert.False(store.IsRevoked(s.SessionId)));
    }

    [Fact]
    public void Register_DuplicateSessionId_ShouldBeIdempotent()
    {
        var store = new OnlineSessionStore();
        store.Register(NewSession("s1"));
        store.Register(NewSession("s1"));

        Assert.Single(store.List());
    }

    [Fact]
    public void Revoke_ShouldRemoveFromList_AndMarkRevoked()
    {
        var store = new OnlineSessionStore();
        store.Register(NewSession("s1"));

        Assert.True(store.Revoke("s1"));
        Assert.Empty(store.List());
        Assert.True(store.IsRevoked("s1"));

        // 重复下线：会话已不在名单，返回 false 不重复记吊销
        Assert.False(store.Revoke("s1"));
    }

    [Fact]
    public void RevokeByUser_ShouldKickOnlyTargetUser()
    {
        var store = new OnlineSessionStore();
        store.Register(NewSession("a1", 1));
        store.Register(NewSession("a2", 1));
        store.Register(NewSession("b1", 2));

        var kicked = store.RevokeByUser(1);
        Assert.Equal(2, kicked);
        Assert.Single(store.List());
        Assert.True(store.IsRevoked("a1"));
        Assert.True(store.IsRevoked("a2"));
        Assert.False(store.IsRevoked("b1"));
    }

    [Fact]
    public void ExpiredSessions_ShouldBePrunedByList_AndStayRevokedWithinLifetime()
    {
        // 有效期 1 分钟：注册后把登录时间改到 2 分钟前，List 即应清出
        var store = new OnlineSessionStore(tokenLifetimeMinutes: 1);
        store.Register(NewSession("s1"));
        store.Register(NewSession("s2"));

        var s1 = store.Find("s1")!;
        typeof(OnlineSession).GetProperty(nameof(OnlineSession.LoginTime))!
            .SetValue(s1, DateTime.Now.AddMinutes(-2));

        var list = store.List(); // s1 过期被清，s2 仍在
        Assert.Single(list);
        Assert.Equal("s2", list[0].SessionId);

        // 过期会话的吊销记录在一个令牌周期内保留（防「下线后复活」）
        store.Register(NewSession("s3"));
        store.Revoke("s3");
        Assert.True(store.IsRevoked("s3"));
    }

    [Fact]
    public void Touch_ShouldUpdateLastActiveTime()
    {
        var store = new OnlineSessionStore();
        store.Register(NewSession("s1"));
        var before = store.Find("s1")!.LastActiveTime;
        System.Threading.Thread.Sleep(20);

        store.Touch("s1");
        Assert.True(store.Find("s1")!.LastActiveTime > before);
        // 不存在的会话 Touch 不抛错
        store.Touch("missing");
    }

    [Fact]
    public void ExpiredRevocation_ShouldBePrunedAfterTokenLifetime()
    {
        // 能红：吊销记录超过一个令牌周期必须被清出（若漏判会内存泄漏且语义含糊）
        var store = new OnlineSessionStore(tokenLifetimeMinutes: 1);
        store.Register(NewSession("r1"));
        store.Revoke("r1");
        Assert.True(store.IsRevoked("r1"));

        // 吊销记录时间无法直接改（内部字典），走真实时钟：等 61 秒太久，改用反射验证内部结构
        var revokedField = typeof(OnlineSessionStore).GetField("_revoked",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(revokedField);
        var revoked = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)revokedField!.GetValue(store)!;
        revoked["r1"] = DateTime.Now.AddMinutes(-2); // 吊销时间改到 2 分钟前

        store.List(); // 触发清理
        Assert.False(store.IsRevoked("r1")); // 过期吊销记录已被清出
    }
}
