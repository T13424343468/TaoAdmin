using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaoAdmin.Common.Http;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using SqlSugar;

namespace TaoAdmin.Application.Services;

/// <summary>表设计保存请求</summary>
public class CodegenDesignSaveRequest
{
    /// <summary>设计 Id（新建传空）</summary>
    public long? id { get; set; }
    /// <summary>表名（英文下划线风格）</summary>
    public string tableName { get; set; } = "";
    /// <summary>中文名</summary>
    public string displayName { get; set; } = "";
    /// <summary>字段设计清单</summary>
    public List<CodegenFieldDesign> fields { get; set; } = new();
    /// <summary>备注</summary>
    public string? remark { get; set; }
    /// <summary>主从关系配置（主表视角；null 表示单表设计）</summary>
    public CodegenMasterDetailConfig? masterDetail { get; set; }
    /// <summary>增强配置（导入导出等补配；null 表示清空）</summary>
    public CodegenEnhancementConfig? enhancement { get; set; }
}

/// <summary>同步结果：执行了什么、跳过了什么、待确认的破坏性语句</summary>
public class CodegenSyncResult
{
    /// <summary>是否执行了变更</summary>
    public bool executed { get; set; }
    /// <summary>执行说明（建表/加列清单）</summary>
    public List<string> actions { get; set; } = new();
    /// <summary>破坏性变更语句（删列/改类型）：仅返回不执行，经确认后另行执行</summary>
    public List<string> destructivePending { get; set; } = new();
}

/// <summary>生成模块运行时配置（生成页挂载时拉取，按配置渲染；补配即生效）</summary>
public class CodegenModuleConfig
{
    public string id { get; set; } = "";
    public string tableName { get; set; } = "";
    public string displayName { get; set; } = "";
    public List<CodegenFieldDesign> fields { get; set; } = new();
    public CodegenEnhancementConfig enhancement { get; set; } = new();
    /// <summary>主从配置：主表生成页按此渲染从表行编辑区（从表字段同样配置驱动，补配即生效）</summary>
    public List<CodegenDetailModuleConfig> details { get; set; } = new();
}

/// <summary>主从模块运行时配置中的单个从表（主表生成页行编辑区按此渲染）</summary>
public class CodegenDetailModuleConfig
{
    public string tableName { get; set; } = "";
    public string displayName { get; set; } = "";
    /// <summary>从表外键字段名（指向主表 Id）</summary>
    public string foreignKey { get; set; } = "";
    /// <summary>是否生成了独立全套功能</summary>
    public bool standalone { get; set; }
    /// <summary>数据来源：free=自由录入，dict=从字典挑</summary>
    public string dataSource { get; set; } = "free";
    public string? dictCode { get; set; }
    public List<CodegenFieldDesign> fields { get; set; } = new();
}

/// <summary>生成结果：文件清单 + 记录 Id</summary>
public class CodegenGenerateResult
{
    public long recordId { get; set; }
    public string tableName { get; set; } = "";
    /// <summary>生成出的文件（相对路径 → 内容，内容带统一标记头）</summary>
    public List<CodegenFile> files { get; set; } = new();
    public long elapsedMs { get; set; }
}

/// <summary>生成出的单个文件</summary>
public class CodegenFile
{
    public string path { get; set; } = "";
    public string content { get; set; } = "";
}

/// <summary>
/// 代码生成器应用服务（⑲）：
/// 表设计维护 → 同步建库（加列自动同步；删列/改类型等破坏性变更仅生成语句经确认后执行）
/// → 一键生成全套增删改查前后端代码（实体/服务/接口/权限登记 + 前端接口定义/列表页/表单弹窗）。
/// 生成文件带统一标记头，重复生成只覆盖生成区；手改逻辑写扩展文件（部分类）不被冲掉。
/// </summary>
public class CodeGenAppService(ISqlSugarClient db, SnowflakeIdGenerator snowflake)
{
    /// <summary>生成文件统一标记头：标识该文件为生成区，重复生成会被覆盖</summary>
    public const string MarkerHeader = "// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件；手改逻辑请写 *AppService.Extensions.cs 扩展文件） ====";

    private static readonly string[] AllowedTypes = { "string", "int", "long", "decimal", "datetime", "bool", "text" };
    private static readonly string[] AllowedControls = { "text", "number", "date", "textarea", "dict", "switch" };
    private static readonly string[] AllowedQueries = { "none", "eq", "like", "range" };

    // ==================== 设计维护 ====================

    /// <summary>保存表设计（新建或更新）</summary>
    public async Task<long> SaveDesignAsync(CodegenDesignSaveRequest req)
    {
        ValidateDesign(req);
        SysCodeGenTable row;
        if (req.id is long id && id > 0)
        {
            row = await db.Queryable<SysCodeGenTable>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
                ?? throw new DomainException("表设计不存在或已删除");
        }
        else
        {
            var dup = await db.Queryable<SysCodeGenTable>().AnyAsync(t => t.TableName == req.tableName && t.IsDeleted == false);
            if (dup) throw new DomainException($"表名 {req.tableName} 的设计已存在，请直接编辑原设计");
            row = new SysCodeGenTable { Id = snowflake.NextId() };
        }
        row.TableName = req.tableName.Trim();
        row.DisplayName = req.displayName.Trim();
        row.FieldsJson = JsonSerializer.Serialize(req.fields);
        row.Remark = req.remark;
        row.EnhancementJson = req.enhancement == null ? null : JsonSerializer.Serialize(req.enhancement);
        row.UpdatedAt = DateTime.Now;
        await SaveMasterDetailAsync(row, req.masterDetail);
        if (row.Id != 0 && await db.Queryable<SysCodeGenTable>().AnyAsync(t => t.Id == row.Id))
            await db.Updateable(row).ExecuteCommandAsync();
        else
            await db.Insertable(row).ExecuteCommandAsync();
        return row.Id;
    }

