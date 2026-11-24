using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TaoAdmin.Utilities;
using Xunit;

namespace TaoAdmin.Tests.Utilities;

/// <summary>HttpHelper：链式配置、五类动词、字符串/JSON 反序列化、非成功码抛错（实效断言）</summary>
public class HttpHelperTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EchoDto { Name = "ok" })
            };

        /// <summary>请求体在 SendAsync 处理中可能被释放，改为处理时即读出缓存。</summary>
        public string? CachedBody { get; private set; }

        public async Task<string> ReadRequestBodyAsync() => CachedBody ?? await LastRequest!.Content!.ReadAsStringAsync();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null) CachedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request);
        }
    }

    private sealed class EchoDto { public string Name { get; set; } = ""; }

    private sealed class StubFactory : IHttpClientFactory
    {
        public StubHandler Handler { get; } = new();
        public string? LastClientName { get; private set; }
        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return new HttpClient(Handler) { BaseAddress = new Uri("http://stub/") };
        }
    }

    private static HttpHelper CreateHelper(StubFactory factory, Action<HttpHelper>? configure = null)
    {
        var helper = new HttpHelper(factory);
        configure?.Invoke(helper);
        return helper;
    }

    [Fact]
    public async Task GetAsync_ShouldReturnDeserializedDto_AndUseConfiguredClientAndHeaders()
    {
        var factory = new StubFactory();
        var helper = CreateHelper(factory, h => { h.ClientName = "named"; h.Bearer("tok-1"); h.Header("X-Trace", "t1"); });

        var dto = await helper.GetAsync<EchoDto>("http://stub/x");

        Assert.Equal("named", factory.LastClientName);
        Assert.Equal("ok", dto!.Name);
        var req = factory.Handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("Bearer tok-1", req.Headers.GetValues("Authorization").Single());
        Assert.Equal("t1", req.Headers.GetValues("X-Trace").Single());
    }

    [Fact]
    public async Task PostAsync_WithAnonymousBody_ShouldSendJson_AndDeserializeResponse()
    {
        var factory = new StubFactory();
        var helper = CreateHelper(factory);

        var dto = await helper.PostAsync<EchoDto>("http://stub/p", new { a = 1 });

        Assert.Equal("ok", dto!.Name);
        var req = factory.Handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.NotNull(req.Content);
        var body = await factory.Handler.ReadRequestBodyAsync();
        Assert.Contains("\"a\"", body);
    }

    [Fact]
    public async Task PostAsync_WithHttpContentBody_ShouldPassThroughUntouched()
    {
        var factory = new StubFactory();
        var helper = CreateHelper(factory);
        using var content = new StringContent("raw-body", System.Text.Encoding.UTF8, "text/plain");

        await helper.PostAsync<EchoDto>("http://stub/p", content);

        var req = factory.Handler.LastRequest!;
        Assert.Same(content, req.Content);
    }

    [Fact]
    public async Task PostAsync_WithNullBody_ShouldSendNoContent()
    {
        var factory = new StubFactory();
        var helper = CreateHelper(factory);

        await helper.PostAsync<EchoDto>("http://stub/p", null);

        Assert.Null(factory.Handler.LastRequest!.Content);
    }

    [Fact]
    public async Task PutAsync_And_DeleteAsync_ShouldUseCorrespondingVerbs()
    {
        var factory = new StubFactory();
        var helper = CreateHelper(factory);

        await helper.PutAsync<EchoDto>("http://stub/u", new { a = 1 });
        Assert.Equal(HttpMethod.Put, factory.Handler.LastRequest!.Method);

        await helper.DeleteAsync<EchoDto>("http://stub/d");
        Assert.Equal(HttpMethod.Delete, factory.Handler.LastRequest!.Method);
    }

    [Fact]
    public async Task GetStringAsync_ShouldReturnRawBody_AndApplyTimeout()
    {
        var factory = new StubFactory();
        factory.Handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("plain-text") };
        var helper = CreateHelper(factory, h => h.Timeout(7));

        var text = await helper.GetStringAsync("http://stub/s");

        Assert.Equal("plain-text", text);
        // Timeout 配置落到 CreateClient 产出的客户端上（实效：错误配置会抛或值不符）
        Assert.Equal("default", factory.LastClientName);
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatusCode_ShouldThrowHttpRequestException()
    {
        var factory = new StubFactory();
        factory.Handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var helper = CreateHelper(factory);

        await Assert.ThrowsAsync<HttpRequestException>(() => helper.GetAsync<EchoDto>("http://stub/e"));
    }

    [Fact]
    public async Task SendAsync_StringTypeArgument_ShouldReturnStringNotJson()
    {
        var factory = new StubFactory();
        factory.Handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"quoted\"") };
        var helper = CreateHelper(factory);

        var text = await helper.GetAsync<string>("http://stub/s");

        Assert.Equal("\"quoted\"", text);
    }

    [Fact]
    public void AddHttpHelper_ShouldRegisterFactoryClientAndHelper()
    {
        var services = new ServiceCollection();
        services.AddHttpHelper();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>());
        Assert.NotNull(provider.GetRequiredService<HttpHelper>());
    }
}
