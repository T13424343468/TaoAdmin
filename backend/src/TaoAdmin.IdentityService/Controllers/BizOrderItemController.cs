// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件；手改逻辑请写 *AppService.Extensions.cs 扩展文件） ====
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Auth;
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Web;
using TaoAdmin.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TaoAdmin.IdentityService.Controllers;

/// <summary>订单明细管理（代码生成器生成）</summary>
[Route("biz_order_item")]
public class BizOrderItemController : BaseApiController
{
    private readonly BizOrderItemAppService _svc;
    public BizOrderItemController(BizOrderItemAppService svc) => _svc = svc;

    /// <summary>分页查询</summary>
    [HttpGet]
    [Permission("gen:biz_order_item:manage")]
    public async Task<ApiResult<PageResult<BizOrderItem>>> Page([FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 10, [FromQuery] string? keyword = null)
    {
        var query = Request.Query
            .Where(kv => kv.Key is not ("pageIndex" or "pageSize" or "keyword"))
            .ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        return ApiOk(await _svc.PageAsync(pageIndex, pageSize, keyword, query));
    }

    /// <summary>详情</summary>
    [HttpGet("{id}")]
    [Permission("gen:biz_order_item:manage")]
    public async Task<ApiResult<BizOrderItem>> Get(string id)
        => ApiOk(await _svc.GetAsync(long.Parse(id)));

    /// <summary>保存（新增或更新）</summary>
    [HttpPost]
    [Permission("gen:biz_order_item:manage")]
    public async Task<ApiResult<object>> Save([FromBody] BizOrderItemSaveRequest req)
    {
        var id = await _svc.SaveAsync(req);
        return ApiOk(id > 0 ? id.ToString() : "保存成功");
    }

    /// <summary>删除（软删）</summary>
    [HttpDelete("{id}")]
    [Permission("gen:biz_order_item:manage")]
    public async Task<ApiResult<object>> Delete(string id)
    {
        await _svc.DeleteAsync(long.Parse(id));
        return ApiOk("删除成功");
    }

    /// <summary>批量删除（软删，返回实际删除数）</summary>
    [HttpPost("batch-delete")]
    [Permission("gen:biz_order_item:manage")]
    public async Task<ApiResult<object>> BatchDelete([FromBody] BatchIdsRequest req)
        => ApiOk<object>(new { deleted = await _svc.BatchDeleteAsync(req.Ids) });
}
