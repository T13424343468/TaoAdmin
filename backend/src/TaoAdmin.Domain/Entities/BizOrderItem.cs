// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件；手改逻辑请写 *AppService.Extensions.cs 扩展文件） ====
using SqlSugar;
using TaoAdmin.Common.Entities;

namespace TaoAdmin.Domain.Entities;

/// <summary>
/// 订单明细（biz_order_item）：由代码生成器生成，手改逻辑请写扩展文件。
/// </summary>
[SugarTable("biz_order_item", TableDescription = "订单明细")]
public class BizOrderItem : BaseEntity
{
    /// <summary>订单Id</summary>
    [SugarColumn(ColumnDescription = "订单Id")]
    public long OrderId { get; set; } = default;

    /// <summary>商品名称</summary>
    [SugarColumn(Length = 100, ColumnDescription = "商品名称")]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>数量</summary>
    [SugarColumn(ColumnDescription = "数量")]
    public int Quantity { get; set; } = default;

    /// <summary>单价</summary>
    [SugarColumn(DecimalDigits = 2, ColumnDescription = "单价")]
    public decimal Price { get; set; } = default;

}
