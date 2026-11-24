using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.Infrastructure;

/// <summary>
/// 终账收尾基建缺口清零（分支 100% 硬门禁）：
/// ①ApiEndpointScanner：PUT/DELETE/PATCH 无模板特性（Template ?? "" 的 null 侧）；
/// ②McaRegionDataSource：displaycode 缺失行（?? "" null 侧）与 rows:null 信封（反序列化 null 侧）；
/// ③SqlSugarSetup：EntityValue null 侧、pars null 侧、RunCodeFirst seedAction null 侧。
/// </summary>
public class FinalGapInfraTests
{
    // ==================== ApiEndpointScanner：无模板 PUT/DELETE/PATCH ====================

    [Route("api/noroute")]
    private sealed class NoTemplateController : ControllerBase
    {
        [HttpPut]
        [Permission("nt:put")]
        public string Put() => "";

        [HttpDelete]
        [Permission("nt:delete")]
        public string Del() => "";

        [HttpPatch]
        [Permission("nt:patch")]
        public string Patch() => "";
    }

    [Fact]
    public void Scan_无模板PUT_DELETE_PATCH_路径无模板段()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<SysApi>();

        ApiEndpointScanner.SyncApis(db, new SnowflakeIdGenerator(8, 8), typeof(NoTemplateController).Assembly);

        var rows = db.Queryable<SysApi>().Where(a => a.Path == "/api/noroute").ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain("//", r.Path!)); // 空模板不产生双斜杠
        Assert.Contains(rows, r => r.HttpMethod == "PUT");
        Assert.Contains(rows, r => r.HttpMethod == "DELETE");
        Assert.Contains(rows, r => r.HttpMethod == "PATCH");
    }

    // ==================== McaRegionDataSource：displaycode null 与 rows null ====================

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Responder(request));
    }

    private static McaRegionDataSource Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler { Responder = responder };
        return new McaRegionDataSource(new HttpClient(handler),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<McaRegionDataSource>());
    }

    private static HttpResponseMessage Json(string json)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static string Envelope(string dataJson) => $$"""{"code":200,"data":{{dataJson}},"errors":null}""";

    [Fact]
    public async Task FetchLatest_子级行缺displaycode_按空码映射不抛()
    {
        // 省级清单 25 个有效省；首个省份的子树中混入一行缺 displaycode（?? "" null 侧）
        var provinces = string.Join(",", Enumerable.Range(20, 25)
            .Select(i => $"{{\"areaname\":\"省{i}\",\"displaycode\":\"{i}0000\"}}"));
        var svc = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(Envelope($"{{\"data\":[{provinces}]}}"));
            // 子树响应：按请求的 searchStr 生成根节点，并混入一行缺 displaycode 的子级
            var form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var code = form.Split('&').First(kv => kv.StartsWith("searchStr="))["searchStr=".Length..];
            return Json(Envelope(
                $$"""{"data":[{"areaname":"根","displaycode":"{{code}}","children":[{"areaname":"无码区"}]}]}"""));
        });

        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);

        Assert.Equal(25, snapshot.Nodes.Count);
        Assert.All(snapshot.Nodes, p => Assert.Equal(string.Empty, Assert.Single(p.Children).Code)); // displaycode 缺失按空串映射
    }

    [Fact]
    public async Task FetchLatest_rows为null信封_按空清单拒绝()
    {
        // data.data.rows 为 JSON null：反序列化返回 null（buffered == null 侧），按空清单抛明确异常
        var svc = Create(_ => Json(Envelope("""{"data":{"rows":null}}""")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单为空", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_行缺areaname_按空名映射不抛()
    {
        // 根与子级行均缺 areaname（?? "" null 侧），displaycode/children 齐备
        var provinces = string.Join(",", Enumerable.Range(20, 25)
            .Select(i => $"{{\"areaname\":\"省{i}\",\"displaycode\":\"{i}0000\"}}"));
        var svc = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(Envelope($"{{\"data\":[{provinces}]}}"));
            var form = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var code = form.Split('&').First(kv => kv.StartsWith("searchStr="))["searchStr=".Length..];
            return Json(Envelope(
                $$"""{"data":[{"areaname":null,"displaycode":"{{code}}","children":[{"areaname":null,"displaycode":"110101"}]}]}"""));
        });

        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);

        Assert.Equal(25, snapshot.Nodes.Count);
        Assert.All(snapshot.Nodes, p =>
        {
            Assert.Equal(string.Empty, p.Name);          // 根缺 areaname 按空串映射
            Assert.Equal(string.Empty, Assert.Single(p.Children).Name); // 子级缺 areaname 同样空串
        });
    }

    // ==================== SqlSugarSetup：AOP 与种子 null 侧 ====================

    private sealed class TenantRow : TaoAdmin.Common.Entities.ITenantEntity
    {
        public long Id { get; set; }
        public long TenantId { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void RunCodeFirst_seedAction为null_仅建表不抛()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });

        // seedAction == null：?. 短路侧，仅建表
        db.RunCodeFirst(null!, typeof(TenantRow));
        db.Insertable(new TenantRow { Id = 1, Name = "x" }).ExecuteCommand();
        Assert.Equal(1, db.Queryable<TenantRow>().Count());
    }

    [Fact]
    public async Task AOP_表达式更新无实体值_EntityValueNull侧不抛()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<TenantRow>();
        db.Insertable(new TenantRow { Id = 1, Name = "旧名" }).ExecuteCommand();

        // SetColumns 表达式更新：无实体对象，DataExecuting 的 EntityValue 为 null（?. 短路侧）
        await db.Updateable<TenantRow>()
            .SetColumns(r => r.Name == "新名")
            .Where(r => r.Id == 1)
            .ExecuteCommandAsync();

        Assert.Equal("新名", db.Queryable<TenantRow>().First().Name);
    }

    [Fact]
    public async Task OnLogExecuting_无参SQL_parsNull侧不抛()
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        db.CodeFirst.InitTables<TenantRow>();

        // 无参数 SQL：OnLogExecuting 的 pars 为 null（?? string.Empty 侧）
        var rows = db.Ado.SqlQuery<int>("SELECT COUNT(1) AS C FROM TenantRow");
        Assert.NotEmpty(rows);
    }
}
