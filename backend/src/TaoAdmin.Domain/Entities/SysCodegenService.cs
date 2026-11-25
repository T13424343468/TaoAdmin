using SqlSugar;
using TaoAdmin.Common.Entities;

namespace TaoAdmin.Domain.Entities;

/// <summary>
/// 代码生成器：服务登记（sys_codegen_service）。
/// 登记后端服务清单（内置两个 + 一键生成新服务时自动登记），
/// 供「生成器可选目标服务」下拉与生成新服务的端口自动分配使用；新服务默认共用数据库各管各表。
/// </summary>
[SugarTable("sys_codegen_service", TableDescription = "代码生成器服务登记")]
public class SysCodegenService : BaseEntity
{
    /// <summary>服务工程名（如 TaoAdmin.OrderService）</summary>
    [SugarColumn(Length = 100, ColumnDescription = "服务工程名")]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>短名（如 Order，用于命名空间 TaoAdmin.OrderService.Controllers）</summary>
    [SugarColumn(Length = 50, ColumnDescription = "服务短名")]
    public string ShortName { get; set; } = string.Empty;

    /// <summary>网关路由前缀（如 order，对应 /api/order/**）</summary>
    [SugarColumn(Length = 50, ColumnDescription = "网关路由前缀")]
    public string RoutePrefix { get; set; } = string.Empty;

    /// <summary>服务端口（自动分配：内置 5001/5002，新服务从 5003 起递增）</summary>
    [SugarColumn(ColumnDescription = "服务端口")]
    public int Port { get; set; }

    /// <summary>中文名（如「订单服务」）</summary>
    [SugarColumn(Length = 100, ColumnDescription = "中文名")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否内置服务（内置不可删除）</summary>
    [SugarColumn(ColumnDescription = "是否内置服务")]
    public bool IsBuiltIn { get; set; }
}
