// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件；手改逻辑请写 *AppService.Extensions.cs 扩展文件） ====
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using SqlSugar;

namespace TaoAdmin.Application.Services;

/// <summary>订单主表 应用服务（生成区）：分页/详情/增/改/删/批量删。业务个性逻辑写 BizOrderAppService.Extensions.cs。</summary>
public partial class BizOrderAppService(ISqlSugarClient db, SnowflakeIdGenerator snowflake)
{
    /// <summary>分页查询（keyword 走 like 字段；query 走设计字段的 eq/range 过滤）</summary>
    public async Task<PageResult<BizOrder>> PageAsync(int pageIndex, int pageSize, string? keyword, Dictionary<string, string>? query = null)
    {
        RefAsync<int> total = 0;
        var queryable = db.Queryable<BizOrder>()
            .Where(t => t.IsDeleted == false);
        if (TryQ(query, "order_no", out var q_order_no)) queryable = queryable.Where(t => t.OrderNo == q_order_no);
        if (TryQ(query, "order_amount_from", out var qf_order_amount) && decimal.TryParse(qf_order_amount, out var qfv_order_amount)) queryable = queryable.Where(t => t.OrderAmount >= qfv_order_amount);
        if (TryQ(query, "order_amount_to", out var qt_order_amount) && decimal.TryParse(qt_order_amount, out var qtv_order_amount)) queryable = queryable.Where(t => t.OrderAmount <= qtv_order_amount);
        var list = await queryable
            .OrderByDescending(t => t.Id)
            .ToPageListAsync(pageIndex, pageSize, total);
        return new PageResult<BizOrder> { Items = list, Total = total, PageIndex = pageIndex, PageSize = pageSize };
    }

    private static bool TryQ(Dictionary<string, string>? q, string key, out string v)
    {
        v = string.Empty;
        if (q == null || !q.TryGetValue(key, out var s)) return false;
        v = s.Trim();
        return v.Length > 0;
    }

    /// <summary>详情（含从表行清单）</summary>
    public async Task<BizOrderWithDetails> GetAsync(long id)
    {
        var master = await db.Queryable<BizOrder>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
           ?? throw new DomainException("订单主表不存在或已删除");
        var result = new BizOrderWithDetails { master = master };
        result.details0 = await db.Queryable<BizOrderItem>().Where(t => t.OrderId == id && t.IsDeleted == false).OrderBy(t => t.Id).ToListAsync();
        return result;
    }

    /// <summary>保存（新增或更新）</summary>
    public async Task<long> SaveAsync(BizOrderSaveRequest req)
    {
        BizOrder row;
        if (req.id is long id && id > 0)
        {
            row = await db.Queryable<BizOrder>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
                ?? throw new DomainException("订单主表不存在或已删除");
        }
        else
        {
            row = new BizOrder { Id = snowflake.NextId() };
        }
        row.OrderNo = req.OrderNo;
        row.OrderAmount = req.OrderAmount;
        var tran = await db.Ado.UseTranAsync(async () =>
        {
            row.UpdatedAt = DateTime.Now;
            if (req.id is long uid && uid > 0)
                await db.Updateable(row).ExecuteCommandAsync();
            else
                await db.Insertable(row).ExecuteCommandAsync();
            await ReplaceDetails0Async(row.Id, req.details0);
        });
        if (!tran.IsSuccess) throw new DomainException($"保存失败：{tran.ErrorMessage}");
        return row.Id;
    }

    /// <summary>删除（软删；从表行连带软删，同一事务）</summary>
    public async Task DeleteAsync(long id)
    {
        var row = await db.Queryable<BizOrder>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
           ?? throw new DomainException("订单主表不存在或已删除");
        var tran = await db.Ado.UseTranAsync(async () =>
        {
            row.IsDeleted = true;
            row.UpdatedAt = DateTime.Now;
            await db.Updateable(row).ExecuteCommandAsync();
            await db.Updateable<BizOrderItem>().SetColumns(t => t.IsDeleted == true).SetColumns(t => t.UpdatedAt == DateTime.Now)
                .Where(t => t.OrderId == id && t.IsDeleted == false).ExecuteCommandAsync();
        });
        if (!tran.IsSuccess) throw new DomainException($"删除失败：{tran.ErrorMessage}");
    }

    /// <summary>批量删除（软删，返回实际删除数）</summary>
    public async Task<int> BatchDeleteAsync(List<long> ids)
    {
        if (ids == null || ids.Count == 0) return 0;
        return await db.Updateable<BizOrder>()
            .SetColumns(t => t.IsDeleted == true)
            .SetColumns(t => t.UpdatedAt == DateTime.Now)
            .Where(t => ids.Contains(t.Id) && t.IsDeleted == false)
            .ExecuteCommandAsync();
    }

    /// <summary>替换从表「订单明细」行：传来的行保留/更新，未传的旧行软删（外键强制指向主行）</summary>
    private async Task ReplaceDetails0Async(long masterId, List<BizOrderItemSaveRequest>? rows)
    {
        var keepIds = rows?.Where(r => r.id is long k && k > 0).Select(r => r.id!.Value).ToList() ?? new List<long>();
        await db.Updateable<BizOrderItem>().SetColumns(t => t.IsDeleted == true).SetColumns(t => t.UpdatedAt == DateTime.Now)
            .Where(t => t.OrderId == masterId && t.IsDeleted == false && !keepIds.Contains(t.Id)).ExecuteCommandAsync();
        if (rows == null) return;
        foreach (var r in rows)
        {
            BizOrderItem drow;
            if (r.id is long did && did > 0)
            {
                drow = await db.Queryable<BizOrderItem>().FirstAsync(t => t.Id == did && t.IsDeleted == false)
                    ?? throw new DomainException("订单明细行不存在或已删除");
            }
            else
            {
                drow = new BizOrderItem { Id = snowflake.NextId() };
            }
            drow.ProductName = r.ProductName;
            drow.Quantity = r.Quantity;
            drow.Price = r.Price;
            drow.OrderId = masterId;
            drow.UpdatedAt = DateTime.Now;
            if (r.id is long udid && udid > 0)
                await db.Updateable(drow).ExecuteCommandAsync();
            else
                await db.Insertable(drow).ExecuteCommandAsync();
        }
    }
}

/// <summary>订单主表 保存请求</summary>
public class BizOrderSaveRequest
{
    /// <summary>Id（更新时传）</summary>
    public long? id { get; set; }
    /// <summary>订单号</summary>
    public string OrderNo { get; set; } = string.Empty;
    /// <summary>订单金额</summary>
    public decimal OrderAmount { get; set; } = default;
    /// <summary>从表行清单：订单明细（整单一个事务保存；未传的旧行视为删除）</summary>
    public List<BizOrderItemSaveRequest>? details0 { get; set; }
}

/// <summary>订单主表 详情（主行 + 各从表行清单）</summary>
public class BizOrderWithDetails
{
    public BizOrder master { get; set; } = null!;
    public List<BizOrderItem> details0 { get; set; } = new();
}
