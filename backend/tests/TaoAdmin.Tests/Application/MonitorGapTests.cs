using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using TaoAdmin.Application.Services;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// MonitorAppService 缺口补测（存量补账班，分支 100% 硬门禁）：
/// 服务健康探测三态（未配置 DOWN / 探活 UP 含实例 / 探活失败 DOWN）、
/// 日志过滤三路（级别/服务/关键字，含大小写不敏感与不匹配剔除）、
/// 文件名回退服务来源、目录不存在、翻页越界。
/// </summary>
public class MonitorAppServiceGapTests : IDisposable
{
    private readonly string _dir;

    public MonitorAppServiceGapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "taoadmin-loggap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private MonitorAppService Create(Dictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?> { ["Monitor:LogDir"] = _dir };
        if (extra != null) foreach (var kv in extra) dict[kv.Key] = kv.Value;
        return new MonitorAppService(new ConfigurationBuilder().AddInMemoryCollection(dict).Build(),
            new GapHttpClientFactory());
    }

    private class GapHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>起一个只回 200 OK 的本机 TCP 假健康端点，返回实际监听地址。</summary>
    private static async Task<string> StartFakeHealthAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    await stream.ReadAsync(buf);
                    var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                    await stream.WriteAsync(resp);
                }
            }
            catch { /* listener stopped */ }
        });
        return $"http://127.0.0.1:{port}|{listener}";
    }

    [Fact]
    public async Task GetServicesAsync_NoUrlConfigured_AllDown()
    {
        var services = await Create().GetServicesAsync();
        Assert.Equal(3, services.Count);
        Assert.All(services, s => Assert.Equal("DOWN", s.status));
        Assert.All(services, s => Assert.Empty(s.instanceList));
    }

    [Fact]
    public async Task GetServicesAsync_HealthUp_StatusUpWithInstance()
    {
        var fake = await StartFakeHealthAsync();
        var parts = fake.Split('|');
        try
        {
            var url = parts[0];
            var services = await Create(new Dictionary<string, string?>
            {
                ["Monitor:Services:gateway"] = url,
                ["Monitor:Services:identity"] = url,
                ["Monitor:Services:task"] = url
            }).GetServicesAsync();
            Assert.All(services, s => Assert.Equal("UP", s.status));
            Assert.All(services, s => Assert.Single(s.instanceList));
            Assert.All(services, s => Assert.Equal(1, s.instances));
            Assert.All(services, s => Assert.Equal(127, s.instanceList[0].host == "127.0.0.1" ? 127 : 0));
            Assert.All(services, s => Assert.False(string.IsNullOrEmpty(s.lastHealthCheck)));
        }
        finally
        {
            // TcpListener 无 Close 引用，进程结束自动释放；解析端口留空即可
            GC.KeepAlive(parts);
        }
    }

    [Fact]
    public async Task GetServicesAsync_HealthRefused_StatusDown()
    {
        // 占住端口再释放，得到一个确定拒连的端口
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var deadPort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var services = await Create(new Dictionary<string, string?>
        {
            ["Monitor:Services:gateway"] = $"http://127.0.0.1:{deadPort}"
        }).GetServicesAsync();
        var gw = Assert.Single(services, s => s.serviceName == "taoadmin-gateway");
        Assert.Equal("DOWN", gw.status);
        Assert.Equal(0, gw.instances);
        Assert.Empty(gw.instanceList);
    }

    [Fact]
    public async Task GetLogsAsync_LevelFilterCaseInsensitive()
    {
        File.WriteAllLines(Path.Combine(_dir, "a.jsonl"),
        [
            "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"warn one\",\"@l\":\"WRN\"}",
            "{\"@t\":\"2026-11-22T10:00:02.0000000Z\",\"@m\":\"err one\",\"@l\":\"ERROR\"}"
        ]);
        var (total, items) = await Create().GetLogsAsync(1, 20, "warn", null, null);
        Assert.Equal(1, total);
        Assert.Equal("WARN", items[0].level);
        // 大小写不敏感：传大写也能命中小写级别
        var (total2, _) = await Create().GetLogsAsync(1, 20, "ERROR", null, null);
        Assert.Equal(1, total2);
    }

    [Fact]
    public async Task GetLogsAsync_ServiceFilterExcludesNonMatch()
    {
        File.WriteAllLines(Path.Combine(_dir, "gw.jsonl"),
        [
            "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"from gw\",\"Service\":\"taoadmin-gateway\"}",
            "{\"@t\":\"2026-11-22T10:00:02.0000000Z\",\"@m\":\"from id\",\"Service\":\"taoadmin-identity\"}"
        ]);
        var (total, items) = await Create().GetLogsAsync(1, 20, null, "GATEWAY", null);
        Assert.Equal(1, total);
        Assert.Equal("from gw", items[0].message);
    }

    [Fact]
    public async Task GetLogsAsync_KeywordFilterExcludesNonMatch()
    {
        File.WriteAllLines(Path.Combine(_dir, "k.jsonl"),
        [
            "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"order created ok\"}",
            "{\"@t\":\"2026-11-22T10:00:02.0000000Z\",\"@m\":\"user deleted\"}"
        ]);
        var (total, items) = await Create().GetLogsAsync(1, 20, null, null, "created");
        Assert.Equal(1, total);
        Assert.Contains("created", items[0].message);
    }

    [Fact]
    public async Task GetLogsAsync_FallbackServiceFromFileName()
    {
        // 行内无 Service 属性：服务来源回退到文件名
        File.WriteAllLines(Path.Combine(_dir, "fallbacksvc.jsonl"),
        [
            "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"no service prop\"}"
        ]);
        var (_, items) = await Create().GetLogsAsync(1, 20, null, null, null);
        var item = Assert.Single(items);
        Assert.Equal("fallbacksvc", item.service);
    }

    [Fact]
    public async Task GetLogsAsync_MissingDir_ReturnsEmpty()
    {
        var svc = Create(new Dictionary<string, string?> { ["Monitor:LogDir"] = Path.Combine(_dir, "nope") });
        var (total, items) = await svc.GetLogsAsync(1, 20, null, null, null);
        Assert.Equal(0, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task GetLogsAsync_NoLogDirConfig_FallsBackToLogsDir()
    {
        // 未配置 Monitor:LogDir：回退默认相对目录 logs
        var cwd = Directory.GetCurrentDirectory();
        var defaultDir = Path.Combine(cwd, "logs");
        Directory.CreateDirectory(defaultDir);
        try
        {
            File.WriteAllLines(Path.Combine(defaultDir, "fallback-default.jsonl"),
            [
                "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"from default dir\"}"
            ]);
            var svc = Create(new Dictionary<string, string?> { ["Monitor:LogDir"] = null });
            var (total, items) = await svc.GetLogsAsync(1, 20, null, null, null);
            Assert.Equal(1, total);
            Assert.Equal("from default dir", items[0].message);
        }
        finally
        {
            Directory.Delete(defaultDir, true);
        }
    }

    [Fact]
    public async Task GetLogsAsync_PaginationBeyond_ReturnsEmptyPage()
    {
        File.WriteAllLines(Path.Combine(_dir, "p.jsonl"),
        [
            "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"only one\"}"
        ]);
        var (total, items) = await Create().GetLogsAsync(2, 20, null, null, null);
        Assert.Equal(1, total);
        Assert.Empty(items);
    }

    [Fact]
    public async Task GetLogsAsync_PageSizeOne_TakesFirst()
    {
        File.WriteAllLines(Path.Combine(_dir, "s.jsonl"),
        [
            "{\"@t\":\"2026-11-22T10:00:01.0000000Z\",\"@m\":\"first\"}",
            "{\"@t\":\"2026-11-22T10:00:02.0000000Z\",\"@m\":\"second\"}"
        ]);
        var (total, items) = await Create().GetLogsAsync(1, 1, null, null, null);
        Assert.Equal(2, total);
        Assert.Single(items);
    }
}

/// <summary>
/// SerilogLineParser 缺口补测：时间戳缺失/空值、级别全枚举、异常字段空值、
/// @m 非字符串回退 @mt、无消息字段空消息、非字符串参数渲染、空白行。
/// </summary>
public class SerilogLineParserGapTests
{
    private const string Ts = "\"@t\":\"2026-11-22T10:00:00.0000000Z\"";

    [Theory]
    [InlineData("DBG", "DEBUG")]
    [InlineData("DEBUG", "DEBUG")]
    [InlineData("Verbose", "DEBUG")]
    [InlineData("Debug", "DEBUG")]
    [InlineData("WRN", "WARN")]
    [InlineData("WARNING", "WARN")]
    [InlineData("Warning", "WARN")]
    [InlineData("ERR", "ERROR")]
    [InlineData("ERROR", "ERROR")]
    [InlineData("Fatal", "ERROR")]
    [InlineData("FTL", "ERROR")]
    [InlineData("Information", "INFO")]
    [InlineData("SomethingElse", "INFO")]
    public void Parse_LevelVariants_Mapped(string raw, string expect)
    {
        var line = $"{{{Ts},\"@m\":\"x\",\"@l\":\"{raw}\"}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal(expect, entry!.Level);
    }

    [Fact]
    public void Parse_EmptyTimestamp_ReturnsNull()
    {
        Assert.Null(SerilogLineParser.Parse("{\"@t\":\"\",\"@m\":\"x\"}"));
    }

    [Fact]
    public void Parse_NullLevel_FallsBackToInfo()
    {
        var line = $"{{{Ts},\"@m\":\"x\",\"@l\":null}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal("INFO", entry!.Level);
    }

    [Fact]
    public void Parse_NullTemplate_RendersEmptyMessage()
    {
        var line = $"{{{Ts},\"@mt\":null}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal(string.Empty, entry!.Message);
    }

    [Fact]
    public void Parse_NullExceptionField_ExceptionNull()
    {
        var line = $"{{{Ts},\"@m\":\"x\",\"@x\":null}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Null(entry!.Exception);
    }

    [Fact]
    public void Parse_NonStringRenderedMessage_FallsBackToTemplate()
    {
        // @m 是数字（非字符串）：回退到 @mt 渲染
        var line = $"{{{Ts},\"@m\":42,\"@mt\":\"count {{Count}}\",\"Count\":7}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal("count 7", entry!.Message);
    }

    [Fact]
    public void Parse_NoMessageFields_EmptyMessage()
    {
        var line = $"{{{Ts},\"SourceContext\":\"My.Logger\"}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal(string.Empty, entry!.Message);
        Assert.Equal("My.Logger", entry.Logger);
    }

    [Fact]
    public void Parse_NonStringParam_UsesJsonToString()
    {
        var line = $"{{{Ts},\"@mt\":\"n={{N}} b={{B}}\",\"N\":12,\"B\":true}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal("n=12 b=True", entry!.Message);
    }

    [Fact]
    public void Parse_StringParam_UsesRawString()
    {
        var line = $"{{{Ts},\"@mt\":\"hi {{Name}}\",\"Name\":\"老板\"}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Equal("hi 老板", entry!.Message);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsNull()
    {
        Assert.Null(SerilogLineParser.Parse("   "));
        Assert.Null(SerilogLineParser.Parse("\t"));
    }

    [Fact]
    public void Parse_NullOptionalFields_AllNull()
    {
        var line = $"{{{Ts},\"@m\":\"x\"}}";
        var entry = SerilogLineParser.Parse(line);
        Assert.NotNull(entry);
        Assert.Null(entry!.Service);
        Assert.Null(entry.Logger);
        Assert.Null(entry.Thread);
        Assert.Null(entry.TraceId);
        Assert.Null(entry.Exception);
    }
}
