using Microsoft.AspNetCore.Http;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 文件上传空文件守卫实效断言：file.Length == 0 侧在空 FormFile 下直接返回业务失败，
/// 不触达 FileAppService（失败路径不依赖存储服务，传 null 安全）。
/// </summary>
public class FileControllerUploadGapTests
{
    [Fact]
    public async Task Upload_空文件_返回业务失败且不触达存储服务()
    {
        var controller = new FileController(null!)
        {
            // ApiFail 读取 HttpContext.TraceIdentifier：挂最小上下文即可
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var empty = new FormFile(Stream.Null, 0, 0, "file", "empty.txt");

        var result = await controller.Upload(empty);

        Assert.False(result.Success);
        Assert.Contains("请选择要上传的文件", result.Message);
    }
}
