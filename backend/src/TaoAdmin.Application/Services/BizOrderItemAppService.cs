// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件；手改逻辑请写 *AppService.Extensions.cs 扩展文件） ====
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using SqlSugar;

namespace TaoAdmin.Application.Services;

/// <summary>订单明细 应用服务（生成区）：分页/详情/增/改/删/批量删。业务个性逻辑写 BizOrderItemAppService.Extensions.cs。</summary>
public partial class BizOrderItemAppService(ISqlSugarClient db, SnowflakeIdGenerator snowflake)
{
    /// <summary>分页查询（keyword 走 like 字段；query 走设计字段的 eq/range 过滤）</summary>
    public async Task<PageResult<BizOrderItem>> PageAsync(int pageIndex, int pageSize, string? keyword, Dictionary<string, string>? query = null)
    {
        RefAsync<int> total = 0;
        var queryable = db.Queryable<BizOrderItem>()
            .Where(t => t.IsDeleted == false);
        queryable = queryable.WhereIF(!string.IsNullOrWhiteSpace(keyword), t => t.ProductName.Contains(keyword!));
        var list = await queryable
            .OrderByDescending(t => t.Id)
            .ToPageListAsync(pageIndex, pageSize, total);
        return new PageResult<BizOrderItem> { Items = list, Total = total, PageIndex = pageIndex, PageSize = pageSize };
    }

    private static bool TryQ(Dictionary<string, string>? q, string key, out string v)
    {
        v = string.Empty;
        if (q == null || !q.TryGetValue(key, out var s)) return false;
        v = s.Trim();
        return v.Length > 0;
    }

    /// <summary>详情</summary>
    public async Task<BizOrderItem> GetAsync(long id)
        => await db.Queryable<BizOrderItem>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
           ?? throw new DomainException("订单明细不存在或已删除");

    /// <summary>保存（新增或更新）</summary>
    public async Task<long> SaveAsync(BizOrderItemSaveRequest req)
    {
        BizOrderItem row;
        if (req.id is long id && id > 0)
        {
            row = await db.Queryable<BizOrderItem>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
                ?? throw new DomainException("订单明细不存在或已删除");
        }
        else
        {
            row = new BizOrderItem { Id = snowflake.NextId() };
        }
        row.OrderId = req.OrderId;
        row.ProductName = req.ProductName;
        row.Quantity = req.Quantity;
        row.Price = req.Price;
        row.UpdatedAt = DateTime.Now;
        if (req.id is long uid && uid > 0)
            await db.Updateable(row).ExecuteCommandAsync();
        else
            await db.Insertable(row).ExecuteCommandAsync();
        return row.Id;
    }

    /// <summary>删除（软删）</summary>
    public async Task DeleteAsync(long id)
    {
        var row = await GetAsync(id);
        row.IsDeleted = true;
        row.UpdatedAt = DateTime.Now;
        await db.Updateable(row).ExecuteCommandAsync();
    }

    /// <summary>批量删除（软删，返回实际删除数）</summary>
    public async Task<int> BatchDeleteAsync(List<long> ids)
    {
        if (ids == null || ids.Count == 0) return 0;
        return await db.Updateable<BizOrderItem>()
            .SetColumns(t => t.IsDeleted == true)
            .SetColumns(t => t.UpdatedAt == DateTime.Now)
            .Where(t => ids.Contains(t.Id) && t.IsDeleted == false)
            .ExecuteCommandAsync();
    }
}

/// <summary>订单明细 保存请求</summary>
public class BizOrderItemSaveRequest
{
    /// <summary>Id（更新时传）</summary>
    public long? id { get; set; }
    /// <summary>订单Id</summary>
    public long OrderId { get; set; } = default;
    /// <summary>商品名称</summary>
    public string ProductName { get; set; } = string.Empty;
    /// <summary>数量</summary>
    public int Quantity { get; set; } = default;
    /// <summary>单价</summary>
    public decimal Price { get; set; } = default;
}
