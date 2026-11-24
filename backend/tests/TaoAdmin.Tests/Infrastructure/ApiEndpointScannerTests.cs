using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.Infrastructure;

/// <summary>
/// ApiEndpointScanner 实效测试：内存 SQLite 走真实增量同步——
/// 补录新增、更新变化、停用消失、幂等重跑；反射扫描纯逻辑全覆盖。
/// </summary>
public class ApiEndpointScannerTests
{
    private static SqlSugarClient CreateDb()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysApi>();
        return db;
    }

    [Route("api/demo")]
    private sealed class DemoController : ControllerBase
    {
        [HttpGet("list")]
        [Permission("demo:list")]
        public string Page() => "";

        [HttpPost("save")]
        [Permission("demo:save")]
        public string Save() => "";

        [HttpDelete("{id}")]
        [Permission("demo:delete")]
        public string Remove() => "";

        [HttpPut("update/{id}")]
        [Permission("demo:update")]
        public string Update() => "";

        [HttpPatch("patch")]
        [Permission("demo:patch")]
        public string Patch() => "";

        [AcceptVerbs("GET", "POST")]
        [Route("verbs")]
        [Permission("demo:verbs")]
        public string Verbs() => "";

        [HttpGet("no-perm")]
        public string NoPerm() => ""; // 无 Permission：不进扫描结果

        public string NoHttp() => ""; // 无 HTTP 特性：不进扫描结果
    }

    private sealed class EmptyController : ControllerBase { }

    [Fact]
    public void ScanEndpoints_全HTTP方法与过滤()
    {
        var endpoints = ApiEndpointScanner.ScanEndpoints(new[] { typeof(ApiEndpointScannerTests).Assembly }).ToList();

        var get = endpoints.FirstOrDefault(e => e.Path == "/api/demo/list");
        Assert.NotNull(get);
        Assert.Equal("GET", get!.HttpMethod);
        Assert.Equal("Demo-Page", get.Name);
        Assert.Equal("demo:list", get.PermsCode);

        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.Path == "/api/demo/save");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.Path == "/api/demo/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PUT" && e.Path == "/api/demo/update/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "PATCH" && e.Path == "/api/demo/patch");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.Path == "/api/demo"); // AcceptVerbs 取首个，空模板并入控制器路由
        Assert.DoesNotContain(endpoints, e => e.Path == "/api/demo/no-perm");
    }

    [Fact]
    public void ScanEndpoints_空程序集返回空()
    {
        var endpoints = ApiEndpointScanner.ScanEndpoints(Array.Empty<Assembly>());
        Assert.Empty(endpoints);
    }

    [Fact]
    public void SyncApis_空程序集直接返回不动库()
    {
        using var db = CreateDb();
        ApiEndpointScanner.SyncApis(db, new SnowflakeIdGenerator(1, 1));
        Assert.Equal(0, db.Queryable<SysApi>().Count());
    }

    [Fact]
    public void SyncApis_补录新增_更新变化_停用消失()
    {
        using var db = CreateDb();
        var snowflake = new SnowflakeIdGenerator(1, 1);

        // 预置一条已消失的旧接口 + 一条权限码变化的现存接口
        db.Insertable(new SysApi
        {
            Id = snowflake.NextId(), Name = "Old-Gone", Path = "/api/old", HttpMethod = "GET",
            PermsCode = "old:gone", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        }).ExecuteCommand();
        db.Insertable(new SysApi
        {
            Id = snowflake.NextId(), Name = "Demo-Page", Path = "/api/demo/list", HttpMethod = "GET",
            PermsCode = "demo:list-old", Status = 1, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        }).ExecuteCommand();

        ApiEndpointScanner.SyncApis(db, snowflake, typeof(ApiEndpointScannerTests).Assembly);

        // 新接口补录
        Assert.True(db.Queryable<SysApi>().Count(a => a.Path == "/api/demo/save") > 0);
        // 现存接口权限码与名称被更新
        var updated = db.Queryable<SysApi>().First(a => a.Path == "/api/demo/list");
        Assert.Equal("demo:list", updated.PermsCode);
        // 消失接口被停用（不删）
        var stale = db.Queryable<SysApi>().First(a => a.Path == "/api/old");
        Assert.Equal(0, stale.Status);
    }

    [Fact]
    public void SyncApis_重复执行幂等()
    {
        using var db = CreateDb();
        var snowflake = new SnowflakeIdGenerator(1, 1);
        ApiEndpointScanner.SyncApis(db, snowflake, typeof(ApiEndpointScannerTests).Assembly);
        var count = db.Queryable<SysApi>().Count();
        var staleCount = db.Queryable<SysApi>().Count(a => a.Status == 0);

        ApiEndpointScanner.SyncApis(db, snowflake, typeof(ApiEndpointScannerTests).Assembly);

        Assert.Equal(count, db.Queryable<SysApi>().Count());
        Assert.Equal(staleCount, db.Queryable<SysApi>().Count(a => a.Status == 0));
    }

    [Fact]
    public void SyncApis_已停用接口不重复更新()
    {
        using var db = CreateDb();
        var snowflake = new SnowflakeIdGenerator(1, 1);
        // 预置已停用的消失接口，重跑同步不应再触发更新
        db.Insertable(new SysApi
        {
            Id = snowflake.NextId(), Name = "Old-Gone", Path = "/api/old", HttpMethod = "GET",
            PermsCode = "old:gone", Status = 0, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        }).ExecuteCommand();
        var before = db.Queryable<SysApi>().First(a => a.Path == "/api/old").UpdatedAt;

        ApiEndpointScanner.SyncApis(db, snowflake, typeof(ApiEndpointScannerTests).Assembly);

        var after = db.Queryable<SysApi>().First(a => a.Path == "/api/old").UpdatedAt;
        Assert.Equal(before, after); // Status 已是 0，不再更新
    }
}
