using SqlSugar;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.Persistence;

/// <summary>
/// TrySyncApis 带兜底启动同步：失败走 onFailed 回调不抛出、返回 false；
/// 成功路径正常建表同步返回 true（启动链不因接口扫描失败而阻塞）。
/// </summary>
public class ApiEndpointScannerTests
{
    [Fact]
    public void TrySyncApis_数据库不可用时_回调失败且返回false()
    {
        // 连接串指向不存在的目录：InitTables 即失败，模拟 IdentityService 初始化中的库不可用
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"DataSource=/nonexistent-dir-{Guid.NewGuid():N}/x.db",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        Exception? captured = null;
        var ok = ApiEndpointScanner.TrySyncApis(db, new SnowflakeIdGenerator(1, 1),
            ex => captured = ex, typeof(ApiEndpointScannerTests).Assembly);

        Assert.False(ok);
        Assert.NotNull(captured);
    }

    [Fact]
    public void TrySyncApis_空控制器程序集_成功返回true()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        Exception? failed = null;
        var ok = ApiEndpointScanner.TrySyncApis(db, new SnowflakeIdGenerator(1, 1),
            ex => failed = ex, typeof(ApiEndpointScannerTests).Assembly);

        Assert.True(ok);
        Assert.Null(failed);
        // 无控制器动作可扫：不产生接口记录
        Assert.Equal(0, db.Queryable<TaoAdmin.Domain.Entities.SysApi>().Count());
    }
}
