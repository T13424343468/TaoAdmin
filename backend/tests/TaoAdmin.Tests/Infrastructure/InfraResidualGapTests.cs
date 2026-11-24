using Microsoft.AspNetCore.Mvc;
using SqlSugar;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain.Entities;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.Infrastructure;

/// <summary>
/// 基础设施层残留缺口清零（分支 100% 硬门禁）：
/// ①ApiEndpointScanner 空 AcceptVerbs 与无动词方法不产出端点（HttpInfo 判定矩阵收口）；
/// ②DbInitializer 工作流种子：缺审批人跳过 / 空昵称走兜底 / 缺华南员工跳过第四实例；
/// ③McaRegionDataSource 解析矩阵：空码过滤、子树 data 非对象、行无匹配省码、信封为 null、
///   data 对象无 rows 属性、ToNode 空字段兜底。
/// </summary>
public class InfraResidualGapTests
{
    // ==================== ① ApiEndpointScanner：HttpInfo 判定矩阵收口 ====================

    [Route("api/verbgap")]
    private sealed class VerbGapController : ControllerBase
    {
        [HttpGet("ok")]
        [Permission("gap:ok")]
        public string OkRoute() => "";

        [AcceptVerbs] // 空动词数组：HttpMethods.Any() 为 false → 不识别为接口
        [Permission("gap:verbs-empty")]
        public string EmptyVerbs() => "";

        [Permission("gap:no-verb")] // 无任何 HTTP 动词特性：HttpInfo 返回 (null, "") → 跳过
        public string NoVerb() => "";
    }

    [Fact]
    public void ScanEndpoints_空AcceptVerbs与无动词方法不产出端点()
    {
        var endpoints = ApiEndpointScanner.ScanEndpoints(new[] { typeof(InfraResidualGapTests).Assembly }).ToList();

        // 对照组：同控制器正常动词方法确实被扫出，证明扫描真实运行
        Assert.Contains(endpoints, e => e.PermsCode == "gap:ok" && e.HttpMethod == "GET");
        Assert.DoesNotContain(endpoints, e => e.PermsCode == "gap:verbs-empty");
        Assert.DoesNotContain(endpoints, e => e.PermsCode == "gap:no-verb");
    }

    // ==================== ② DbInitializer：工作流种子三分支 ====================

