using Microsoft.AspNetCore.Http;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Cache;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.IdentityService.Controllers;
using Quartz.Impl;
using Quartz.Simpl;
using Quartz.Spi;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 区划状态接口实效测试（SQLite 内存库真库）：
/// 覆盖 RegionController.Status 的 meta 空值兜底分支（?? "" 与 ?? 0 共 20 个分支）
/// 与 RegionStatusDto 属性往返。断言实效：空库与有数据两态结果可区分。
/// </summary>
public class RegionControllerStatusTests
{
    private readonly SqlSugarClient _db;
    private readonly RegionController _controller;

    public RegionControllerStatusTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysRegion, SysRegionMeta>();
        var svc = new RegionAppService(_db);
        var update = new RegionUpdateService(
            _db,
            new EmptyDataSource(),
            new EmptyPackageProvider(),
            new RegionWriteService(_db),
            new SnowflakeIdGenerator(1, 1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RegionUpdateService>.Instance);
        _controller = new RegionController(svc, update)
        {
            ControllerContext = { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Status_EmptyMeta_ShouldFallbackAllFields()
    {
        var result = await _controller.Status();
        Assert.True(result.Success);
        var dto = result.Data!;
        Assert.Equal("", dto.Version);
        Assert.Equal("", dto.Source);
        Assert.Null(dto.FetchedAt);
        Assert.Equal(0, dto.ProvinceCount);
        Assert.Equal(0, dto.CityCount);
        Assert.Equal(0, dto.CountyCount);
        Assert.Equal(0, dto.TownCount);
        Assert.Equal(0, dto.TotalCount);
        Assert.NotNull(dto.Update);
    }

    [Fact]
    public async Task Status_WithMeta_ShouldReturnRealValues()
    {
        _db.Insertable(new SysRegionMeta
        {
            Id = SysRegionMeta.SingleRowId,
            Version = "民政部国家地名信息库·数据截止2025年12月31日",
            Source = "民政部国家地名信息库在线抓取",
            FetchedAt = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            ProvinceCount = 33, CityCount = 333, CountyCount = 2845, TownCount = 38723
        }).ExecuteCommand();

        var result = await _controller.Status();
        Assert.True(result.Success);
        var dto = result.Data!;
        Assert.Contains("2025年12月31日", dto.Version);
        Assert.Contains("民政部", dto.Source);
        Assert.Equal(33, dto.ProvinceCount);
        Assert.Equal(333, dto.CityCount);
        Assert.Equal(2845, dto.CountyCount);
        Assert.Equal(38723, dto.TownCount);
        Assert.Equal(41934, dto.TotalCount);
        Assert.Equal(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), dto.FetchedAt);
    }

    private sealed class EmptyPackageProvider : IRegionPackageProvider
    {
        public RegionSnapshot GetBuiltinSnapshot() => new();
    }

    private sealed class EmptyDataSource : IRegionDataSource
    {
        public Task<RegionSnapshot> FetchLatestAsync(Action<RegionFetchProgress> onProgress, CancellationToken ct)
            => Task.FromResult(new RegionSnapshot());
    }
}
