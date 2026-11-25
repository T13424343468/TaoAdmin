using System.Text.Json;
using SqlSugar;
using TaoAdmin.Common.Entities;

namespace TaoAdmin.Domain.Entities;

/// <summary>
/// 代码生成器：表设计（sys_codegen_table）。
/// 保存可视化表设计器画出的表结构（表名/中文名/字段清单及各字段的类型、控件、查询、校验配置），
/// 供「同步建库」与「一键生成全套增删改查前后端代码」使用。
/// </summary>
[SugarTable("sys_codegen_table", TableDescription = "代码生成器表设计")]
public class SysCodeGenTable : BaseEntity
{
    /// <summary>表名（英文，下划线风格，如 biz_contract）</summary>
    [SugarColumn(Length = 100, ColumnDescription = "表名")]
    public string TableName { get; set; } = string.Empty;

    /// <summary>中文名（业务名，如「合同管理」）</summary>
    [SugarColumn(Length = 100, ColumnDescription = "中文名")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>字段设计清单（JSON：CodegenFieldDesign 数组）</summary>
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true, ColumnDescription = "字段设计JSON")]
    public string? FieldsJson { get; set; }

    /// <summary>备注</summary>
    [SugarColumn(Length = 500, IsNullable = true, ColumnDescription = "备注")]
    public string? Remark { get; set; }

    /// <summary>最近一次同步到数据库的时间（null 表示尚未同步）</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "最近同步时间")]
    public DateTime? LastSyncAt { get; set; }

    /// <summary>主从关系配置（JSON：CodegenMasterDetailConfig；null 表示单表设计）</summary>
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true, ColumnDescription = "主从关系配置JSON")]
    public string? MasterDetailJson { get; set; }

    /// <summary>是否主从表设计（主表视角）</summary>
    [SugarColumn(IsNullable = true, ColumnDescription = "是否主从表主表")]
    public bool IsMasterDetail { get; set; }

    /// <summary>增强配置（JSON：CodegenEnhancementConfig；导入导出等补配，生成页运行时读取，补配即生效）</summary>
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true, ColumnDescription = "增强配置JSON")]
    public string? EnhancementJson { get; set; }

    /// <summary>解析增强配置（损坏或为空时返回 null）</summary>
    public CodegenEnhancementConfig? ParseEnhancement()
    {
        if (string.IsNullOrWhiteSpace(EnhancementJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<CodegenEnhancementConfig>(EnhancementJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析主从关系配置（损坏或为空时返回 null）</summary>
    public CodegenMasterDetailConfig? ParseMasterDetail()
    {
        if (string.IsNullOrWhiteSpace(MasterDetailJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<CodegenMasterDetailConfig>(MasterDetailJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析字段设计（损坏或为空时返回空清单）</summary>
    public List<CodegenFieldDesign> ParseFields()
    {
        if (string.IsNullOrWhiteSpace(FieldsJson)) return new List<CodegenFieldDesign>();
        try
        {
            return JsonSerializer.Deserialize<List<CodegenFieldDesign>>(FieldsJson) ?? new List<CodegenFieldDesign>();
        }
        catch (JsonException)
        {
            return new List<CodegenFieldDesign>();
        }
    }
}

/// <summary>单个字段的设计：物理属性 + 界面行为</summary>
public class CodegenFieldDesign
{
    /// <summary>字段名（英文）</summary>
    public string name { get; set; } = "";
    /// <summary>显示名（中文）</summary>
    public string label { get; set; } = "";
    /// <summary>数据类型：string/int/long/decimal/datetime/bool/text</summary>
    public string dataType { get; set; } = "string";
    /// <summary>长度（string/decimal 用）</summary>
    public int? length { get; set; }
    /// <summary>小数位（decimal 用）</summary>
    public int? precision { get; set; }
    /// <summary>是否可空</summary>
    public bool nullable { get; set; } = true;
    /// <summary>默认值（字符串表示）</summary>
    public string? defaultValue { get; set; }
    /// <summary>是否主键（生成器默认自带雪花 Id，此标记用于业务唯一键等场景预留）</summary>
    public bool isPrimary { get; set; }
    /// <summary>是否唯一索引</summary>
    public bool isUnique { get; set; }
    /// <summary>是否建普通索引</summary>
    public bool isIndex { get; set; }
    /// <summary>列表是否显示</summary>
    public bool showInList { get; set; } = true;
    /// <summary>表单控件：text/number/date/textarea/dict/switch</summary>
    public string control { get; set; } = "text";
    /// <summary>关联字典编码（control=dict 时必填）</summary>
    public string? dictCode { get; set; }
    /// <summary>查询方式：none/eq/like/range</summary>
    public string queryType { get; set; } = "none";
    /// <summary>校验规则：none/required/required+max（逗号分隔）</summary>
    public string? rules { get; set; }
}

/// <summary>主从表关系配置（主表视角）：一层主从，主表挂多从表</summary>
public class CodegenMasterDetailConfig
{
    /// <summary>从表配置清单</summary>
    public List<CodegenDetailTableConfig> details { get; set; } = new();
}

/// <summary>单个从表配置</summary>
public class CodegenDetailTableConfig
{
    /// <summary>从表设计（现场画新表或反向读已有表，保存时从表也各存一条 SysCodeGenTable）</summary>
    public long detailTableId { get; set; }
    /// <summary>外键字段名（从表上指向主表 Id 的字段，如 orderId；同步建库时自动建索引）</summary>
    public string foreignKey { get; set; } = "";
    /// <summary>从表是否生成独立全套功能（独立菜单/页面）；false 时仅作为主表详情的行编辑子表</summary>
    public bool standalone { get; set; }
    /// <summary>从表数据来源：free=自由录入，dict=从字典挑（dictCode 指定字典）</summary>
    public string dataSource { get; set; } = "free";
    /// <summary>字典编码（dataSource=dict 时必填）</summary>
    public string? dictCode { get; set; }
}

/// <summary>增强配置（⑲增强配置入口：给已生成模块补导入导出等，不动已生成标准代码，补配即生效）。
/// 字典关联与查询配置直接落在字段设计（CodegenFieldDesign.dictCode/queryType）上，运行时随配置接口下发。</summary>
public class CodegenEnhancementConfig
{
    /// <summary>是否启用 Excel 导入（生成页出现导入按钮，后端带导入接口）</summary>
    public bool importEnabled { get; set; }
    /// <summary>是否启用 Excel 导出（生成页出现导出按钮，后端带导出接口）</summary>
    public bool exportEnabled { get; set; }
}
