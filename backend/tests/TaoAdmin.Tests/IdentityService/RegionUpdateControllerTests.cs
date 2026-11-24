using Microsoft.AspNetCore.Http;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 区划更新入口实效测试：StartUpdate/RestoreBuiltin 的成功启动与单任务闸拦截两分支，
/// UpdateProgress 轮询入口。断言实效：返回值 Success/Message 与进度对象可区分。
/// 单任务闸用可阻塞桩卡住后台任务后再断言拦截，消除竞态假绿。
/// </summary>
public class RegionUpdateControllerTests
{
    private readonly SqlSugarClient _db;
    private readonly RegionController _controller;
    private readonly RegionUpdateService _update;
    private readonly BlockingDataSource _dataSource = new();
    private readonly BlockingPackageProvider _packageProvider = new();

    public RegionUpdateControllerTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysRegion, SysRegionMeta>();
        var svc = new RegionAppService(_db);
        _update = new RegionUpdateService(
            _db,
            _dataSource,
            _packageProvider,
            new RegionWriteService(_db),
            new SnowflakeIdGenerator(1, 1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RegionUpdateService>.Instance);
        _controller = new RegionController(svc, _update)
        {
            ControllerContext = { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>可阻塞在线数据源：拿到放行门才返回，用于把单任务闸卡在执行中。</summary>
    private sealed class BlockingDataSource : IRegionDataSource
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RegionSnapshot> FetchLatestAsync(Action<RegionFetchProgress> onProgress, CancellationToken ct)
            => Gate.Task.ContinueWith(_ => new RegionSnapshot());
    }

    /// <summary>可阻塞内置数据包：同上，卡住内置恢复路径。</summary>
    private sealed class BlockingPackageProvider : IRegionPackageProvider
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RegionSnapshot GetBuiltinSnapshot() => Gate.Task.Status == TaskStatus.RanToCompletion
            ? new RegionSnapshot()
            : Gate.Task.Wait(TimeSpan.FromSeconds(5)) ? new RegionSnapshot() : new RegionSnapshot();
    }

    [Fact]
    public void StartUpdate_EmptyDb_ShouldStart_AndExposeProgress()
    {
        _dataSource.Gate.TrySetResult(); // 不阻塞，跑完即释放闸
        var result = _controller.StartUpdate();
        Assert.True(result.Success);

        var progress = _controller.UpdateProgress();
        Assert.True(progress.Success);
        Assert.NotNull(progress.Data);
        // 空库启动：BeforeCount=0，来源为在线
        Assert.Equal(0, progress.Data!.BeforeCount);
        Assert.Equal("online", progress.Data.SourceKind);
    }

    [Fact]
    public void StartUpdate_SecondCall_ShouldBeRejectedBySingleTaskGate()
    {
        var first = _controller.StartUpdate(); // 数据源被卡住，任务保持执行中
        Assert.True(first.Success);
        Assert.True(_update.IsRunning);

        var second = _controller.StartUpdate();
        Assert.False(second.Success);
        Assert.Contains("已有更新任务", second.Message);

        _dataSource.Gate.TrySetResult(); // 放行收尾，释放闸
    }

    [Fact]
    public void RestoreBuiltin_ShouldStart_AndExposeProgress()
    {
        _packageProvider.Gate.TrySetResult();
        var result = _controller.RestoreBuiltin();
        Assert.True(result.Success);

        var progress = _controller.UpdateProgress();
        Assert.True(progress.Success);
        Assert.Equal("builtin", progress.Data!.SourceKind);
    }

    [Fact]
    public void RestoreBuiltin_SecondCall_ShouldBeRejectedBySingleTaskGate()
    {
        var first = _controller.RestoreBuiltin(); // 内置包被卡住，任务保持执行中
        Assert.True(first.Success);
        Assert.True(_update.IsRunning);

        var second = _controller.RestoreBuiltin();
        Assert.False(second.Success);
        Assert.Contains("已有更新任务", second.Message);

        _packageProvider.Gate.TrySetResult(); // 放行收尾
    }
}
