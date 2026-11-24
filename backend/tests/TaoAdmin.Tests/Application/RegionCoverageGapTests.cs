using System.Text;
using Microsoft.Extensions.Logging;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 区划批次补缺（分支 100% 硬门禁）：
/// ①SearchAsync 空关键词与 null 关键词直接返回空列表；
/// ②ReplaceAll 在版本元数据已存在时走更新而非重复插入（同步/异步两侧）；
/// ③更新任务完成日志抛异常时任务报失败且阶段由「完成」回映射为「写入」；
/// ④数据包资源缺失防御抛出；⑤数据包解析结果为空防御抛出；⑥children 为 null 与非 null 两侧解析。
/// </summary>
public class RegionCoverageGapTests
{
    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysRegion>();
        db.CodeFirst.InitTables<SysRegionMeta>();
        return db;
    }

    private static RegionSnapshot TinySnapshot(string version) => new()
    {
        Version = version,
        Source = "测试数据源",
        Nodes =
        [
            new RegionNodeDto
            {
                Code = "110000", Name = "北京市", Level = 1,
                Children = [new RegionNodeDto { Code = "110101", Name = "东城区", Level = 3 }]
            }
        ]
    };

    // ==================== RegionAppService：空关键词分支 ====================

    [Fact]
    public async Task SearchAsync_NullOrBlankKeyword_ReturnsEmpty()
    {
        var db = CreateDb();
        var svc = new RegionAppService(db);

        Assert.Empty(await svc.SearchAsync(null));
        Assert.Empty(await svc.SearchAsync("   "));
    }

    [Fact]
    public async Task GetChildrenAsync_NullParentCode_TreatedAsRoot()
    {
        var db = CreateDb();
        var snow = new SnowflakeIdGenerator(1, 1);
        db.Insertable(new List<SysRegion>
        {
            new() { Id = snow.NextId(), Code = "110000", Name = "北京市", ParentCode = "", Level = 1, FullPath = "北京市", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now },
            new() { Id = snow.NextId(), Code = "320000", Name = "江苏省", ParentCode = "", Level = 1, FullPath = "江苏省", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now }
        }).ExecuteCommand();

        // null 与空串同义：都查省级
        var byNull = await new RegionAppService(db).GetChildrenAsync(null);
        Assert.Equal(2, byNull.Count);
        Assert.All(byNull, p => Assert.Equal(1, p.level));
    }

    // ==================== RegionWriteService：元数据已存在走更新 ====================

    [Fact]
    public async Task ReplaceAll_MetaExists_UpdatesSingleRow_NotDuplicate()
    {
        var db = CreateDb();
        var snow = new SnowflakeIdGenerator(1, 1);
        var writer = new RegionWriteService(db);

        // 首次写入：插入元数据行
        writer.ReplaceAll(TinySnapshot("版本一"), snow);
        var metas = db.Queryable<SysRegionMeta>().Count();
        Assert.Equal(1, metas);
        var first = await db.Queryable<SysRegionMeta>().FirstAsync();

        // 第二次同步写入：元数据已存在，走更新，仍单行且版本刷新
        writer.ReplaceAll(TinySnapshot("版本二"), snow);
        Assert.Equal(1, db.Queryable<SysRegionMeta>().Count());
        var afterSync = await db.Queryable<SysRegionMeta>().FirstAsync();
        Assert.Equal(first.Id, afterSync.Id);
        Assert.Equal("版本二", afterSync.Version);

        // 异步写入：同样走更新路径
        await writer.ReplaceAllAsync(TinySnapshot("版本三"), snow);
        Assert.Equal(1, db.Queryable<SysRegionMeta>().Count());
        var afterAsync = await db.Queryable<SysRegionMeta>().FirstAsync();
        Assert.Equal(first.Id, afterAsync.Id);
        Assert.Equal("版本三", afterAsync.Version);
        Assert.Equal("版本三", (await new RegionAppService(db).GetMetaAsync())!.Version);
    }

    // ==================== RegionUpdateService：完成日志抛异常的阶段回映射 ====================

    /// <summary>仅在写「区划更新完成」这条 Information 日志时抛异常的假日志器（模拟日志管道故障）</summary>
    private sealed class CompletionLogThrowingLogger : ILogger<RegionUpdateService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information && formatter(state, exception).Contains("区划更新完成"))
                throw new InvalidOperationException("日志管道故障");
        }
    }

    /// <summary>满足校验器的 25 省小快照（每省 1 市 1 区 401 街道，共 10100 条）</summary>
    private static RegionSnapshot ValidSnapshot()
    {
        var nodes = new List<RegionNodeDto>();
        for (var p = 1; p <= 25; p++)
        {
            var p2 = $"{p:00}";
            var streets = Enumerable.Range(1, 401)
                .Select(t => new RegionNodeDto { Code = $"{p2}0101{t:000}", Name = $"测试街道{p}-{t}", Level = 4 })
                .ToList();
            nodes.Add(new RegionNodeDto
            {
                Code = $"{p2}0000", Name = $"测试省{p}", Level = 1,
                Children =
                [
                    new RegionNodeDto
                    {
                        Code = $"{p2}0100", Name = $"测试市{p}", Level = 2,
                        Children =
                        [
                            new RegionNodeDto { Code = $"{p2}0101", Name = $"测试区{p}", Level = 3, Children = streets }
                        ]
                    }
                ]
            });
        }
        return new RegionSnapshot { Version = "回映射测试版本", Source = "测试数据源", Nodes = nodes };
    }

    [Fact]
    public async Task Update_CompletionLogThrows_ReportsFailedWithStageMappedBack()
    {
        var db = CreateDb();
        var snow = new SnowflakeIdGenerator(1, 1);
        var source = new StubSource { Snapshot = ValidSnapshot() };
        var updater = new RegionUpdateService(db, source, new BuiltinRegionPackageProvider(),
            new RegionWriteService(db), snow, new CompletionLogThrowingLogger());

        var (ok, _) = updater.StartOnlineUpdate();
        Assert.True(ok);

        RegionUpdateProgress progress;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            await Task.Delay(50);
            progress = updater.GetProgress();
        } while (progress.Status == RegionUpdateStatus.Running && DateTime.UtcNow < deadline);

        // 完成日志抛异常：任务按失败收场，阶段由「完成」回映射为「写入」
        Assert.Equal(RegionUpdateStatus.Failed, progress.Status);
        Assert.Equal("写入", progress.Stage);
        Assert.StartsWith("写入失败", progress.Error);
        Assert.False(progress.Error!.StartsWith("校验失败"));

        // 写入事务本身已提交：数据实际已替换（失败仅发生在收尾日志）
        Assert.Equal(10100, db.Queryable<SysRegion>().Count());

        // 单任务闸已释放：可再次启动
        var (again, _) = updater.StartOnlineUpdate();
        Assert.True(again);
    }

    private sealed class StubSource : IRegionDataSource
    {
        public RegionSnapshot Snapshot { get; init; } = new();
        public Task<RegionSnapshot> FetchLatestAsync(Action<RegionFetchProgress> onProgress, CancellationToken ct)
        {
            onProgress(new RegionFetchProgress { Stage = "抓取数据", FetchedCount = 1, Percent = 70 });
            return Task.FromResult(Snapshot);
        }
    }

    // ==================== RegionDataImporter：防御抛出与 children 两侧 ====================

    [Fact]
    public void LoadBuiltinSnapshot_MissingResource_ThrowsWithResourceName()
    {
        // 测试程序集没有内嵌区划数据包：防御分支应带资源名报错
        var ex = Assert.Throws<InvalidOperationException>(
            () => RegionDataImporter.LoadBuiltinSnapshot(typeof(RegionCoverageGapTests).Assembly));
        Assert.Contains("区划数据包资源缺失", ex.Message);
        Assert.Contains("regions.json", ex.Message);
    }

    [Fact]
    public void ParseSnapshot_NullPayload_ThrowsAsEmpty()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("null"));
        var ex = Assert.Throws<InvalidOperationException>(() => RegionDataImporter.ParseSnapshot(stream));
        Assert.Equal("区划数据包解析结果为空", ex.Message);
    }

    [Fact]
    public void ParseSnapshot_ChildrenNullAndPresent_BothSidesMapped()
    {
        const string json = """
            {"version":"测试版本","source":"测试源","nodes":[
              {"code":"110000","name":"北京市","level":1,"children":null},
              {"code":"320000","name":"江苏省","level":1,"children":[
                {"code":"320500","name":"苏州市","level":2}
              ]}
            ]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var snapshot = RegionDataImporter.ParseSnapshot(stream);

        Assert.Equal("测试版本", snapshot.Version);
        Assert.Equal(2, snapshot.Nodes.Count);
        // children 为 null：映射为空列表
        Assert.Empty(snapshot.Nodes[0].Children);
        // children 非空：逐级映射
        var child = Assert.Single(snapshot.Nodes[1].Children);
        Assert.Equal("320500", child.Code);
        Assert.Equal("苏州市", child.Name);
    }
}
