using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 登录安全设置缺口补测（存量补账班，分支 100% 硬门禁）：
/// 保存校验四组边界（每组上下限）、读取默认值/解析失败回退/钳制下限、
/// 开关三态（缺省/0/1）、缓存命中不落库。
/// </summary>
public class LoginSecurityGapTests
{
    private static (LoginSecurityAppService Svc, SqlSugarClient Db) Create()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysSiteConfig>();
        var svc = new LoginSecurityAppService(
            db, new SnowflakeIdGenerator(1, 1), new MemoryCache(new MemoryCacheOptions()));
        return (svc, db);
    }

    private static long _seedId = 1000;

    private static async Task SeedAsync(SqlSugarClient db, string key, string value)
    {
        await db.Insertable(new SysSiteConfig
        {
            Id = System.Threading.Interlocked.Increment(ref _seedId),
            ConfigKey = key, ConfigValue = value
        }).ExecuteCommandAsync();
    }

    [Theory]
    [InlineData(0, 15, true, 0, 90, false, "连续失败次数")]
    [InlineData(100, 15, true, 0, 90, false, "连续失败次数")]
    [InlineData(5, 0, true, 0, 90, false, "锁定时长")]
    [InlineData(5, 1441, true, 0, 90, false, "锁定时长")]
    [InlineData(5, 15, true, -1, 90, false, "验证码触发阈值")]
    [InlineData(5, 15, true, 100, 90, false, "验证码触发阈值")]
    [InlineData(5, 15, true, 0, -1, false, "日志保留天数")]
    [InlineData(5, 15, true, 0, 3651, false, "日志保留天数")]
    public async Task SaveAsync_OutOfRange_ThrowsWithMessage(
        int maxFails, int lockMinutes, bool captcha, int captchaAfter, int retention, bool kick, string expectPart)
    {
        var (svc, _) = Create();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            svc.SaveAsync(new LoginSecuritySettings(maxFails, lockMinutes, captcha, captchaAfter, retention, kick)));
        Assert.Contains(expectPart, ex.Message);
    }

    [Fact]
    public async Task SaveAsync_AllBoundariesValid_Persists()
    {
        var (svc, db) = Create();
        await svc.SaveAsync(new LoginSecuritySettings(1, 1, false, 0, 0, true));
        var settings = await svc.GetAsync();
        Assert.Equal(1, settings.LoginMaxFails);
        Assert.Equal(1, settings.LoginLockMinutes);
        Assert.False(settings.LoginCaptchaRequired);
        Assert.Equal(0, settings.CaptchaAfterFails);
        Assert.Equal(0, settings.LogRetentionDays);
        Assert.True(settings.KickOnDuplicateLogin);
    }

    [Fact]
    public async Task GetAsync_NonNumericValues_FallBackToDefaults()
    {
        var (svc, db) = Create();
        await SeedAsync(db, LoginSecurityKeys.MaxFails, "not-a-number");
        await SeedAsync(db, LoginSecurityKeys.LockMinutes, "");
        var settings = await svc.GetAsync();
        Assert.Equal(LoginSecurityAppService.DefaultMaxFails, settings.LoginMaxFails);
        Assert.Equal(LoginSecurityAppService.DefaultLockMinutes, settings.LoginLockMinutes);
    }

    [Fact]
    public async Task GetAsync_ZeroStored_ClampedToMinimum()
    {
        var (svc, db) = Create();
        await SeedAsync(db, LoginSecurityKeys.MaxFails, "0");
        await SeedAsync(db, LoginSecurityKeys.LockMinutes, "0");
        await SeedAsync(db, LoginSecurityKeys.LogRetentionDays, "0");
        var settings = await svc.GetAsync();
        Assert.Equal(1, settings.LoginMaxFails);
        Assert.Equal(1, settings.LoginLockMinutes);
        Assert.Equal(0, settings.LogRetentionDays); // 0=永久，允许为 0
    }

    [Theory]
    [InlineData(null, false)]   // 缺省：默认关
    [InlineData("0", false)]    // 显式关
    [InlineData("1", true)]     // 显式开
    public async Task GetAsync_KickSwitch_ThreeStates(string? stored, bool expect)
    {
        var (svc, db) = Create();
        if (stored != null) await SeedAsync(db, LoginSecurityKeys.KickOnDuplicateLogin, stored);
        Assert.Equal(expect, (await svc.GetAsync()).KickOnDuplicateLogin);
    }

    [Theory]
    [InlineData(null, true)]    // 缺省：默认每次都要验证码
    [InlineData("0", false)]
    [InlineData("1", true)]
    public async Task GetAsync_CaptchaRequired_ThreeStates(string? stored, bool expect)
    {
        var (svc, db) = Create();
        if (stored != null) await SeedAsync(db, LoginSecurityKeys.CaptchaRequired, stored);
        Assert.Equal(expect, (await svc.GetAsync()).LoginCaptchaRequired);
    }

    [Fact]
    public async Task GetAsync_CacheHit_SecondReadSkipsDb()
    {
        var (svc, db) = Create();
        var first = await svc.GetAsync();
        // 首读后改库：缓存命中时第二次读不受库变化影响
        await SeedAsync(db, LoginSecurityKeys.MaxFails, "9");
        var second = await svc.GetAsync();
        Assert.Equal(first.LoginMaxFails, second.LoginMaxFails);
        Assert.NotEqual(9, second.LoginMaxFails);
    }

    [Fact]
    public async Task SaveAsync_ClearsCache_NextReadHitsDb()
    {
        var (svc, db) = Create();
        await svc.GetAsync(); // 预热缓存
        await SeedAsync(db, LoginSecurityKeys.MaxFails, "7");
        await svc.SaveAsync(new LoginSecuritySettings(3, 15, true, 0, 90, false));
        // 保存后缓存被清：读到的 MaxFails 来自本次保存值 3，而非库中残留 7
        Assert.Equal(3, (await svc.GetAsync()).LoginMaxFails);
    }
}
