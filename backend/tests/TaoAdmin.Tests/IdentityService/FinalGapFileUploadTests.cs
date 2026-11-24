using Microsoft.AspNetCore.Http;
using TaoAdmin.IdentityService.Controllers;
using Xunit;

namespace TaoAdmin.Tests.IdentityService;

/// <summary>
/// 终账收尾文件上传缺口清零（分支 100% 硬门禁）：
/// file == null 侧（未携带文件字段）直接返回业务失败，不触达 FileAppService。
/// </summary>
public class FinalGapFileUploadTests
{
    [Fact]
    public async Task Upload_未携带文件_返回业务失败且不触达存储服务()
    {
        var controller = new FileController(null!)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new DefaultHttpContext() // ApiFail 读取 TraceIdentifier：挂最小上下文
            }
        };

        var result = await controller.Upload(null!);

        Assert.False(result.Success);
        Assert.Contains("请选择要上传的文件", result.Message);
    }
}
