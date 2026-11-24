using System.Net;
using TaoAdmin.Common.Storage;
using Xunit;

namespace TaoAdmin.Tests.Common;

/// <summary>
/// MinIO 存储实效测试：桩 HttpClient 捕获签名请求——
/// 上传/读取/删除走真实 V4 签名链，断言签名头与对象键。
/// </summary>
public class MinioFileStorageProviderTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest;
        public byte[]? Body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content != null)
                Body = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return Task.FromResult(Response);
        }
    }

    private static (MinioFileStorageProvider Svc, CaptureHandler Handler) Create(HttpResponseMessage? resp = null)
    {
        var handler = new CaptureHandler { Response = resp ?? new HttpResponseMessage(HttpStatusCode.OK) };
        return (new MinioFileStorageProvider("http://minio.internal:9000/", "ak", "sk", "taoadmin", new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task SaveAsync_上传成功_签名与对象键齐全()
    {
        var (svc, handler) = Create();
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello"));

        var path = await svc.SaveAsync(content, "a.png", "image/png");

        Assert.Matches(@"^/uploads/\d{4}/\d{2}/[0-9a-f]{32}\.png$", path);
        var req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Contains("/taoadmin/", req.RequestUri!.ToString());
        Assert.Equal("minio.internal", req.Headers.Host);
        Assert.True(req.Headers.Contains("x-amz-date"));
        Assert.Equal("UNSIGNED-PAYLOAD", req.Headers.GetValues("x-amz-content-sha256").First());
        var auth = req.Headers.Authorization!.ToString();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=ak/", auth);
        Assert.Contains("SignedHeaders=host, Signature=", auth);
        Assert.Equal("image/png", req.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SaveAsync_空内容类型走默认()
    {
        var (svc, handler) = Create();
        using var content = new MemoryStream(new byte[] { 1 });
        await svc.SaveAsync(content, "b.bin", "");
        Assert.Equal("application/octet-stream", handler.LastRequest!.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SaveAsync_失败抛异常()
    {
        var (svc, _) = Create(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.SaveAsync(content, "a.txt", "text/plain"));
    }

    [Fact]
    public async Task OpenAsync_成功返回流_失败返回null()
    {
        var (svc, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 9, 9 })
        });
        var stream = await svc.OpenAsync("/uploads/2026/11/x.bin");
        Assert.NotNull(stream);
        var bytes = new byte[2];
        await stream!.ReadExactlyAsync(bytes);
        Assert.Equal(new byte[] { 9, 9 }, bytes);
        Assert.Contains("/taoadmin/uploads/2026/11/x.bin", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);

        var (svc404, _) = Create(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await svc404.OpenAsync("/missing.bin"));
    }

    [Fact]
    public async Task DeleteAsync_成功不抛_失败抛()
    {
        var (svc, handler) = Create();
        await svc.DeleteAsync("/uploads/2026/11/x.bin");
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);

        var (svc500, _) = Create(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => svc500.DeleteAsync("/x"));
    }
}

/// <summary>本地盘存储实效：保存/读取/删除/防目录穿越。</summary>
public class LocalFileStorageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tao-local-store-{Guid.NewGuid():N}");
    private readonly LocalFileStorageProvider _provider;

    public LocalFileStorageProviderTests()
    {
        _provider = new LocalFileStorageProvider(_root, () => 12345);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task SaveOpenDelete_全链路()
    {
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("data"));
        var relative = await _provider.SaveAsync(content, "photo.png", "image/png");
        Assert.Matches(@"^/uploads/\d{4}/\d{2}/12345\.png$", relative);

        using var opened = await _provider.OpenAsync(relative);
        using var ms = new MemoryStream();
        await opened!.CopyToAsync(ms);
        Assert.Equal("data", System.Text.Encoding.UTF8.GetString(ms.ToArray()));

        await _provider.DeleteAsync(relative);
        Assert.Null(await _provider.OpenAsync(relative));
    }

    [Fact]
    public async Task Open_不存在或穿越路径返回null()
    {
        Assert.Null(await _provider.OpenAsync("/uploads/2026/11/missing.png"));
        Assert.Null(await _provider.OpenAsync("/uploads/../../etc/passwd")); // 目录穿越被拦
    }

    [Fact]
    public async Task Delete_不存在不抛()
    {
        await _provider.DeleteAsync("/uploads/2026/11/missing.png"); // 不抛异常
    }
}
