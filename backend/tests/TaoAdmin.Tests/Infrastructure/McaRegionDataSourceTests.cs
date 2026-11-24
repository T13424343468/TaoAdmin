using System.Net;
using TaoAdmin.Infrastructure.Persistence;
using Xunit;

namespace TaoAdmin.Tests.Infrastructure;

/// <summary>
/// McaRegionDataSource 实效测试：桩 HttpClient 模拟民政部平台响应，
/// 走真实解析/过滤/重试/异常链；DeriveLevel 纯函数全覆盖。
/// </summary>
public class McaRegionDataSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request));
        }
    }

    private static (McaRegionDataSource Svc, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new StubHandler();
        if (responder != null) handler.Responder = responder;
        var svc = new McaRegionDataSource(new HttpClient(handler),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<McaRegionDataSource>());
        return (svc, handler);
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static string Envelope(string dataJson) => $$"""{"code":200,"data":{{dataJson}},"errors":null}""";

    private static string ProvinceListJson()
    {
        var rows = new List<string> { "{\"areaname\":\"北京市\",\"displaycode\":\"110000\"}", "{\"areaname\":\"台湾省\",\"displaycode\":\"资料暂缺\"}" };
        for (var i = 2; i <= 31; i++) // 共 31 个有效省（20~40 区间内）
            rows.Add($"{{\"areaname\":\"省{i:D2}\",\"displaycode\":\"{i:D2}0000\"}}");
        return Envelope($"{{\"data\":[{string.Join(",", rows)}]}}");
    }

    private static string SubtreeJson(string code, string name)
    {
        var childCode = $"{code[..2]}0100";
        var inner = $"{{\"total\":1,\"rows\":[{{\"areaname\":\"{name}\",\"displaycode\":\"{code}\",\"children\":[{{\"areaname\":\"市辖区\",\"displaycode\":\"{childCode}\",\"children\":[]}}]}}]}}";
        return Envelope("{\"data\":" + inner + "}");
    }

    [Fact]
    public async Task DeriveLevel_四级码长全覆盖()
    {
        Assert.Equal(1, McaRegionDataSource.DeriveLevel("110000"));   // 省
        Assert.Equal(2, McaRegionDataSource.DeriveLevel("110100"));   // 地级市
        Assert.Equal(3, McaRegionDataSource.DeriveLevel("110105"));   // 区县
        Assert.Equal(4, McaRegionDataSource.DeriveLevel("110105001")); // 街道
    }

    [Fact]
    public async Task FetchLatest_省级清单为空抛明确异常()
    {
        var (svc, _) = Create(_ => Json(Envelope("""{"data":[]}""")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单为空", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_省级数量异常抛明确异常()
    {
        // 伪造 3 个有效省码（不在 20~40 区间），占位条目被过滤
        var rows = string.Join(",", Enumerable.Range(1, 3)
            .Select(i => $"{{\"areaname\":\"省{i}\",\"displaycode\":\"{i:D2}0000\"}}"));
        var (svc, _) = Create(_ => Json(Envelope($$"""{"data":[{{rows}}]}""")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单数量", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_接口错误码抛明确异常()
    {
        var (svc, _) = Create(_ => Json(
            """{"code":500,"data":null,"errors":[{"errorCode":"E1","msg":"系统繁忙"}]}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("E1", ex.Message);
        Assert.Contains("系统繁忙", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_HTTP非成功抛明确异常()
    {
        var (svc, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("HTTP 503", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_单省失败整体失败并留省名()
    {
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            return new HttpResponseMessage(HttpStatusCode.InternalServerError); // 子树请求失败
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("北京市", ex.Message);
        Assert.Contains("子树获取未成功", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_子树返回空行重试后抛未找到()
    {
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            return Json(Envelope("""{"data":{"total":0,"rows":[]}}""")); // 子树空
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("未找到该省根节点", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_完整链路成功_版本页取截止日期()
    {
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                var name = code == "110000" ? "北京市" : $"省{code[..2]}";
                return Json(SubtreeJson(code, name));
            }
            // 版本说明页
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>数据截止日期为2025年12月31日</html>")
            };
        });
        var progress = new List<TaoAdmin.Application.Services.RegionFetchProgress>();
        var snapshot = await svc.FetchLatestAsync(progress.Add, CancellationToken.None);

        Assert.Equal(31, snapshot.Nodes.Count);
        Assert.Equal("110000", snapshot.Nodes[0].Code);
        Assert.Equal("民政部国家地名信息库·数据截止2025年12月31日", snapshot.Version);
        Assert.Contains("民政部", snapshot.Source);
        Assert.True(progress.Count > 2); // 连接数据源 + 每省抓取 + 完成
        Assert.Equal("抓取数据", progress[^1].Stage);
    }

    [Fact]
    public async Task FetchLatest_版本页失败退化为抓取日期()
    {
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                return Json(SubtreeJson(code, "北京市"));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound); // 版本页失败
        });
        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);
        Assert.StartsWith("民政部国家地名信息库·抓取于", snapshot.Version);
    }

    [Fact]
    public async Task FetchLatest_版本页无截止文案退化为抓取日期()
    {
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                return Json(SubtreeJson(code, "北京市"));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>无日期文案</html>")
            };
        });
        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);
        Assert.StartsWith("民政部国家地名信息库·抓取于", snapshot.Version);
    }

    [Fact]
    public async Task FetchLatest_行数组结构兼容解析()
    {
        // 子级查询走 data.data 行数组结构（ProvinceListJson 已覆盖）；
        // 此处验证 $ref 引用串被跳过、对象行保留
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(Envelope("""{"data":[{"areaname":"北京市","displaycode":"110000"},"$ref$1"]}"""));
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        // 只有 1 个有效省（<20），先触发数量异常——但 $ref 行已被安全跳过（否则解析抛错）
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单数量 1", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_信封data为null_解析兜底返回空触发数量异常()
    {
        // data 为 null（非 Object）→ ParseRowsOrList 返回 null → 有效省 0 条
        var (svc, _) = Create(_ => Json(Envelope("null")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FetchLatestAsync(_ => { }, CancellationToken.None));
        Assert.Contains("省级清单为空", ex.Message);
    }

    [Fact]
    public async Task FetchLatest_子树rows对象结构成功解析出根节点()
    {
        // 子树走 data.data 为 {total, rows} 对象结构，成功解析并产出节点
        var (svc, handler) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                return Json(SubtreeJson(code, "北京市"));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>数据截止日期为2025年12月31日</html>")
            };
        });
        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);
        var bj = snapshot.Nodes.FirstOrDefault(n => n.Name == "北京市");
        Assert.NotNull(bj);
        Assert.Equal("110000", bj!.Code);
        Assert.Contains(bj, snapshot.Nodes);
    }

    [Fact]
    public async Task FetchVersion_说明页请求抛异常_版本号退化为抓取日期()
    {
        // 版本说明页请求直接抛异常 → catch 记警告并返回 null → 版本号退化
        var (svc, _) = Create(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("queryXzqhChildren"))
                return Json(ProvinceListJson());
            if (url.Contains("queryXzqhList"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var code = System.Text.RegularExpressions.Regex.Match(body, "searchStr=(\\d+)").Groups[1].Value;
                return Json(SubtreeJson(code, "北京市"));
            }
            throw new HttpRequestException("version page boom"); // 版本说明页
        });
        var snapshot = await svc.FetchLatestAsync(_ => { }, CancellationToken.None);
        Assert.StartsWith("民政部国家地名信息库·抓取于", snapshot.Version);
    }
}