    private static SqlSugarClient CreateDb()
        => new(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });

    private static SysUser User(long id, string userName, string? nickName)
        => new() { Id = id, UserName = userName, Password = "x", NickName = nickName, Status = 1, CreatedAt = DateTime.Now };

    [Fact]
    public void Initialize_仅预插east01_工作流种子因缺审批人跳过()
    {
        using var db = CreateDb();
        db.CodeFirst.InitTables<SysUser>();
        db.Insertable(User(1, "east01", "张员工")).ExecuteCommand();

        DbInitializer.Initialize(db, new SnowflakeIdGenerator(1, 1), typeof(InfraResidualGapTests).Assembly);

        // east01 已存在 → 演示数据块跳过 → SeedWorkflows 找不到 east_leader → 整体返回，不落实例
        Assert.Equal(0, db.Queryable<SysWorkflowInstance>().Count());
        Assert.True(db.Queryable<SysWorkflow>().Any(w => w.WorkflowCode == "leave_audit")); // 模板仍登记
    }

    [Fact]
    public void Initialize_预插空昵称演示账号_工作流种子走昵称兜底()
    {
        using var db = CreateDb();
        db.CodeFirst.InitTables<SysUser>();
        db.Insertable(new[]
        {
            User(1, "east01", null), User(2, "east_leader", null),
            User(3, "south_manager", null), User(4, "south01", null)
        }).ExecuteCommand();

        DbInitializer.Initialize(db, new SnowflakeIdGenerator(1, 1), typeof(InfraResidualGapTests).Assembly);

        var instances = db.Queryable<SysWorkflowInstance>().ToList();
        Assert.Equal(4, instances.Count);
        Assert.Contains(instances, i => i.ApplicantName == "张员工"); // NickName 为空 → 兜底文案
        Assert.Contains(instances, i => i.ApplicantName == "赵员工");
        var tasks = db.Queryable<SysWorkflowTask>().ToList();
        Assert.Contains(tasks, t => t.HandlerName == "李组长");
        Assert.Contains(tasks, t => t.HandlerName == "王经理");
    }

    [Fact]
    public void Initialize_缺华南员工_前三实例照常落库第四组跳过()
    {
        using var db = CreateDb();
        db.CodeFirst.InitTables<SysUser>();
        db.Insertable(new[]
        {
            User(1, "east01", "张员工"), User(2, "east_leader", "李组长"), User(3, "south_manager", "王经理")
        }).ExecuteCommand();

        DbInitializer.Initialize(db, new SnowflakeIdGenerator(1, 1), typeof(InfraResidualGapTests).Assembly);

        // south01 缺失：仅跳过第四组演示数据，前三组不受影响
        Assert.Equal(3, db.Queryable<SysWorkflowInstance>().Count());
        Assert.Equal(4, db.Queryable<SysWorkflowTask>().Count());
    }

    // ==================== ③ McaRegionDataSource：解析矩阵收口 ====================

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Responder(request));
    }

    private static McaRegionDataSource Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new HttpClient(new StubHandler { Responder = responder }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<McaRegionDataSource>.Instance);

    private static HttpResponseMessage Json(string json)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static string Envelope(string dataJson)
        => $$"""{"code":200,"data":{{dataJson}},"errors":null}""";

    private static string ProvinceListJson(params string[] extraRows)
    {
        var rows = new List<string> { "{\"areaname\":\"北京市\",\"displaycode\":\"110000\"}" };
        for (var i = 2; i <= 31; i++)
            rows.Add($"{{\"areaname\":\"省{i:D2}\",\"displaycode\":\"{i:D2}0000\"}}");
        rows.AddRange(extraRows);
        return Envelope($"{{\"data\":[{string.Join(",", rows)}]}}");
    }

    private static string SubtreeJson(string code, string name)
    {
        var childCode = $"{code[..2]}0100";
        var inner = $"{{\"total\":1,\"rows\":[{{\"areaname\":\"{name}\",\"displaycode\":\"{code}\",\"children\":[{{\"areaname\":\"市辖区\",\"displaycode\":\"{childCode}\",\"children\":[]}}]}}]}}";
        return Envelope("{\"data\":" + inner + "}");
    }

    private static HttpResponseMessage SubtreeFor(string code)
        => Json(SubtreeJson(code, code == "110000" ? "北京市" : $"省{code[..2]}"));

    [Fact]
    public async Task FetchLatest_省级清单含空码与非数字码_全部被过滤()
    {
        // 空码（displaycode 缺失）与非数字码（"11-000"）均不得进入抓取目标
        var svc = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson("{\"areaname\":\"无码占位\"}", "{\"areaname\":\"怪码\",\"displaycode\":\"11-000\"}"));
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                return SubtreeFor(code);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound); // 版本页失败即可
        });
        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);

        Assert.Equal(31, snapshot.Nodes.Count); // 占位行未混入目标
        Assert.DoesNotContain(snapshot.Nodes, n => n.Code == "" || n.Code.Contains('-'));
    }

    [Fact]
    public async Task FetchLatest_子树data非对象_按空行重试后抛未找到()
    {
        var svc = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            return Json(Envelope("""{"data":123}""")); // data 非对象 → 解析为 null
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("未找到该省根节点", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_子树行无匹配省码_重试后抛未找到()
    {
        var svc = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            return Json(Envelope("""{"data":{"total":1,"rows":[{"areaname":"别处","displaycode":"990000"}]}}"""));
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("未找到该省根节点", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_信封data对象无rows属性_省级清单为空()
    {
        var svc = Create(_ => Json(Envelope("""{"data":{"foo":1}}"""))); // data 属性为对象但无 rows → 解析为 null
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单为空", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_信封为null_省级清单为空()
    {
        // 响应体为 JSON null → 反序列化得到 null 信封 → errors 与 data 双双走空链兜底
        var svc = Create(_ => Json("null"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单为空", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_子树行缺名称与子级字段_ToNode空值兜底()
    {
        var svc = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                if (code == "110000")
                {
                    // 根行缺 areaname、子行缺 displaycode 与 children：三处空值兜底实效触达
                    return Json(Envelope(
                        """{"data":{"total":1,"rows":[{"displaycode":"110000","children":[{"areaname":"child"}]}]}}"""));
                }
                return SubtreeFor(code);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);

        var bj = snapshot.Nodes.First(n => n.Code == "110000");
        Assert.Equal("", bj.Name); // areaname 缺失 → 空串兜底
        var child = bj.Children.Single();
        Assert.Equal("", child.Code); // displaycode 缺失 → 空串兜底
        Assert.Empty(child.Children); // children 缺失 → 空集合兜底
    }
}
