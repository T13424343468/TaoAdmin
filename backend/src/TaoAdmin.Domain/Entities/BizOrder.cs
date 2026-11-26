// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件；手改逻辑请写 *AppService.Extensions.cs 扩展文件） ====
using SqlSugar;
using TaoAdmin.Common.Entities;

namespace TaoAdmin.Domain.Entities;

/// <summary>
/// 订单主表（biz_order）：由代码生成器生成，手改逻辑请写扩展文件。
/// </summary>
[SugarTable("biz_order", TableDescription = "订单主表")]
public class BizOrder : BaseEntity
{
    /// <summary>订单号</summary>
    [SugarColumn(Length = 50, ColumnDescription = "订单号")]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单金额</summary>
    [SugarColumn(DecimalDigits = 2, ColumnDescription = "订单金额")]
    public decimal OrderAmount { get; set; } = default;

}