    /// <summary>
    /// 保存主从关系配置：主表存 IsMasterDetail + MasterDetailJson；校验从表设计存在、外键字段在从表字段清单中、从表未挂到其他主表。
    /// masterDetail 为 null 时清空主从标记（退回单表设计）。
    /// </summary>
    private async Task SaveMasterDetailAsync(SysCodeGenTable masterRow, CodegenMasterDetailConfig? config)
    {
        if (config == null || config.details.Count == 0)
        {
            masterRow.IsMasterDetail = false;
            masterRow.MasterDetailJson = null;
            return;
        }
        var seenTables = new HashSet<long>();
        foreach (var d in config.details)
        {
            if (d.detailTableId <= 0) throw new DomainException("主从配置缺少从表设计，请先在从表页签画好并保存");
            if (string.IsNullOrWhiteSpace(d.foreignKey)) throw new DomainException($"从表关联缺少外键字段名");
            if (!seenTables.Add(d.detailTableId)) throw new DomainException("同一条主从配置里从表重复");
            var detail = await db.Queryable<SysCodeGenTable>().FirstAsync(t => t.Id == d.detailTableId && t.IsDeleted == false)
                ?? throw new DomainException($"从表设计 {d.detailTableId} 不存在或已删除");
            if (detail.Id == masterRow.Id) throw new DomainException("从表不能是主表自己");
            var detailFields = detail.ParseFields();
            if (!detailFields.Any(f => f.name.Equals(d.foreignKey.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new DomainException($"从表 {detail.TableName} 里没有外键字段 {d.foreignKey}，请先在从表设计里加上");
            // 外键字段自动补索引标记
            var fk = detailFields.First(f => f.name.Equals(d.foreignKey.Trim(), StringComparison.OrdinalIgnoreCase));
            fk.isIndex = true;
            detail.FieldsJson = JsonSerializer.Serialize(detailFields);
            detail.UpdatedAt = DateTime.Now;
            await db.Updateable(detail).ExecuteCommandAsync();
        }
        masterRow.IsMasterDetail = true;
        masterRow.MasterDetailJson = JsonSerializer.Serialize(config);
    }

    /// <summary>设计分页</summary>
    public async Task<PageResult<SysCodeGenTable>> PageAsync(int pageIndex, int pageSize, string? keyword)
    {
        RefAsync<int> total = 0;
        var list = await db.Queryable<SysCodeGenTable>()
            .Where(t => t.IsDeleted == false)
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), t => t.TableName.Contains(keyword!) || t.DisplayName.Contains(keyword!))
            .OrderByDescending(t => t.UpdatedAt)
            .ToPageListAsync(pageIndex, pageSize, total);
        return new PageResult<SysCodeGenTable> { Items = list, Total = total, PageIndex = pageIndex, PageSize = pageSize };
    }

    /// <summary>设计详情</summary>
    public async Task<SysCodeGenTable> GetAsync(long id)
        => await db.Queryable<SysCodeGenTable>().FirstAsync(t => t.Id == id && t.IsDeleted == false)
           ?? throw new DomainException("表设计不存在或已删除");

    /// <summary>
    /// 生成模块运行时配置（⑲增强配置「补配即生效」的读取口）：
    /// 生成页挂载时拉取字段清单（含字典关联/查询配置/控件）与增强配置，按配置渲染，补配后无需重新生成。
    /// </summary>
    public async Task<CodegenModuleConfig> GetConfigAsync(long id)
    {
        var design = await GetAsync(id);
        var config = new CodegenModuleConfig
        {
            id = design.Id.ToString(),
            tableName = design.TableName,
            displayName = design.DisplayName,
            fields = design.ParseFields(),
            enhancement = design.ParseEnhancement() ?? new CodegenEnhancementConfig(),
        };
        // 主从表：从表字段清单随配置下发，主表生成页行编辑区按配置渲染（补配即生效）
        var md = design.IsMasterDetail ? design.ParseMasterDetail() : null;
        if (md != null)
        {
            foreach (var d in md.details)
            {
                var dd = await GetAsync(d.detailTableId);
                config.details.Add(new CodegenDetailModuleConfig
                {
                    tableName = dd.TableName,
                    displayName = dd.DisplayName,
                    foreignKey = d.foreignKey,
                    standalone = d.standalone,
                    dataSource = d.dataSource,
                    dictCode = d.dictCode,
                    fields = dd.ParseFields(),
                });
            }
        }
        return config;
    }

    /// <summary>删除设计（软删，不动已生成的数据库表与代码）</summary>
    public async Task DeleteAsync(long id)
    {
        var row = await GetAsync(id);
        row.IsDeleted = true;
        row.UpdatedAt = DateTime.Now;
        await db.Updateable(row).ExecuteCommandAsync();
    }

    // ==================== 同步建库 ====================

    /// <summary>
    /// 同步到数据库：表不存在则建表；已存在则只自动加缺失列（加字段自动同步）。
    /// 删列、改类型等破坏性变更不自动执行——生成语句放进 destructivePending，经确认后另行执行。
    /// </summary>
    public async Task<CodegenSyncResult> SyncToDatabaseAsync(long tableId)
    {
        var design = await GetAsync(tableId);
        var fields = design.ParseFields();
        if (fields.Count == 0) throw new DomainException("字段清单为空，请先在设计器里画字段");
        var result = new CodegenSyncResult();
        var table = design.TableName;

        if (!await TableExistsAsync(table))
        {
            var createSql = BuildCreateTableSql(table, fields);
            await db.Ado.ExecuteCommandAsync(createSql);
            result.executed = true;
            result.actions.Add($"已建表 {table}（{fields.Count} 个业务字段）");
        }
        else
        {
            var existing = await GetExistingColumnsAsync(table);
            foreach (var f in fields)
            {
                if (existing.Contains(f.name.ToLowerInvariant())) continue;
                var colSql = BuildColumnSql(f, sqliteAddColumn: IsSqliteDialect);
                await db.Ado.ExecuteCommandAsync($"ALTER TABLE `{table}` ADD COLUMN {colSql};");
                result.executed = true;
                result.actions.Add($"已加列 {f.name}（{f.dataType}）");
            }
            // 破坏性变更：库里有、设计里没有的列 → 只生成语句不执行
            var designNames = fields.Select(f => f.name.ToLowerInvariant()).ToHashSet();
            foreach (var col in existing)
            {
                if (designNames.Contains(col) || col == "id" || col == "created_at" || col == "updated_at" || col == "is_deleted") continue;
                result.destructivePending.Add($"-- 删除列 {col}（设计里已移除，确认后执行）：\nALTER TABLE `{table}` DROP COLUMN `{col}`;");
            }
        }

        design.LastSyncAt = DateTime.Now;
        design.UpdatedAt = DateTime.Now;
        await db.Updateable(design).ExecuteCommandAsync();

        // 主从表：主表同步后级联同步各从表（外键字段已在保存时补索引标记）
        if (design.IsMasterDetail)
        {
            var md = design.ParseMasterDetail();
            if (md != null)
            {
                foreach (var d in md.details)
                {
                    var detailResult = await SyncToDatabaseAsync(d.detailTableId);
                    result.actions.AddRange(detailResult.actions.Select(a => $"[从表] {a}"));
                    result.destructivePending.AddRange(detailResult.destructivePending.Select(s => $"-- 从表侧待确认：\n{s}"));
                }
            }
        }
        return result;
    }

    /// <summary>执行经确认的破坏性变更语句（仅允许单条 ALTER TABLE）</summary>
    public async Task ExecuteDestructiveAsync(string sql)
    {
        var trimmed = sql.Trim().TrimEnd(';');
        if (!trimmed.StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("仅允许执行 ALTER TABLE 语句");
        if (trimmed.Contains(';'))
            throw new DomainException("一次只允许执行一条语句");
        await db.Ado.ExecuteCommandAsync(trimmed + ";");
    }

    // ==================== 一键生成 ====================

    /// <summary>一键生成全套增删改查前后端代码，落生成记录。
    /// 主从表：主表生成嵌从表行编辑的全套（整单一个事务保存、删主带删明细、详情抽屉）；
    /// 从表始终生成后端全套（主表保存契约依赖从表 SaveRequest），standalone=true 时另生成独立页面与菜单。</summary>
    public async Task<CodegenGenerateResult> GenerateAsync(long tableId, string mode = "generate")
    {
        var sw = Stopwatch.StartNew();
        var design = await GetAsync(tableId);
        var fields = design.ParseFields();
        if (fields.Count == 0) throw new DomainException("字段清单为空，请先在设计器里画字段");

        var files = new List<CodegenFile>();
        var md = design.IsMasterDetail ? design.ParseMasterDetail() : null;
        if (md != null && md.details.Count > 0)
        {
            // ---------- 主从表：主表 + 各从表 ----------
            var entityName = ToPascal(design.TableName);
            var camel = ToCamel(entityName);
            // 从表设计一次性载入（保存校验已保证存在且不重复）
            var detailPairs = new List<(CodegenDetailTableConfig Cfg, SysCodeGenTable Design, List<CodegenFieldDesign> Fields, string Entity, string Camel)>();
            foreach (var d in md.details)
            {
                var dd = await GetAsync(d.detailTableId);
                var dfields = dd.ParseFields();
                if (dfields.Count == 0) throw new DomainException($"从表 {dd.DisplayName} 字段清单为空，请先在设计器里画字段");
                var dEntity = ToPascal(dd.TableName);
                detailPairs.Add((d, dd, dfields, dEntity, ToCamel(dEntity)));
            }

            // 主表：实体（标准）+ 服务（嵌从表事务保存）+ 扩展桩 + 控制器（详情带从表）+ 前端接口 + 页面（行编辑+详情抽屉）+ 菜单权限
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Domain/Entities/{entityName}.cs", content = GenEntity(design, fields) });
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Application/Services/{entityName}AppService.cs", content = GenAppService(design, fields, entityName, detailPairs) });
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Application/Services/{entityName}AppService.Extensions.cs", content = GenExtensionStub(entityName) });
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.IdentityService/Controllers/{entityName}Controller.cs", content = GenController(design, fields, entityName, withDetails: true) });
            files.Add(new CodegenFile { path = $"frontend/src/api/{camel}.ts", content = GenFrontendApi(design, fields, entityName, camel) });
            files.Add(new CodegenFile { path = $"frontend/src/views/{camel}/index.vue", content = GenFrontendPage(design, fields, entityName, camel, detailPairs) });
            files.Add(new CodegenFile { path = $"generated/{design.TableName}/menu-permission-init.sql", content = GenMenuPermissionSql(design, entityName) });

            // 从表：后端全套始终生成（主表保存契约引用从表 SaveRequest）；独立功能另出页面与菜单
            foreach (var (cfg, ddesign, dfields, dEntity, dCamel) in detailPairs)
            {
                files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Domain/Entities/{dEntity}.cs", content = GenEntity(ddesign, dfields) });
                files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Application/Services/{dEntity}AppService.cs", content = GenAppService(ddesign, dfields, dEntity) });
                files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Application/Services/{dEntity}AppService.Extensions.cs", content = GenExtensionStub(dEntity) });
                files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.IdentityService/Controllers/{dEntity}Controller.cs", content = GenController(ddesign, dfields, dEntity) });
                files.Add(new CodegenFile { path = $"frontend/src/api/{dCamel}.ts", content = GenFrontendApi(ddesign, dfields, dEntity, dCamel) });
                if (cfg.standalone)
                {
                    files.Add(new CodegenFile { path = $"frontend/src/views/{dCamel}/index.vue", content = GenFrontendPage(ddesign, dfields, dEntity, dCamel) });
                    files.Add(new CodegenFile { path = $"generated/{ddesign.TableName}/menu-permission-init.sql", content = GenMenuPermissionSql(ddesign, dEntity) });
                }
            }
        }
        else
        {
            // ---------- 单表（原口径不变） ----------
            var entityName = ToPascal(design.TableName);
            var camel = ToCamel(entityName);
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Domain/Entities/{entityName}.cs", content = GenEntity(design, fields) });
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Application/Services/{entityName}AppService.cs", content = GenAppService(design, fields, entityName) });
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.Application/Services/{entityName}AppService.Extensions.cs", content = GenExtensionStub(entityName) });
            files.Add(new CodegenFile { path = $"backend/src/TaoAdmin.IdentityService/Controllers/{entityName}Controller.cs", content = GenController(design, fields, entityName) });
            files.Add(new CodegenFile { path = $"frontend/src/api/{camel}.ts", content = GenFrontendApi(design, fields, entityName, camel) });
            files.Add(new CodegenFile { path = $"frontend/src/views/{camel}/index.vue", content = GenFrontendPage(design, fields, entityName, camel) });
            files.Add(new CodegenFile
            {
                path = $"generated/{design.TableName}/menu-permission-init.sql",
                content = GenMenuPermissionSql(design, entityName)
            });
        }

        sw.Stop();
        var record = new SysCodeGenRecord
        {
            Id = snowflake.NextId(),
            TableId = tableId,
            TableName = design.TableName,
            Mode = mode == "regenerate" ? "regenerate" : "generate",
            FilesJson = JsonSerializer.Serialize(files),
            FileCount = files.Count,
            ElapsedMs = sw.ElapsedMilliseconds,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        await db.Insertable(record).ExecuteCommandAsync();

        return new CodegenGenerateResult { recordId = record.Id, tableName = design.TableName, files = files, elapsedMs = sw.ElapsedMilliseconds };
    }

    /// <summary>生成记录分页</summary>
    public async Task<PageResult<SysCodeGenRecord>> PageRecordsAsync(int pageIndex, int pageSize, string? keyword)
    {
        RefAsync<int> total = 0;
        var list = await db.Queryable<SysCodeGenRecord>()
            .Where(r => r.IsDeleted == false)
            .WhereIF(!string.IsNullOrWhiteSpace(keyword), r => r.TableName.Contains(keyword!))
            .OrderByDescending(r => r.CreatedAt)
            .ToPageListAsync(pageIndex, pageSize, total);
        return new PageResult<SysCodeGenRecord> { Items = list, Total = total, PageIndex = pageIndex, PageSize = pageSize };
    }

    /// <summary>生成记录详情（含全部文件内容）</summary>
    public async Task<(SysCodeGenRecord Record, List<CodegenFile> Files)> GetRecordAsync(long id)
    {
        var record = await db.Queryable<SysCodeGenRecord>().FirstAsync(r => r.Id == id && r.IsDeleted == false)
            ?? throw new DomainException("生成记录不存在");
        var files = string.IsNullOrWhiteSpace(record.FilesJson)
            ? new List<CodegenFile>()
            : JsonSerializer.Deserialize<List<CodegenFile>>(record.FilesJson) ?? new List<CodegenFile>();
        return (record, files);
    }

    // ==================== 校验 ====================

    private static void ValidateDesign(CodegenDesignSaveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.tableName)) throw new DomainException("表名不能为空");
        if (!Regex.IsMatch(req.tableName.Trim(), @"^[a-z][a-z0-9_]{2,98}$"))
            throw new DomainException("表名须为小写字母开头的下划线风格（3-99 位，仅小写字母/数字/下划线）");
        if (string.IsNullOrWhiteSpace(req.displayName)) throw new DomainException("中文名不能为空");
        if (req.fields.Count == 0) throw new DomainException("至少要有一个业务字段");
        var names = new HashSet<string>();
        foreach (var f in req.fields)
        {
            if (string.IsNullOrWhiteSpace(f.name) || !Regex.IsMatch(f.name.Trim(), @"^[a-z][a-z0-9_]{1,63}$"))
                throw new DomainException($"字段名 {f.name} 不合法（小写字母开头的下划线风格）");
            if (!names.Add(f.name.Trim())) throw new DomainException($"字段名 {f.name} 重复");
            if (!AllowedTypes.Contains(f.dataType)) throw new DomainException($"字段 {f.name} 的类型 {f.dataType} 不支持");
            if (!AllowedControls.Contains(f.control)) throw new DomainException($"字段 {f.name} 的控件 {f.control} 不支持");
            if (!AllowedQueries.Contains(f.queryType)) throw new DomainException($"字段 {f.name} 的查询方式 {f.queryType} 不支持");
            if (f.control == "dict" && string.IsNullOrWhiteSpace(f.dictCode))
                throw new DomainException($"字段 {f.name} 选了字典控件，必须填关联字典编码");
            if (f.dataType == "string" && (f.length is null or < 1 or > 4000))
                throw new DomainException($"字段 {f.name} 的长度须在 1-4000 之间");
            if (f.dataType == "decimal" && (f.precision is null or < 0 or > 10))
                throw new DomainException($"字段 {f.name} 的小数位须在 0-10 之间");
        }
    }

    // ==================== DDL 构建 ====================

    /// <summary>方言判断缝隙：SQLite 走 sqlite_master/PRAGMA，其余走 information_schema（测试可覆写模拟 MySQL 方言）</summary>
    protected virtual bool IsSqliteDialect => db.CurrentConnectionConfig.DbType == SqlSugar.DbType.Sqlite;

    private async Task<bool> TableExistsAsync(string table)
    {
        // 统一走 SqlSugar Ado 通道：生产 IsAutoCloseConnection=true 时裸连接是关闭的，
        // 直接拿 Connection 强转执行会报连接错误（单机挂账根因），由 Ado 负责开关连接
        if (IsSqliteDialect)
        {
            var n = await db.Ado.SqlQueryAsync<int>($"SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name = '{table.Replace("'", "''")}'");
            return n.FirstOrDefault() > 0;
        }
        var count = await db.Ado.SqlQueryAsync<int>($"SELECT COUNT(1) FROM information_schema.tables WHERE table_name = '{table.Replace("'", "''")}'");
        return count.FirstOrDefault() > 0;
    }

    private async Task<HashSet<string>> GetExistingColumnsAsync(string table)
    {
        var set = new HashSet<string>();
        if (IsSqliteDialect)
        {
            // pragma_table_info 表值函数（SQLite 3.16+），同样走 Ado 通道避免裸连接关闭问题
            var cols = await db.Ado.SqlQueryAsync<string>($"SELECT LOWER(name) FROM pragma_table_info('{table.Replace("'", "''")}')");
            foreach (var c in cols) set.Add(c);
        }
        else
        {
            var cols = await db.Ado.SqlQueryAsync<string>($"SELECT LOWER(column_name) FROM information_schema.columns WHERE table_name = '{table.Replace("'", "''")}'");
            foreach (var c in cols) set.Add(c);
        }
        return set;
    }

    private static string BuildCreateTableSql(string table, List<CodegenFieldDesign> fields)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE `").Append(table).Append("` (\n");
        sb.AppendLine("  `id` bigint NOT NULL PRIMARY KEY,");
        foreach (var f in fields) sb.Append("  ").Append(BuildColumnSql(f)).Append(",\n");
        sb.AppendLine("  `created_at` datetime NOT NULL,");
        sb.AppendLine("  `updated_at` datetime NOT NULL,");
        sb.AppendLine("  `is_deleted` boolean NOT NULL DEFAULT 0");
        sb.AppendLine(");");
        foreach (var f in fields.Where(f => f.isIndex || f.isUnique))
            sb.AppendLine($"CREATE {(f.isUnique ? "UNIQUE " : "")}INDEX `idx_{table}_{f.name}` ON `{table}` (`{f.name}`);");
        return sb.ToString();
    }

    /// <param name="sqliteAddColumn">SQLite ALTER TABLE ADD COLUMN 场景：NOT NULL 且无默认值的列必须补默认值（SQLite 不允许隐式 NULL 默认）</param>
    private static string BuildColumnSql(CodegenFieldDesign f, bool sqliteAddColumn = false)
    {
        var type = f.dataType switch
        {
            "string" => f.length is int l ? $"nvarchar({l})" : "nvarchar(200)",
            "text" => "TEXT",
            "int" => "int",
            "long" => "bigint",
            "decimal" => $"decimal(18,{f.precision ?? 2})",
            "datetime" => "datetime",
            "bool" => "boolean",
            _ => "nvarchar(200)"
        };
        var nullable = f.nullable ? " NULL" : " NOT NULL";
        var def = string.IsNullOrWhiteSpace(f.defaultValue) ? "" : $" DEFAULT {FormatDefault(f)}";
        if (sqliteAddColumn && !f.nullable && def == "")
            def = $" DEFAULT {SqliteAddColumnDefault(f.dataType)}";
        return $"`{f.name}` {type}{nullable}{def}";
    }

    /// <summary>SQLite 加列场景的类型化兜底默认值（ADD COLUMN 不允许 CURRENT_TIMESTAMP 等表达式默认）</summary>
    private static string SqliteAddColumnDefault(string dataType) => dataType switch
    {
        "int" or "long" or "decimal" or "bool" => "0",
        "datetime" => "'1970-01-01 00:00:00'",
        _ => "''"
    };

    private static string FormatDefault(CodegenFieldDesign f) => f.dataType switch
    {
        "int" or "long" or "decimal" => f.defaultValue!.Trim(),
        "bool" => f.defaultValue!.Trim() == "true" || f.defaultValue!.Trim() == "1" ? "1" : "0",
        _ => $"'{f.defaultValue!.Replace("'", "''")}'"
    };

    // ==================== 代码模板 ====================

    private static string ToPascal(string name)
        => string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    private static string ToCamel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    private static string CsType(CodegenFieldDesign f) => f.dataType switch
    {
        "int" => "int",
        "long" => "long",
        "decimal" => "decimal",
        "datetime" => "DateTime",
        "bool" => "bool",
        _ => "string"
    };

    private static string NullableCs(CodegenFieldDesign f)
        => f.nullable ? CsType(f) + "?" : CsType(f);

    private static string GenEntity(SysCodeGenTable design, List<CodegenFieldDesign> fields)
    {
        var entityName = ToPascal(design.TableName);
        var sb = new StringBuilder();
        sb.AppendLine(MarkerHeader);
        sb.AppendLine("using SqlSugar;");
        sb.AppendLine("using TaoAdmin.Common.Entities;");
        sb.AppendLine();
        sb.AppendLine("namespace TaoAdmin.Domain.Entities;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// {design.DisplayName}（{design.TableName}）：由代码生成器生成，手改逻辑请写扩展文件。");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"[SugarTable(\"{design.TableName}\", TableDescription = \"{design.DisplayName}\")]");
        sb.AppendLine($"public class {entityName} : BaseEntity");
        sb.AppendLine("{");
        foreach (var f in fields)
        {
            var colAttr = f.dataType switch
            {
                "string" => $"[SugarColumn(Length = {f.length ?? 200}, {(f.nullable ? "IsNullable = true, " : "")}ColumnDescription = \"{f.label}\")]",
                "text" => $"[SugarColumn(ColumnDataType = \"TEXT\", IsNullable = true, ColumnDescription = \"{f.label}\")]",
                "decimal" => $"[SugarColumn(DecimalDigits = {f.precision ?? 2}, {(f.nullable ? "IsNullable = true, " : "")}ColumnDescription = \"{f.label}\")]",
                _ => $"[SugarColumn({(f.nullable ? "IsNullable = true, " : "")}ColumnDescription = \"{f.label}\")]"
            };
            sb.AppendLine($"    /// <summary>{f.label}</summary>");
            sb.AppendLine($"    {colAttr}");
            sb.AppendLine($"    public {NullableCs(f)} {ToPascal(f.name)} {{ get; set; }}{(f.nullable ? "" : (f.dataType == "string" || f.dataType == "text" ? " = string.Empty;" : " = default;"))}");
            sb.AppendLine();
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenAppService(SysCodeGenTable design, List<CodegenFieldDesign> fields, string entityName,
        List<(CodegenDetailTableConfig Cfg, SysCodeGenTable Design, List<CodegenFieldDesign> Fields, string Entity, string Camel)>? masterDetails = null)
    {
        var camel = ToCamel(entityName);
        var sb = new StringBuilder();
        sb.AppendLine(MarkerHeader);
        sb.AppendLine("using TaoAdmin.Common.Http;");
        sb.AppendLine("using TaoAdmin.Common.Snowflake;");
        sb.AppendLine("using TaoAdmin.Domain;");
        sb.AppendLine("using TaoAdmin.Domain.Entities;");
        sb.AppendLine("using SqlSugar;");
        var enhHeader = design.ParseEnhancement();
        if (enhHeader != null && (enhHeader.importEnabled || enhHeader.exportEnabled))
            sb.AppendLine("using MiniExcelLibs;");
        sb.AppendLine();
        sb.AppendLine("namespace TaoAdmin.Application.Services;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>{design.DisplayName} 应用服务（生成区）：分页/详情/增/改/删/批量删。业务个性逻辑写 {entityName}AppService.Extensions.cs。</summary>");
        sb.AppendLine($"public partial class {entityName}AppService(ISqlSugarClient db, SnowflakeIdGenerator snowflake)");
        sb.AppendLine("{");
        // 分页
        sb.AppendLine($"    /// <summary>分页查询</summary>");
        sb.AppendLine($"    public async Task<PageResult<{entityName}>> PageAsync(int pageIndex, int pageSize, string? keyword)");
        sb.AppendLine("    {");
        sb.AppendLine("        RefAsync<int> total = 0;");
        var likeField = fields.FirstOrDefault(f => f.queryType == "like");
        sb.AppendLine($"        var list = await db.Queryable<{entityName}>()");
        sb.AppendLine("            .Where(t => t.IsDeleted == false)");
        if (likeField != null)
            sb.AppendLine($"            .WhereIF(!string.IsNullOrWhiteSpace(keyword), t => t.{ToPascal(likeField.name)}.Contains(keyword!))");
        sb.AppendLine("            .OrderByDescending(t => t.Id)");
        sb.AppendLine("            .ToPageListAsync(pageIndex, pageSize, total);");
        sb.AppendLine("        return new PageResult<" + entityName + "> { Items = list, Total = total, PageIndex = pageIndex, PageSize = pageSize };");
        sb.AppendLine("    }");
        sb.AppendLine();
        // 详情（主从表返回主行 + 各从表行清单）
        if (masterDetails != null)
        {
            sb.AppendLine($"    /// <summary>详情（含从表行清单）</summary>");
            sb.AppendLine($"    public async Task<{entityName}WithDetails> GetAsync(long id)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var master = await db.Queryable<{entityName}>().FirstAsync(t => t.Id == id && t.IsDeleted == false)");
            sb.AppendLine($"           ?? throw new DomainException(\"{design.DisplayName}不存在或已删除\");");
            sb.AppendLine($"        var result = new {entityName}WithDetails {{ master = master }};");
            for (var i = 0; i < masterDetails.Count; i++)
            {
                var dEntity = masterDetails[i].Entity;
                var fkPascal = ToPascal(masterDetails[i].Cfg.foreignKey);
                sb.AppendLine($"        result.details{i} = await db.Queryable<{dEntity}>().Where(t => t.{fkPascal} == id && t.IsDeleted == false).OrderBy(t => t.Id).ToListAsync();");
            }
            sb.AppendLine("        return result;");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine($"    /// <summary>详情</summary>");
            sb.AppendLine($"    public async Task<{entityName}> GetAsync(long id)");
            sb.AppendLine($"        => await db.Queryable<{entityName}>().FirstAsync(t => t.Id == id && t.IsDeleted == false)");
            sb.AppendLine($"           ?? throw new DomainException(\"{design.DisplayName}不存在或已删除\");");
        }
        sb.AppendLine();
        // 保存（增/改）。保存请求类在方法体后统一以命名空间级类型输出（嵌在服务类内会导致控制器/主表无法引用）
        var reqProps = string.Join("\n", fields.Select(f => $"    /// <summary>{f.label}</summary>\n    public {NullableCs(f)} {ToPascal(f.name)} {{ get; set; }}{(f.nullable ? "" : (f.dataType == "string" || f.dataType == "text" ? " = string.Empty;" : " = default;"))}"));
        var detailProps = new StringBuilder();
        if (masterDetails != null)
        {
            for (var i = 0; i < masterDetails.Count; i++)
            {
                detailProps.AppendLine($"    /// <summary>从表行清单：{masterDetails[i].Design.DisplayName}（整单一个事务保存；未传的旧行视为删除）</summary>");
                detailProps.AppendLine($"    public List<{masterDetails[i].Entity}SaveRequest>? details{i} {{ get; set; }}");
            }
        }
        sb.AppendLine($"    /// <summary>保存（新增或更新）</summary>");
        if (masterDetails != null)
        {
            // 主从表：主行 + 各从表行整单一个事务（任一步失败整体回滚）
            sb.AppendLine($"    public async Task<long> SaveAsync({entityName}SaveRequest req)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {entityName} row;");
            sb.AppendLine("        if (req.id is long id && id > 0)");
            sb.AppendLine("        {");
            sb.AppendLine($"            row = await db.Queryable<{entityName}>().FirstAsync(t => t.Id == id && t.IsDeleted == false)");
            sb.AppendLine($"                ?? throw new DomainException(\"{design.DisplayName}不存在或已删除\");");
            sb.AppendLine("        }");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            sb.AppendLine($"            row = new {entityName} {{ Id = snowflake.NextId() }};");
            sb.AppendLine("        }");
            foreach (var f in fields)
                sb.AppendLine($"        row.{ToPascal(f.name)} = req.{ToPascal(f.name)};");
            sb.AppendLine("        var tran = await db.Ado.UseTranAsync(async () =>");
            sb.AppendLine("        {");
            sb.AppendLine("            row.UpdatedAt = DateTime.Now;");
            sb.AppendLine("            if (req.id is long uid && uid > 0)");
            sb.AppendLine("                await db.Updateable(row).ExecuteCommandAsync();");
            sb.AppendLine("            else");
            sb.AppendLine("                await db.Insertable(row).ExecuteCommandAsync();");
            for (var i = 0; i < masterDetails.Count; i++)
                sb.AppendLine($"            await ReplaceDetails{i}Async(row.Id, req.details{i});");
            sb.AppendLine("        });");
            sb.AppendLine("        if (!tran.IsSuccess) throw new DomainException($\"保存失败：{tran.ErrorMessage}\");");
            sb.AppendLine("        return row.Id;");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine($"    public async Task<long> SaveAsync({entityName}SaveRequest req)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {entityName} row;");
            sb.AppendLine("        if (req.id is long id && id > 0)");
            sb.AppendLine("        {");
            sb.AppendLine($"            row = await db.Queryable<{entityName}>().FirstAsync(t => t.Id == id && t.IsDeleted == false)");
            sb.AppendLine($"                ?? throw new DomainException(\"{design.DisplayName}不存在或已删除\");");
            sb.AppendLine("        }");
            sb.AppendLine("        else");
            sb.AppendLine("        {");
            sb.AppendLine($"            row = new {entityName} {{ Id = snowflake.NextId() }};");
            sb.AppendLine("        }");
            foreach (var f in fields)
                sb.AppendLine($"        row.{ToPascal(f.name)} = req.{ToPascal(f.name)};");
            sb.AppendLine("        row.UpdatedAt = DateTime.Now;");
            sb.AppendLine("        if (req.id is long uid && uid > 0)");
            sb.AppendLine("            await db.Updateable(row).ExecuteCommandAsync();");
            sb.AppendLine("        else");
            sb.AppendLine("            await db.Insertable(row).ExecuteCommandAsync();");
            sb.AppendLine("        return row.Id;");
            sb.AppendLine("    }");
        }
        sb.AppendLine();
        // 删除（主从表：删主带删明细，同一事务）
        if (masterDetails != null)
        {
            sb.AppendLine($"    /// <summary>删除（软删；从表行连带软删，同一事务）</summary>");
            sb.AppendLine($"    public async Task DeleteAsync(long id)");
            sb.AppendLine("    {");
            sb.AppendLine("        var row = await db.Queryable<" + entityName + ">().FirstAsync(t => t.Id == id && t.IsDeleted == false)");
            sb.AppendLine($"           ?? throw new DomainException(\"{design.DisplayName}不存在或已删除\");");
            sb.AppendLine("        var tran = await db.Ado.UseTranAsync(async () =>");
            sb.AppendLine("        {");
            sb.AppendLine("            row.IsDeleted = true;");
            sb.AppendLine("            row.UpdatedAt = DateTime.Now;");
            sb.AppendLine("            await db.Updateable(row).ExecuteCommandAsync();");
            for (var i = 0; i < masterDetails.Count; i++)
            {
                var fkPascal = ToPascal(masterDetails[i].Cfg.foreignKey);
                var dEntity = masterDetails[i].Entity;
                sb.AppendLine($"            await db.Updateable<{dEntity}>().SetColumns(t => t.IsDeleted == true).SetColumns(t => t.UpdatedAt == DateTime.Now)");
                sb.AppendLine($"                .Where(t => t.{fkPascal} == id && t.IsDeleted == false).ExecuteCommandAsync();");
            }
            sb.AppendLine("        });");
            sb.AppendLine("        if (!tran.IsSuccess) throw new DomainException($\"删除失败：{tran.ErrorMessage}\");");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine($"    /// <summary>删除（软删）</summary>");
            sb.AppendLine($"    public async Task DeleteAsync(long id)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var row = await GetAsync(id);");
            sb.AppendLine("        row.IsDeleted = true;");
            sb.AppendLine("        row.UpdatedAt = DateTime.Now;");
            sb.AppendLine("        await db.Updateable(row).ExecuteCommandAsync();");
            sb.AppendLine("    }");
        }
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>批量删除（软删，返回实际删除数）</summary>");
        sb.AppendLine($"    public async Task<int> BatchDeleteAsync(List<long> ids)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (ids == null || ids.Count == 0) return 0;");
        sb.AppendLine($"        return await db.Updateable<{entityName}>()");
        sb.AppendLine("            .SetColumns(t => t.IsDeleted == true)");
        sb.AppendLine("            .SetColumns(t => t.UpdatedAt == DateTime.Now)");
        sb.AppendLine("            .Where(t => ids.Contains(t.Id) && t.IsDeleted == false)");
        sb.AppendLine("            .ExecuteCommandAsync();");
        sb.AppendLine("    }");
        // 增强配置：导入导出（补配后重新生成才带接口；生成页运行时读配置决定按钮显隐）
        var enh = design.ParseEnhancement();
        if (enh != null && (enh.importEnabled || enh.exportEnabled))
            sb.Append(GenExcelSupport(design, fields, entityName));
        // 主从表：从表行替换（整单保存口径：传来的行保留/更新，未传的旧行软删）+ 详情 DTO
        if (masterDetails != null)
        {
            for (var i = 0; i < masterDetails.Count; i++)
            {
                var (cfg, ddesign, dfields, dEntity, _) = masterDetails[i];
                var fkPascal = ToPascal(cfg.foreignKey);
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>替换从表「{ddesign.DisplayName}」行：传来的行保留/更新，未传的旧行软删（外键强制指向主行）</summary>");
                sb.AppendLine($"    private async Task ReplaceDetails{i}Async(long masterId, List<{dEntity}SaveRequest>? rows)");
                sb.AppendLine("    {");
                sb.AppendLine("        var keepIds = rows?.Where(r => r.id is long k && k > 0).Select(r => r.id!.Value).ToList() ?? new List<long>();");
                sb.AppendLine($"        await db.Updateable<{dEntity}>().SetColumns(t => t.IsDeleted == true).SetColumns(t => t.UpdatedAt == DateTime.Now)");
                sb.AppendLine($"            .Where(t => t.{fkPascal} == masterId && t.IsDeleted == false && !keepIds.Contains(t.Id)).ExecuteCommandAsync();");
                sb.AppendLine("        if (rows == null) return;");
                sb.AppendLine($"        foreach (var r in rows)");
                sb.AppendLine("        {");
                sb.AppendLine($"            {dEntity} drow;");
                sb.AppendLine("            if (r.id is long did && did > 0)");
                sb.AppendLine("            {");
                sb.AppendLine($"                drow = await db.Queryable<{dEntity}>().FirstAsync(t => t.Id == did && t.IsDeleted == false)");
                sb.AppendLine($"                    ?? throw new DomainException(\"{ddesign.DisplayName}行不存在或已删除\");");
                sb.AppendLine("            }");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
                sb.AppendLine($"                drow = new {dEntity} {{ Id = snowflake.NextId() }};");
                sb.AppendLine("            }");
                foreach (var f in dfields)
                    sb.AppendLine($"            drow.{ToPascal(f.name)} = r.{ToPascal(f.name)};");
                sb.AppendLine($"            drow.{fkPascal} = masterId;");
                sb.AppendLine("            drow.UpdatedAt = DateTime.Now;");
                sb.AppendLine("            if (r.id is long udid && udid > 0)");
                sb.AppendLine("                await db.Updateable(drow).ExecuteCommandAsync();");
                sb.AppendLine("            else");
                sb.AppendLine("                await db.Insertable(drow).ExecuteCommandAsync();");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
            }
        }
        sb.AppendLine("}");
        // 命名空间级类型：保存请求 + 主从详情 DTO（必须在服务类外，控制器与主表保存契约才能引用）
        sb.AppendLine();
        sb.AppendLine($"/// <summary>{design.DisplayName} 保存请求</summary>");
        sb.AppendLine($"public class {entityName}SaveRequest");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Id（更新时传）</summary>");
        sb.AppendLine("    public long? id { get; set; }");
        sb.AppendLine(reqProps);
        if (masterDetails != null)
            sb.Append(detailProps);
        sb.AppendLine("}");
        if (masterDetails != null)
        {
            sb.AppendLine();
            sb.AppendLine($"/// <summary>{design.DisplayName} 详情（主行 + 各从表行清单）</summary>");
            sb.AppendLine($"public class {entityName}WithDetails");
            sb.AppendLine("{");
            sb.AppendLine($"    public {entityName} master {{ get; set; }} = null!;");
            for (var i = 0; i < masterDetails.Count; i++)
                sb.AppendLine($"    public List<{masterDetails[i].Entity}> details{i} {{ get; set; }} = new();");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    /// <summary>生成导入导出支撑代码（MiniExcel，列头=字段显示名，逐行校验错误明细不中断）</summary>
    private static string GenExcelSupport(SysCodeGenTable design, List<CodegenFieldDesign> fields, string entityName)
    {
        var enh = design.ParseEnhancement()!;
        var sb = new StringBuilder();
        var likeField = fields.FirstOrDefault(f => f.queryType == "like");
        var binders = string.Join(",\n", fields.Select(f =>
            $"            (\"{f.label}\", \"{f.name}\", \"{f.dataType}\", {(!f.nullable).ToString().ToLower()}, {(f.dataType == "string" && f.length is int l ? l.ToString() : "null")})"));
        var exportRows = string.Join("\n", fields.Select(f =>
        {
            var p = ToPascal(f.name);
            return f.dataType switch
            {
                "bool" => $"            [\"{f.label}\"] = t.{p} ? \"是\" : \"否\",",
                "datetime" => f.nullable
                    ? $"            [\"{f.label}\"] = t.{p} == null ? \"\" : ((DateTime)t.{p}).ToString(\"yyyy-MM-dd HH:mm:ss\"),"
                    : $"            [\"{f.label}\"] = t.{p}.ToString(\"yyyy-MM-dd HH:mm:ss\"),",
                "string" or "text" => f.nullable
                    ? $"            [\"{f.label}\"] = t.{p} ?? \"\","
                    : $"            [\"{f.label}\"] = t.{p},",
                _ => $"            [\"{f.label}\"] = t.{p}.ToString(),"
            };
        }));
        var sampleRow = string.Join("\n", fields.Select(f =>
        {
            var sample = f.dataType switch
            {
                "int" or "long" => "0",
                "decimal" => "0",
                "datetime" => "2026-01-01 00:00:00",
                "bool" => "是",
                _ => "示例"
            };
            return $"            [\"{f.label}\"] = \"{sample}\",";
        }));

        sb.AppendLine();
        sb.AppendLine("    // ---------- 导入导出（增强配置启用时生成；列头=字段显示名） ----------");
        if (enh.importEnabled)
        {
            sb.AppendLine("    private static readonly (string Col, string Name, string DataType, bool Required, int? MaxLen)[] ImportBinders =");
            sb.AppendLine("        new (string, string, string, bool, int?)[] {");
            sb.AppendLine(binders);
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>导入模板（表头 + 一行示例）</summary>");
            sb.AppendLine("    public async Task<byte[]> BuildTemplateAsync()");
            sb.AppendLine("    {");
            sb.AppendLine("        var sample = new Dictionary<string, object>");
            sb.AppendLine("        {");
            sb.AppendLine(sampleRow);
            sb.AppendLine("        };");
            sb.AppendLine("        using var ms = new MemoryStream();");
            sb.AppendLine($"        await MiniExcel.SaveAsAsync(ms, new List<Dictionary<string, object>> {{ sample }}, sheetName: \"{design.DisplayName}导入模板\");");
            sb.AppendLine("        return ms.ToArray();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>导入：逐行校验（必填/类型/长度），错误行给明细不中断</summary>");
            sb.AppendLine($"    public async Task<{entityName}ImportResultDto> ImportAsync(Stream stream)");
            sb.AppendLine("    {");
            sb.AppendLine("        var result = new " + entityName + "ImportResultDto();");
            sb.AppendLine("        List<IDictionary<string, object?>> rows;");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            rows = MiniExcel.Query(stream, true).Cast<IDictionary<string, object?>>().ToList();");
            sb.AppendLine("        }");
            sb.AppendLine("        catch (Exception)");
            sb.AppendLine("        {");
            sb.AppendLine("            throw new DomainException(\"表格解析失败：请使用系统提供的导入模板填写\", 400);");
            sb.AppendLine("        }");
            sb.AppendLine("        string Cell(IDictionary<string, object?> row, string col) => row.TryGetValue(col, out var v) && v != null ? v.ToString()!.Trim() : \"\";");
            sb.AppendLine("        var valid = new List<" + entityName + ">();");
            sb.AppendLine("        for (int i = 0; i < rows.Count; i++)");
            sb.AppendLine("        {");
            sb.AppendLine("            var row = rows[i];");
            sb.AppendLine("            int rowNo = i + 1;");
            sb.AppendLine("            if (row.All(c => string.IsNullOrWhiteSpace(c.Value?.ToString()))) continue;");
            sb.AppendLine("            result.total++;");
            sb.AppendLine("            var bound = new Dictionary<string, object?>();");
            sb.AppendLine("            string? err = null;");
            sb.AppendLine("            foreach (var b in ImportBinders)");
            sb.AppendLine("            {");
            sb.AppendLine("                var text = Cell(row, b.Col);");
            sb.AppendLine("                var cerr = ConvertImportCell(text, b.DataType, out var v);");
            sb.AppendLine("                if (cerr == null && b.Required && text.Length == 0) cerr = $\"{b.Label}不能为空\";");
            sb.AppendLine("                if (cerr == null && b.MaxLen is int ml && text.Length > ml) cerr = $\"{b.Label}超过{ml}个字符\";");
            sb.AppendLine("                if (cerr != null)");
            sb.AppendLine("                {");
            sb.AppendLine("                    result.errors.Add(new " + entityName + "ImportErrorDto { row = rowNo, reason = $\"{b.Label}{cerr}\" });");
            sb.AppendLine("                    err = \"stop\";");
            sb.AppendLine("                    break;");
            sb.AppendLine("                }");
            sb.AppendLine("                bound[b.Name] = v;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (err != null) continue;");
            sb.AppendLine("            var item = new " + entityName + "();");
            sb.AppendLine("            BindImportProps(item, bound);");
            sb.AppendLine("            valid.Add(item);");
            sb.AppendLine("        }");
            sb.AppendLine("        foreach (var item in valid)");
            sb.AppendLine("        {");
            sb.AppendLine("            item.Id = snowflake.NextId();");
            sb.AppendLine("            item.CreatedAt = DateTime.Now;");
            sb.AppendLine("            item.UpdatedAt = DateTime.Now;");
            sb.AppendLine("            item.IsDeleted = false;");
            sb.AppendLine("            await db.Insertable(item).ExecuteCommandAsync();");
            sb.AppendLine("            result.success++;");
            sb.AppendLine("        }");
            sb.AppendLine("        return result;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private static string? ConvertImportCell(string text, string dataType, out object? value)");
            sb.AppendLine("    {");
            sb.AppendLine("        value = text;");
            sb.AppendLine("        switch (dataType)");
            sb.AppendLine("        {");
            sb.AppendLine("            case \"int\": if (text.Length == 0) { value = 0; return null; } if (int.TryParse(text, out var i)) { value = i; return null; } return \"须为整数\";");
            sb.AppendLine("            case \"long\": if (text.Length == 0) { value = 0L; return null; } if (long.TryParse(text, out var l)) { value = l; return null; } return \"须为整数\";");
            sb.AppendLine("            case \"decimal\": if (text.Length == 0) { value = 0m; return null; } if (decimal.TryParse(text, out var d)) { value = d; return null; } return \"须为数字\";");
            sb.AppendLine("            case \"bool\":");
            sb.AppendLine("                if (text is \"是\" or \"1\" or \"true\") { value = true; return null; }");
            sb.AppendLine("                if (text is \"\" or \"否\" or \"0\" or \"false\") { value = false; return null; }");
            sb.AppendLine("                return \"须为 是/否\";");
            sb.AppendLine("            case \"datetime\": if (text.Length == 0) { value = null; return null; } if (DateTime.TryParse(text, out var dt)) { value = dt; return null; } return \"日期格式无效\";");
            sb.AppendLine("            default: value = text; return null;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    private static void BindImportProps({entityName} item, Dictionary<string, object?> bound)");
            sb.AppendLine("    {");
            foreach (var f in fields)
            {
                var cast = f.dataType switch
                {
                    "int" => "int)",
                    "long" => "long)",
                    "decimal" => "decimal)",
                    "bool" => "bool)",
                    "datetime" => "DateTime?)",
                    _ => "string?)"
                };
                sb.AppendLine($"        if (bound.TryGetValue(\"{f.name}\", out var v{ToPascal(f.name)})) item.{ToPascal(f.name)} = ({cast})v{ToPascal(f.name)};");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"/// <summary>{design.DisplayName} 导入结果</summary>");
            sb.AppendLine($"public class {entityName}ImportResultDto");
            sb.AppendLine("{");
            sb.AppendLine("    public int total { get; set; }");
            sb.AppendLine("    public int success { get; set; }");
            sb.AppendLine($"    public List<{entityName}ImportErrorDto> errors {{ get; set; }} = new();");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"public class {entityName}ImportErrorDto");
            sb.AppendLine("{");
            sb.AppendLine("    public int row { get; set; }");
            sb.AppendLine("    public string reason { get; set; } = \"\";");
            sb.AppendLine("}");
        }
        if (enh.exportEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("    /// <summary>导出（xlsx 字节）</summary>");
            sb.AppendLine("    public async Task<byte[]> ExportAsync(string? keyword = null)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var list = await db.Queryable<{entityName}>().Where(t => t.IsDeleted == false)");
            if (likeField != null)
                sb.AppendLine($"            .WhereIF(!string.IsNullOrWhiteSpace(keyword), t => t.{ToPascal(likeField.name)}.Contains(keyword!))");
            sb.AppendLine("            .OrderByDescending(t => t.Id).ToListAsync();");
            sb.AppendLine("        var rows = new List<Dictionary<string, object>>();");
            sb.AppendLine("        foreach (var t in list)");
            sb.AppendLine("        {");
            sb.AppendLine("            rows.Add(new Dictionary<string, object>");
            sb.AppendLine("            {");
            sb.AppendLine(exportRows);
            sb.AppendLine("            });");
            sb.AppendLine("        }");
            sb.AppendLine("        using var ms = new MemoryStream();");
            sb.AppendLine($"        await MiniExcel.SaveAsAsync(ms, rows, sheetName: \"{design.DisplayName}\");");
            sb.AppendLine("        return ms.ToArray();");
            sb.AppendLine("    }");
        }
        return sb.ToString();
    }

    private static string GenExtensionStub(string entityName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ==== CODEGEN EXTENSION FILE（手写扩展区：本文件不会被重新生成覆盖，业务个性逻辑写在这里） ====");
        sb.AppendLine("using SqlSugar;");
        sb.AppendLine("using TaoAdmin.Common.Snowflake;");
        sb.AppendLine();
        sb.AppendLine("namespace TaoAdmin.Application.Services;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>{entityName} 手写扩展区：跨表报表、复杂流程、特殊查询等个性逻辑放这里。</summary>");
        sb.AppendLine($"public partial class {entityName}AppService");
        sb.AppendLine("{");
        sb.AppendLine($"    // 示例：public async Task<object> MyBusinessQueryAsync(...) {{ ... }}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenController(SysCodeGenTable design, List<CodegenFieldDesign> fields, string entityName, bool withDetails = false)
    {
        var perm = $"gen:{design.TableName}:manage";
        var sb = new StringBuilder();
        sb.AppendLine(MarkerHeader);
        sb.AppendLine("using TaoAdmin.Application.Services;");
        sb.AppendLine("using TaoAdmin.Common.Auth;");
        sb.AppendLine("using TaoAdmin.Common.Http;");
        sb.AppendLine("using TaoAdmin.Common.Web;");
        sb.AppendLine("using TaoAdmin.Domain.Entities;");
        sb.AppendLine("using Microsoft.AspNetCore.Authorization;");
        sb.AppendLine("using Microsoft.AspNetCore.Mvc;");
        sb.AppendLine();
        sb.AppendLine("namespace TaoAdmin.IdentityService.Controllers;");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>{design.DisplayName}管理（代码生成器生成）</summary>");
        sb.AppendLine($"[Route(\"{design.TableName}\")]");
        sb.AppendLine($"public class {entityName}Controller : BaseApiController");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly {entityName}AppService _svc;");
        sb.AppendLine($"    public {entityName}Controller({entityName}AppService svc) => _svc = svc;");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>分页查询</summary>");
        sb.AppendLine($"    [HttpGet]");
        sb.AppendLine($"    [Permission(\"{perm}\")]");
        sb.AppendLine($"    public async Task<ApiResult<PageResult<{entityName}>>> Page([FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 10, [FromQuery] string? keyword = null)");
        sb.AppendLine($"        => ApiOk(await _svc.PageAsync(pageIndex, pageSize, keyword));");
        sb.AppendLine();
        if (withDetails)
        {
            sb.AppendLine($"    /// <summary>详情（含从表行清单）</summary>");
            sb.AppendLine($"    [HttpGet(\"{{id}}\")]");
            sb.AppendLine($"    [Permission(\"{perm}\")]");
            sb.AppendLine($"    public async Task<ApiResult<{entityName}WithDetails>> Get(string id)");
            sb.AppendLine($"        => ApiOk(await _svc.GetAsync(long.Parse(id)));");
        }
        else
        {
            sb.AppendLine($"    /// <summary>详情</summary>");
            sb.AppendLine($"    [HttpGet(\"{{id}}\")]");
            sb.AppendLine($"    [Permission(\"{perm}\")]");
            sb.AppendLine($"    public async Task<ApiResult<{entityName}>> Get(string id)");
            sb.AppendLine($"        => ApiOk(await _svc.GetAsync(long.Parse(id)));");
        }
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>保存（新增或更新）</summary>");
        sb.AppendLine($"    [HttpPost]");
        sb.AppendLine($"    [Permission(\"{perm}\")]");
        sb.AppendLine($"    public async Task<ApiResult<object>> Save([FromBody] {entityName}SaveRequest req)");
        sb.AppendLine("    {");
        sb.AppendLine("        var id = await _svc.SaveAsync(req);");
        sb.AppendLine("        return ApiOk(id > 0 ? id.ToString() : \"保存成功\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>删除（软删）</summary>");
        sb.AppendLine($"    [HttpDelete(\"{{id}}\")]");
        sb.AppendLine($"    [Permission(\"{perm}\")]");
        sb.AppendLine($"    public async Task<ApiResult<object>> Delete(string id)");
        sb.AppendLine("    {");
        sb.AppendLine("        await _svc.DeleteAsync(long.Parse(id));");
        sb.AppendLine($"        return ApiOk(\"删除成功\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>批量删除（软删，返回实际删除数）</summary>");
        sb.AppendLine($"    [HttpPost(\"batch-delete\")]");
        sb.AppendLine($"    [Permission(\"{perm}\")]");
        sb.AppendLine($"    public async Task<ApiResult<object>> BatchDelete([FromBody] BatchIdsRequest req)");
        sb.AppendLine($"        => ApiOk($\"已删除 {{await _svc.BatchDeleteAsync(req.Ids)}} 条\");");
        var enhCtl = design.ParseEnhancement();
        if (enhCtl != null && (enhCtl.importEnabled || enhCtl.exportEnabled))
        {
            if (enhCtl.exportEnabled)
            {
                sb.AppendLine();
                sb.AppendLine("    /// <summary>导出（xlsx）</summary>");
                sb.AppendLine("    [HttpGet(\"export\")]");
                sb.AppendLine($"    [Permission(\"{perm}\")]");
                sb.AppendLine("    public async Task<IActionResult> Export([FromQuery] string? keyword = null)");
                sb.AppendLine("    {");
                sb.AppendLine("        var bytes = await _svc.ExportAsync(keyword);");
                sb.AppendLine($"        return File(bytes, \"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\", $\"{design.DisplayName}.xlsx\");");
                sb.AppendLine("    }");
            }
            if (enhCtl.importEnabled)
            {
                sb.AppendLine();
                sb.AppendLine("    /// <summary>下载导入模板（xlsx）</summary>");
                sb.AppendLine("    [HttpGet(\"import-template\")]");
                sb.AppendLine($"    [Permission(\"{perm}\")]");
                sb.AppendLine("    public async Task<IActionResult> ImportTemplate()");
                sb.AppendLine("    {");
                sb.AppendLine("        var bytes = await _svc.BuildTemplateAsync();");
                sb.AppendLine($"        return File(bytes, \"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\", $\"{design.DisplayName}导入模板.xlsx\");");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine("    /// <summary>导入（xlsx；逐行校验，错误行给明细不中断）</summary>");
                sb.AppendLine("    [HttpPost(\"import\")]");
                sb.AppendLine($"    [Permission(\"{perm}\")]");
                sb.AppendLine("    public async Task<ApiResult<object>> Import(IFormFile file)");
                sb.AppendLine("    {");
                sb.AppendLine("        if (file == null || file.Length == 0) throw new DomainException(\"请选择要导入的文件\", 400);");
                sb.AppendLine("        using var ms = new MemoryStream();");
                sb.AppendLine("        await file.CopyToAsync(ms);");
                sb.AppendLine("        ms.Position = 0;");
                sb.AppendLine("        var result = await _svc.ImportAsync(ms);");
                sb.AppendLine("        return ApiOk<object>(result);");
                sb.AppendLine("    }");
            }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenFrontendApi(SysCodeGenTable design, List<CodegenFieldDesign> fields, string entityName, string camel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件） ====");
        sb.AppendLine($"// {design.DisplayName} 接口定义");
        sb.AppendLine("import { request } from './request';");
        sb.AppendLine();
        sb.AppendLine($"export interface {entityName}Item {{");
        sb.AppendLine("  id: string;");
        foreach (var f in fields)
            sb.AppendLine($"  {f.name}: {TsType(f)};");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"export interface {entityName}Save {{");
        sb.AppendLine("  id?: string;");
        foreach (var f in fields)
            sb.AppendLine($"  {f.name}: {TsType(f)};");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 分页查询 */");
        sb.AppendLine($"export function page{entityName}(params: {{ pageIndex?: number; pageSize?: number; keyword?: string }}): Promise<{{ total: number; items: {entityName}Item[] }}> {{");
        sb.AppendLine($"  return request({{ url: '/identity/{design.TableName}', method: 'get', params }});");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 详情 */");
        sb.AppendLine($"export function get{entityName}(id: string): Promise<{entityName}Item> {{");
        sb.AppendLine($"  return request({{ url: `/identity/{design.TableName}/${{id}}`, method: 'get' }});");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 保存（新增或更新） */");
        sb.AppendLine($"export function save{entityName}(data: {entityName}Save): Promise<void> {{");
        sb.AppendLine($"  return request({{ url: '/identity/{design.TableName}', method: 'post', data: {{ ...data, id: data.id ? Number(data.id) : undefined }} }});");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 删除（单条） */");
        sb.AppendLine($"export function delete{entityName}(id: string): Promise<void> {{");
        sb.AppendLine($"  return request({{ url: `/identity/{design.TableName}/${{id}}`, method: 'delete' }});");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/** 批量删除 */");
        sb.AppendLine($"export function batchDelete{entityName}(ids: string[]): Promise<{{ deleted: number }}> {{");
        sb.AppendLine($"  return request({{ url: '/identity/{design.TableName}/batch-delete', method: 'post', data: {{ ids: ids.map(Number) }} }});");
        sb.AppendLine("}");
        var enhApi = design.ParseEnhancement();
        var hasExcel = enhApi != null && (enhApi.importEnabled || enhApi.exportEnabled);
        // 运行时配置读取（增强配置补配即生效）：生成页挂载时拉取，按配置渲染
        sb.AppendLine();
        sb.AppendLine("/** 运行时配置（字段/字典/查询/导入导出按配置渲染，补配后无需重新生成） */");
        sb.AppendLine($"export function get{entityName}Config(): Promise<any> {{");
        sb.AppendLine($"  return request({{ url: '/identity/codegen/designs/{design.Id}/config', method: 'get' }});");
        sb.AppendLine("}");
        if (hasExcel)
        {
            sb.AppendLine();
            sb.AppendLine("import service from './request';");
            sb.AppendLine("import { message, Modal } from 'ant-design-vue';");
            sb.AppendLine("import { h } from 'vue';");
            sb.AppendLine();
            if (enhApi!.exportEnabled)
            {
                sb.AppendLine("/** 导出（xlsx 下载） */");
                sb.AppendLine($"export async function export{entityName}(keyword?: string): Promise<void> {{");
                sb.AppendLine("  const res = await service.get(" + $"'/identity/{design.TableName}/export'" + ", {");
                sb.AppendLine("    params: keyword ? { keyword } : undefined,");
                sb.AppendLine("    responseType: 'blob',");
                sb.AppendLine("  });");
                sb.AppendLine($"  saveBlob(res.data as Blob, readFilename(res.headers, '{design.DisplayName}.xlsx'));");
                sb.AppendLine("}");
            }
            if (enhApi.importEnabled)
            {
                sb.AppendLine("/** 下载导入模板（xlsx） */");
                sb.AppendLine($"export async function download{entityName}Template(): Promise<void> {{");
                sb.AppendLine("  const res = await service.get(" + $"'/identity/{design.TableName}/import-template'" + ", { responseType: 'blob' });");
                sb.AppendLine($"  saveBlob(res.data as Blob, readFilename(res.headers, '{design.DisplayName}导入模板.xlsx'));");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine("/** 导入（xlsx 上传；返回逐行校验错误明细） */");
                sb.AppendLine($"export function import{entityName}(file: File): Promise<{{ total: number; success: number; errors: {{ row: number; reason: string }}[] }}> {{");
                sb.AppendLine("  const form = new FormData();");
                sb.AppendLine("  form.append('file', file);");
                sb.AppendLine($"  return service.post('/identity/{design.TableName}/import', form) as any;");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine("/** 导入结果提示（有错误行时给明细，不中断） */");
                sb.AppendLine($"export function notifyImportResult(res: {{ total: number; success: number; errors: {{ row: number; reason: string }}[] }}): void {{");
                sb.AppendLine("  if (res.errors && res.errors.length > 0) {");
                sb.AppendLine("    Modal.error({");
                sb.AppendLine("      title: `导入完成：成功 ${res.success}/${res.total} 条`,");
                sb.AppendLine("      content: h(");
                sb.AppendLine("        'div',");
                sb.AppendLine("        {},");
                sb.AppendLine("        res.errors.slice(0, 10).map((e) => h('p', `第 ${e.row} 行：${e.reason}`))");
                sb.AppendLine("      ),");
                sb.AppendLine("    });");
                sb.AppendLine("  } else {");
                sb.AppendLine("    message.success(`导入成功 ${res.success} 条`);");
                sb.AppendLine("  }");
                sb.AppendLine("}");
            }
            sb.AppendLine();
            sb.AppendLine("/** 按 Content-Disposition 还原文件名（缺失时用兜底名） */");
            sb.AppendLine("function readFilename(headers: any, fallback: string): string {");
            sb.AppendLine("  const dispo: string = headers?.['content-disposition'] || '';");
            sb.AppendLine("  const match = dispo.match(/filename\\*?=(?:UTF-8''|\")?([^\";]+)/i);");
            sb.AppendLine("  let filename = match ? match[1].replace(/\"/g, '') : fallback;");
            sb.AppendLine("  try {");
            sb.AppendLine("    if (filename.includes('%')) filename = decodeURIComponent(filename);");
            sb.AppendLine("  } catch {");
            sb.AppendLine("    // 保留原名");
            sb.AppendLine("  }");
            sb.AppendLine("  return filename;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("/** 触发浏览器保存文件 */");
            sb.AppendLine("function saveBlob(blob: Blob, filename: string): void {");
            sb.AppendLine("  const url = URL.createObjectURL(blob);");
            sb.AppendLine("  const a = document.createElement('a');");
            sb.AppendLine("  a.href = url;");
            sb.AppendLine("  a.download = filename;");
            sb.AppendLine("  a.click();");
            sb.AppendLine("  URL.revokeObjectURL(url);");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static string TsType(CodegenFieldDesign f) => f.dataType switch
    {
        "int" or "long" or "decimal" => "number",
        "bool" => "boolean",
        _ => "string"
    };

    private static string GenFrontendPage(SysCodeGenTable design, List<CodegenFieldDesign> fields, string entityName, string camel,
        List<(CodegenDetailTableConfig Cfg, SysCodeGenTable Design, List<CodegenFieldDesign> Fields, string Entity, string Camel)>? masterDetails = null)
    {
        // 配置驱动页面：字段清单/字典关联/查询配置/导入导出按钮均按运行时配置渲染（增强配置补配即生效，无需重新生成）
        var enh = design.ParseEnhancement();
        var sb = new StringBuilder();
        sb.AppendLine("<!-- ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件） ====");
        sb.AppendLine($"     {design.DisplayName} 列表页：配置驱动（字段/字典/查询/导入导出按运行时配置渲染，补配即生效） -->");
        sb.AppendLine("<template>");
        sb.AppendLine("  <div class=\"page-container\">");
        sb.AppendLine("    <!-- 搜索与操作区 -->");
        sb.AppendLine("    <div class=\"table-operator\">");
        sb.AppendLine("      <a-input v-model:value=\"keyword\" placeholder=\"搜索\" allow-clear style=\"width: 220px\" @press-enter=\"loadData()\" />");
        sb.AppendLine("      <a-button type=\"primary\" style=\"margin-left: 8px\" @click=\"loadData()\">查询</a-button>");
        sb.AppendLine("      <a-button style=\"margin-left: 8px\" @click=\"resetQuery\">重置</a-button>");
        sb.AppendLine("      <a-button type=\"primary\" style=\"margin-left: 12px\" @click=\"openCreate\">新增</a-button>");
        if (enh != null && enh.exportEnabled)
            sb.AppendLine("      <a-button v-if=\"enhancement.exportEnabled\" style=\"margin-left: 12px\" @click=\"onExport\">导出</a-button>");
        if (enh != null && enh.importEnabled)
        {
            sb.AppendLine("      <a-upload v-if=\"enhancement.importEnabled\" :show-upload-list=\"false\" :before-upload=\"onImport\" accept=\".xlsx,.xls\" style=\"margin-left: 12px\">");
            sb.AppendLine("        <a-button>导入</a-button>");
            sb.AppendLine("      </a-upload>");
        }
        sb.AppendLine("      <a-space v-if=\"selectedRowKeys.length > 0\" style=\"margin-left: 12px\">");
        sb.AppendLine("        <span>已选 {{ selectedRowKeys.length }} 项</span>");
        sb.AppendLine("        <a-button danger @click=\"onBatchDelete\">批量删除</a-button>");
        sb.AppendLine("      </a-space>");
        sb.AppendLine("    </div>");
        sb.AppendLine();
        sb.AppendLine("    <!-- 表格（列按运行时配置渲染） -->");
        sb.AppendLine("    <a-table");
        sb.AppendLine("      :data-source=\"rows\"");
        sb.AppendLine("      :columns=\"listColumns\"");
        sb.AppendLine("      :loading=\"loading\"");
        sb.AppendLine("      row-key=\"id\"");
        sb.AppendLine("      size=\"middle\"");
        sb.AppendLine("      :pagination=\"pagination\"");
        sb.AppendLine("      :row-selection=\"{ selectedRowKeys, onChange: (k: any[]) => (selectedRowKeys = k) }\"");
        sb.AppendLine("      @change=\"onTableChange\"");
        sb.AppendLine("    >");
        sb.AppendLine("      <template #bodyCell=\"{ column, record }\">");
        sb.AppendLine("        <template v-if=\"column.key === 'action'\">");
        sb.AppendLine("          <a-space>");
        sb.AppendLine("            <a @click=\"openDetail(record)\">详情</a>");
        sb.AppendLine("            <a @click=\"openEdit(record)\">编辑</a>");
        sb.AppendLine("            <a-popconfirm title=\"确定删除该条记录吗？\" @confirm=\"onDelete(record)\">");
        sb.AppendLine("              <a class=\"danger-link\">删除</a>");
        sb.AppendLine("            </a-popconfirm>");
        sb.AppendLine("          </a-space>");
        sb.AppendLine("        </template>");
        sb.AppendLine("        <template v-else-if=\"dictMap[column.key]\">");
        sb.AppendLine("          {{ dictLabel(dictMap[column.key], record[column.key]) }}");
        sb.AppendLine("        </template>");
        sb.AppendLine("        <template v-else-if=\"boolKeys.includes(column.key)\">");
        sb.AppendLine("          {{ record[column.key] ? '是' : '否' }}");
        sb.AppendLine("        </template>");
        sb.AppendLine("      </template>");
        sb.AppendLine("    </a-table>");
        sb.AppendLine();
        sb.AppendLine("    <!-- 新增/编辑弹窗（控件按运行时配置渲染） -->");
        sb.AppendLine("    <a-modal v-model:open=\"dialogVisible\" :title=\"form.id ? '编辑' : '新增'\" :confirm-loading=\"saving\" @ok=\"onSave\">");
        sb.AppendLine("      <a-form layout=\"vertical\">");
        sb.AppendLine("        <a-form-item v-for=\"f in formFields\" :key=\"f.name\" :label=\"f.label\" :name=\"f.name\" :required=\"!f.nullable\">");
        sb.AppendLine("          <a-input-number v-if=\"ctrlKind(f) === 'number'\" v-model:value=\"form[f.name]\" style=\"width: 100%\" />");
        sb.AppendLine("          <a-date-picker v-else-if=\"ctrlKind(f) === 'date'\" v-model:value=\"form[f.name]\" show-time value-format=\"YYYY-MM-DD HH:mm:ss\" style=\"width: 100%\" />");
        sb.AppendLine("          <a-textarea v-else-if=\"ctrlKind(f) === 'textarea'\" v-model:value=\"form[f.name]\" :rows=\"3\" />");
        sb.AppendLine("          <a-select v-else-if=\"ctrlKind(f) === 'dict'\" v-model:value=\"form[f.name]\" :options=\"dictOptions[f.dictCode] || []\" placeholder=\"请选择\" allow-clear />");
        sb.AppendLine("          <a-switch v-else-if=\"ctrlKind(f) === 'switch'\" v-model:checked=\"form[f.name]\" />");
        sb.AppendLine("          <a-input v-else v-model:value=\"form[f.name]\" />");
        sb.AppendLine("        </a-form-item>");
        if (masterDetails != null)
        {
            // 主从表：表单内嵌从表行编辑（整单一个事务保存）
            for (var i = 0; i < masterDetails.Count; i++)
            {
                sb.AppendLine($"        <a-divider plain>{{{{ detailName({i}) }}}}（明细行）</a-divider>");
                sb.AppendLine("        <div style=\"margin-bottom: 8px\">");
                sb.AppendLine($"          <a-button size=\"small\" type=\"dashed\" @click=\"addDetailRow({i})\">添加一行</a-button>");
                sb.AppendLine("        </div>");
                sb.AppendLine($"        <a-table :data-source=\"detailRows[{i}] || []\" :columns=\"detailColumns({i})\" row-key=\"__k\" :pagination=\"false\" size=\"small\">");
                sb.AppendLine("          <template #bodyCell=\"{ column, record, index }\">");
                sb.AppendLine("            <template v-if=\"column.key === '__op'\">");
                sb.AppendLine($"              <a-popconfirm title=\"删除该明细行？\" @confirm=\"(detailRows[{i}] || []).splice(index, 1)\">");
                sb.AppendLine("                <a class=\"danger-link\">删除</a>");
                sb.AppendLine("              </a-popconfirm>");
                sb.AppendLine("            </template>");
                sb.AppendLine("            <template v-else>");
                sb.AppendLine($"              <a-input-number v-if=\"detailCtrlKind({i}, column.key) === 'number'\" v-model:value=\"record[column.key]\" size=\"small\" style=\"width: 100%\" />");
                sb.AppendLine($"              <a-date-picker v-else-if=\"detailCtrlKind({i}, column.key) === 'date'\" v-model:value=\"record[column.key]\" show-time value-format=\"YYYY-MM-DD HH:mm:ss\" size=\"small\" style=\"width: 100%\" />");
                sb.AppendLine($"              <a-select v-else-if=\"detailCtrlKind({i}, column.key) === 'dict'\" v-model:value=\"record[column.key]\" :options=\"dictOptions[detailDictCode({i}, column.key)] || []\" size=\"small\" allow-clear style=\"width: 100%\" />");
                sb.AppendLine($"              <a-switch v-else-if=\"detailCtrlKind({i}, column.key) === 'switch'\" v-model:checked=\"record[column.key]\" />");
                sb.AppendLine($"              <a-textarea v-else-if=\"detailCtrlKind({i}, column.key) === 'textarea'\" v-model:value=\"record[column.key]\" :rows=\"2\" />");
                sb.AppendLine("              <a-input v-else v-model:value=\"record[column.key]\" size=\"small\" />");
                sb.AppendLine("            </template>");
                sb.AppendLine("          </template>");
                sb.AppendLine("        </a-table>");
            }
        }
        sb.AppendLine("      </a-form>");
        sb.AppendLine("    </a-modal>");
        if (masterDetails != null)
        {
            // 主从表：详情抽屉（列表点行开抽屉，从表行只读展示）
            sb.AppendLine("    <a-drawer v-model:open=\"drawerVisible\" :title=\"detailTitle\" width=\"640\">");
            sb.AppendLine("      <div v-for=\"(dc, di) in config.details\" :key=\"di\" style=\"margin-bottom: 16px\">");
            sb.AppendLine("        <h4 style=\"margin: 0 0 8px\">{{ dc.displayName }}</h4>");
            sb.AppendLine("        <a-table :data-source=\"detailViewRows[Number(di)] || []\" :columns=\"detailViewColumns(Number(di))\" row-key=\"id\" size=\"small\" :pagination=\"false\">");
            sb.AppendLine("          <template #bodyCell=\"{ column, record }\">");
            sb.AppendLine("            <template v-if=\"detailKind(Number(di), column.key) === 'dict'\">{{ dictLabel(detailDictCode(Number(di), column.key), record[column.key]) }}</template>");
            sb.AppendLine("            <template v-else-if=\"detailKind(Number(di), column.key) === 'bool'\">{{ record[column.key] ? '是' : '否' }}</template>");
            sb.AppendLine("            <template v-else>{{ record[column.key] }}</template>");
            sb.AppendLine("          </template>");
            sb.AppendLine("        </a-table>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </a-drawer>");
        }
        sb.AppendLine("  </div>");
        sb.AppendLine("</template>");
        sb.AppendLine();
        sb.AppendLine("<script setup lang=\"ts\">");
        var importNames = $"page{entityName}, get{entityName}, save{entityName}, delete{entityName}, batchDelete{entityName}, get{entityName}Config";
        if (enh != null && enh.exportEnabled) importNames += $", export{entityName}";
        if (enh != null && enh.importEnabled) importNames += $", import{entityName}, notifyImportResult";
        sb.AppendLine($"import {{ {importNames} }} from '@/api/{camel}'");
        sb.AppendLine("import { request } from '@/api/request'");
        sb.AppendLine("import { message, Modal } from 'ant-design-vue'");
        sb.AppendLine("import { ref, reactive, computed } from 'vue'");
        sb.AppendLine();
        sb.AppendLine("// 运行时配置：字段/字典/查询/导入导出按配置渲染，增强配置补配即生效");
        sb.AppendLine("const config = ref<any>({ fields: [], enhancement: {} })");
        sb.AppendLine("const fields = computed<any[]>(() => config.value.fields || [])");
        sb.AppendLine("const enhancement = computed<any>(() => config.value.enhancement || {})");
        sb.AppendLine("const listColumns = computed(() => [");
        sb.AppendLine("  ...fields.value.filter((f: any) => f.showInList).map((f: any) => ({ title: f.label, dataIndex: f.name, key: f.name })),");
        sb.AppendLine("  { title: '操作', key: 'action', width: 140 },");
        sb.AppendLine("])");
        sb.AppendLine("const formFields = computed(() => fields.value)");
        sb.AppendLine("const boolKeys = computed(() => fields.value.filter((f: any) => f.dataType === 'bool').map((f: any) => f.name))");
        sb.AppendLine("const dictMap = computed<Record<string, string>>(() => {");
        sb.AppendLine("  const m: Record<string, string> = {};");
        sb.AppendLine("  fields.value.filter((f: any) => f.control === 'dict' && f.dictCode).forEach((f: any) => (m[f.name] = f.dictCode));");
        sb.AppendLine("  return m;");
        sb.AppendLine("})");
        sb.AppendLine("const dictOptions = ref<Record<string, any[]>>({})");
        sb.AppendLine("function ctrlKind(f: any): string {");
        sb.AppendLine("  if (f.control === 'dict' && f.dictCode) return 'dict'");
        sb.AppendLine("  if (f.control === 'number' || ['int', 'long', 'decimal'].includes(f.dataType)) return 'number'");
        sb.AppendLine("  if (f.control === 'date' || f.dataType === 'datetime') return 'date'");
        sb.AppendLine("  if (f.control === 'textarea') return 'textarea'");
        sb.AppendLine("  if (f.control === 'switch') return 'switch'");
        sb.AppendLine("  return 'text'");
        sb.AppendLine("}");
        sb.AppendLine("function dictLabel(code: string, value: any): string {");
        sb.AppendLine("  const hit = (dictOptions.value[code] || []).find((o: any) => String(o.value) === String(value))");
        sb.AppendLine("  return hit ? hit.label : (value ?? '')");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("const rows = ref<any[]>([])");
        sb.AppendLine("const loading = ref(false)");
        sb.AppendLine("const saving = ref(false)");
        sb.AppendLine("const keyword = ref('')");
        sb.AppendLine("const selectedRowKeys = ref<any[]>([])");
        sb.AppendLine("const dialogVisible = ref(false)");
        sb.AppendLine("const pagination = reactive({ current: 1, pageSize: 10, total: 0, showSizeChanger: true, showTotal: (t: number) => `共 ${t} 条` })");
        sb.AppendLine("const form = ref<any>({ id: undefined })");
        if (masterDetails != null)
        {
            sb.AppendLine("const detailRows = ref<any[][]>([])");
            sb.AppendLine("let detailKeySeq = 0");
            sb.AppendLine("const drawerVisible = ref(false)");
            sb.AppendLine("const detailTitle = ref('')");
            sb.AppendLine("const detailViewRows = ref<any[][]>([])");
        }
        sb.AppendLine();
        sb.AppendLine("async function loadConfig() {");
        sb.AppendLine($"  config.value = await get{entityName}Config()");
        sb.AppendLine("  for (const code of Object.values(dictMap.value)) {");
        sb.AppendLine("    if (!dictOptions.value[code]) {");
        sb.AppendLine("      try {");
        sb.AppendLine("        dictOptions.value[code] = await request({ url: `/identity/dict/items/${code}`, method: 'get' })");
        sb.AppendLine("      } catch {");
        sb.AppendLine("        dictOptions.value[code] = []");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        if (masterDetails != null)
        {
            // 从表字典选项一并预取（行编辑与详情抽屉共用）
            sb.AppendLine("  const detailCodes: string[] = []");
            sb.AppendLine("  for (const dc of config.value.details || []) {");
            sb.AppendLine("    if (dc.dictCode) detailCodes.push(dc.dictCode)");
            sb.AppendLine("    for (const f of dc.fields || []) if (f.control === 'dict' && f.dictCode) detailCodes.push(f.dictCode)");
            sb.AppendLine("  }");
            sb.AppendLine("  for (const code of detailCodes) {");
            sb.AppendLine("    if (!dictOptions.value[code]) {");
            sb.AppendLine("      try {");
            sb.AppendLine("        dictOptions.value[code] = await request({ url: `/identity/dict/items/${code}`, method: 'get' })");
            sb.AppendLine("      } catch {");
            sb.AppendLine("        dictOptions.value[code] = []");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function loadData() {");
        sb.AppendLine("  loading.value = true");
        sb.AppendLine("  try {");
        sb.AppendLine($"    const res = await page{entityName}({{ pageIndex: pagination.current, pageSize: pagination.pageSize, keyword: keyword.value || undefined }})");
        sb.AppendLine("    rows.value = res.items");
        sb.AppendLine("    pagination.total = res.total");
        sb.AppendLine("  } finally {");
        sb.AppendLine("    loading.value = false");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function resetQuery() {");
        sb.AppendLine("  keyword.value = ''");
        sb.AppendLine("  pagination.current = 1");
        sb.AppendLine("  loadData()");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function onTableChange(p: any) {");
        sb.AppendLine("  pagination.current = p.current");
        sb.AppendLine("  pagination.pageSize = p.pageSize");
        sb.AppendLine("  loadData()");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function openCreate() {");
        sb.AppendLine("  form.value = { id: undefined }");
        sb.AppendLine("  dialogVisible.value = true");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function openEdit(row: any) {");
        if (masterDetails != null)
        {
            sb.AppendLine($"  const res: any = await get{entityName}(row.id)");
            sb.AppendLine("  form.value = res.master");
            sb.AppendLine("  detailRows.value = (config.value.details || []).map((_: any, di: number) =>");
            sb.AppendLine("    ((res as any)['details' + di] || []).map((r: any) => ({ __k: ++detailKeySeq, ...r })))");
        }
        else
        {
            sb.AppendLine($"  form.value = await get{entityName}(row.id)");
        }
        sb.AppendLine("  dialogVisible.value = true");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function onSave() {");
        sb.AppendLine("  saving.value = true");
        sb.AppendLine("  try {");
        if (masterDetails != null)
        {
            sb.AppendLine("    const payload: any = { ...form.value }");
            sb.AppendLine("    for (let i = 0; i < (config.value.details || []).length; i++) {");
            sb.AppendLine("      payload['details' + i] = (detailRows.value[i] || []).map((r: any) => {");
            sb.AppendLine("        const { __k, ...rest } = r");
            sb.AppendLine("        return rest");
            sb.AppendLine("      })");
            sb.AppendLine("    }");
            sb.AppendLine($"    await save{entityName}(payload)");
        }
        else
        {
            sb.AppendLine($"    await save{entityName}(form.value)");
        }
        sb.AppendLine("    message.success('保存成功')");
        sb.AppendLine("    dialogVisible.value = false");
        sb.AppendLine("    await loadData()");
        sb.AppendLine("  } finally {");
        sb.AppendLine("    saving.value = false");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function onDelete(row: any) {");
        sb.AppendLine($"  return delete{entityName}(row.id).then(() => {{");
        sb.AppendLine("    message.success('删除成功')");
        sb.AppendLine("    return loadData()");
        sb.AppendLine("  })");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function onBatchDelete() {");
        sb.AppendLine("  Modal.confirm({");
        sb.AppendLine("    title: '批量删除',");
        sb.AppendLine($"    content: `确认删除选中的 ${{selectedRowKeys.value.length}} 条记录？`,");
        sb.AppendLine("    onOk: async () => {");
        sb.AppendLine($"      const res = await batchDelete{entityName}(selectedRowKeys.value)");
        sb.AppendLine("      message.success(`已删除 ${res.deleted} 条`)");
        sb.AppendLine("      selectedRowKeys.value = []");
        sb.AppendLine("      await loadData()");
        sb.AppendLine("    }");
        sb.AppendLine("  })");
        sb.AppendLine("}");
        if (enh != null && enh.exportEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("async function onExport() {");
            sb.AppendLine($"  await export{entityName}(keyword.value || undefined)");
            sb.AppendLine("  message.success('导出成功')");
            sb.AppendLine("}");
        }
        if (enh != null && enh.importEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("function onImport(file: File) {");
            sb.AppendLine($"  import{entityName}(file)");
            sb.AppendLine("    .then((res: any) => {");
            sb.AppendLine("      notifyImportResult(res)");
            sb.AppendLine("      loadData()");
            sb.AppendLine("    })");
            sb.AppendLine("    .catch(() => {})");
            sb.AppendLine("  return false; // 阻止 a-upload 自动上传，改走手工导入");
            sb.AppendLine("}");
        }
        if (masterDetails != null)
        {
            sb.AppendLine();
            sb.AppendLine("// ---------- 主从表：从表行编辑与详情抽屉 ----------");
            sb.AppendLine("function detailCfg(i: number): any {");
            sb.AppendLine("  return (config.value.details || [])[i] || {}");
            sb.AppendLine("}");
            sb.AppendLine("function detailName(i: number): string {");
            sb.AppendLine("  return detailCfg(i).displayName || `明细${i + 1}`");
            sb.AppendLine("}");
            sb.AppendLine("function detailFieldOf(i: number, key: string): any {");
            sb.AppendLine("  return (detailCfg(i).fields || []).find((f: any) => f.name === key)");
            sb.AppendLine("}");
            sb.AppendLine("function detailColumns(i: number): any[] {");
            sb.AppendLine("  const cfg = detailCfg(i)");
            sb.AppendLine("  const cols = (cfg.fields || []).filter((f: any) => f.name !== cfg.foreignKey).map((f: any) => ({ title: f.label, dataIndex: f.name, key: f.name }))");
            sb.AppendLine("  return [...cols, { title: '操作', key: '__op', width: 70 }]");
            sb.AppendLine("}");
            sb.AppendLine("function detailViewColumns(di: number): any[] {");
            sb.AppendLine("  const cfg = detailCfg(di)");
            sb.AppendLine("  return (cfg.fields || []).filter((f: any) => f.name !== cfg.foreignKey).map((f: any) => ({ title: f.label, dataIndex: f.name, key: f.name }))");
            sb.AppendLine("}");
            sb.AppendLine("function detailCtrlKind(i: number, key: string): string {");
            sb.AppendLine("  const f = detailFieldOf(i, key)");
            sb.AppendLine("  if (!f) return 'text'");
            sb.AppendLine("  const cfg = detailCfg(i)");
            sb.AppendLine("  if ((cfg.dataSource === 'dict' && cfg.dictCode && (key === cfg.dictCode || f.control === 'dict')) || (f.control === 'dict' && f.dictCode)) return 'dict'");
            sb.AppendLine("  if (f.control === 'number' || ['int', 'long', 'decimal'].includes(f.dataType)) return 'number'");
            sb.AppendLine("  if (f.control === 'date' || f.dataType === 'datetime') return 'date'");
            sb.AppendLine("  if (f.control === 'textarea') return 'textarea'");
            sb.AppendLine("  if (f.control === 'switch') return 'switch'");
            sb.AppendLine("  return 'text'");
            sb.AppendLine("}");
            sb.AppendLine("function detailKind(di: number, key: string): string {");
            sb.AppendLine("  const f = detailFieldOf(di, key)");
            sb.AppendLine("  if (!f) return 'plain'");
            sb.AppendLine("  if (detailCtrlKind(di, key) === 'dict') return 'dict'");
            sb.AppendLine("  if (f.dataType === 'bool') return 'bool'");
            sb.AppendLine("  return 'plain'");
            sb.AppendLine("}");
            sb.AppendLine("function detailDictCode(i: number, key: string): string {");
            sb.AppendLine("  const f = detailFieldOf(i, key)");
            sb.AppendLine("  if (f?.dictCode) return f.dictCode");
            sb.AppendLine("  return detailCfg(i).dictCode || ''");
            sb.AppendLine("}");
            sb.AppendLine("function detailDefaults(f: any): any {");
            sb.AppendLine("  if (['int', 'long', 'decimal'].includes(f.dataType)) return 0");
            sb.AppendLine("  if (f.dataType === 'bool') return false");
            sb.AppendLine("  return ''");
            sb.AppendLine("}");
            sb.AppendLine("function addDetailRow(i: number) {");
            sb.AppendLine("  const cfg = detailCfg(i)");
            sb.AppendLine("  const row: any = { __k: ++detailKeySeq }");
            sb.AppendLine("  for (const f of cfg.fields || []) if (f.name !== cfg.foreignKey) row[f.name] = detailDefaults(f)");
            sb.AppendLine("  if (!detailRows.value[i]) detailRows.value[i] = []");
            sb.AppendLine("  detailRows.value[i].push(row)");
            sb.AppendLine("}");
            sb.AppendLine("async function openDetail(row: any) {");
            sb.AppendLine($"  const res: any = await get{entityName}(row.id)");
            sb.AppendLine("  detailTitle.value = (config.value.displayName || '') + '详情'");
            sb.AppendLine("  detailViewRows.value = (config.value.details || []).map((_: any, di: number) => (res as any)['details' + di] || [])");
            sb.AppendLine("  drawerVisible.value = true");
            sb.AppendLine("}");
        }
        sb.AppendLine();
        sb.AppendLine("loadConfig().then(loadData)");
        sb.AppendLine("</script>");
        return sb.ToString();
    }

    private static string TsInit(CodegenFieldDesign f) => f.dataType switch
    {
        "int" or "long" or "decimal" => "0",
        "bool" => "false",
        _ => "''"
    };

    private static string GenMenuPermissionSql(SysCodeGenTable design, string entityName)
    {
        var perm = $"gen:{design.TableName}:manage";
        return $@"-- {design.DisplayName}：菜单与按钮权限初始化（装上后在角色管理里勾选）
-- 菜单（挂在系统管理下，parent_id 请按实际菜单 Id 调整）
INSERT INTO sys_menu (id, parent_id, name, path, component, menu_type, permission, sort, is_deleted, created_at, updated_at)
VALUES ({{雪花ID}}, 0, '{design.DisplayName}', '/{design.TableName}', '{ToCamel(entityName)}/index', 1, NULL, 100, 0, NOW(), NOW());
-- 按钮权限
INSERT INTO sys_menu (id, parent_id, name, path, component, menu_type, permission, sort, is_deleted, created_at, updated_at)
VALUES ({{雪花ID}}, {{上级菜单ID}}, '{design.DisplayName}管理', '', '', 2, '{perm}', 1, 0, NOW(), NOW());";
    }
}
