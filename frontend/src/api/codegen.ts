import { request } from './request';

// ============ 代码生成器（⑲） ============

/** 单个字段设计 */
export interface CodegenFieldDesign {
  name: string;
  label: string;
  dataType: 'string' | 'int' | 'long' | 'decimal' | 'datetime' | 'bool' | 'text';
  length?: number;
  precision?: number;
  nullable: boolean;
  defaultValue?: string;
  isPrimary: boolean;
  isUnique: boolean;
  isIndex: boolean;
  showInList: boolean;
  control: 'text' | 'number' | 'date' | 'textarea' | 'dict' | 'switch';
  dictCode?: string;
  queryType: 'none' | 'eq' | 'like' | 'range';
  rules?: string;
}

/** 表设计 */
export interface CodegenDesign {
  id: string;
  tableName: string;
  displayName: string;
  fieldsJson?: string;
  remark?: string;
  lastSyncAt?: string;
  createdAt: string;
  /** 是否主从表设计（主表视角） */
  isMasterDetail?: boolean;
  /** 主从关系配置 JSON（CodegenMasterDetailConfig） */
  masterDetailJson?: string;
  /** 增强配置 JSON（CodegenEnhancementConfig） */
  enhancementJson?: string;
  /** 目标服务工程名（控制器落进哪个服务；空 = 默认权限服务） */
  targetService?: string;
}

/** 单个从表配置 */
export interface CodegenDetailTableConfig {
  /** 从表设计 Id */
  detailTableId: string;
  /** 外键字段名（从表上指向主表 Id 的字段） */
  foreignKey: string;
  /** 从表是否生成独立全套功能（独立菜单/页面） */
  standalone: boolean;
  /** 从表数据来源：free=自由录入，dict=从字典挑 */
  dataSource: 'free' | 'dict';
  /** 字典编码（dataSource=dict 时必填） */
  dictCode?: string;
}

/** 主从关系配置（主表视角）：一层主从，主表挂多从表 */
export interface CodegenMasterDetailConfig {
  details: CodegenDetailTableConfig[];
}

/** 增强配置（⑲补配即生效：导入导出等，不动已生成标准代码） */
export interface CodegenEnhancementConfig {
  /** 启用 Excel 导入（模板下载 + 逐行校验导入） */
  importEnabled: boolean;
  /** 启用 Excel 导出 */
  exportEnabled: boolean;
}

/** 表设计保存请求 */
export interface CodegenDesignSave {
  id?: string;
  tableName: string;
  displayName: string;
  fields: CodegenFieldDesign[];
  remark?: string;
  /** 主从关系配置（主表视角；不传表示单表设计） */
  masterDetail?: CodegenMasterDetailConfig;
  /** 增强配置（null 表示清空） */
  enhancement?: CodegenEnhancementConfig;
  /** 目标服务工程名（控制器落进哪个服务；空 = 默认权限服务） */
  targetService?: string;
}

/** 同步建库结果 */
export interface CodegenSyncResult {
  executed: boolean;
  actions: string[];
  destructivePending: string[];
}

/** 生成出的单个文件 */
export interface CodegenFile {
  path: string;
  content: string;
}

/** 一键生成结果 */
export interface CodegenGenerateResult {
  recordId: string;
  tableName: string;
  files: CodegenFile[];
  elapsedMs: number;
}

/** 生成记录 */
export interface CodegenRecord {
  id: string;
  tableName: string;
  mode: string;
  fileCount: number;
  elapsedMs: number;
  createdAt: string;
}

/** 表设计分页 */
export function getCodegenDesigns(params: { pageIndex?: number; pageSize?: number; keyword?: string }): Promise<{
  total: number;
  items: CodegenDesign[];
}> {
  return request({ url: '/identity/codegen/designs', method: 'get', params });
}

/** 保存表设计（新建或更新） */
export function saveCodegenDesign(data: CodegenDesignSave): Promise<string> {
  return request({ url: '/identity/codegen/designs', method: 'post', data });
}

/** 表设计详情 */
export function getCodegenDesign(id: string): Promise<CodegenDesign> {
  return request({ url: `/identity/codegen/designs/${id}`, method: 'get' });
}

/** 删除表设计（软删，不动已生成的库表与代码） */
export function deleteCodegenDesign(id: string): Promise<void> {
  return request({ url: `/identity/codegen/designs/${id}`, method: 'delete' });
}

/** 同步到数据库：建表/加列自动执行；删列等破坏性变更仅返回语句待确认 */
export function syncCodegenDesign(id: string): Promise<CodegenSyncResult> {
  return request({ url: `/identity/codegen/designs/${id}/sync`, method: 'post' });
}

/** 执行经确认的破坏性变更语句（仅允许单条 ALTER TABLE） */
export function executeDestructiveSql(sql: string): Promise<void> {
  return request({ url: '/identity/codegen/destructive', method: 'post', data: { sql } });
}

/** 一键生成全套增删改查前后端代码（落生成记录） */
export function generateCodegen(id: string, mode?: string): Promise<CodegenGenerateResult> {
  return request({ url: `/identity/codegen/designs/${id}/generate`, method: 'post', params: { mode } });
}

/** 生成记录分页 */
export function getCodegenRecords(params: { pageIndex?: number; pageSize?: number; keyword?: string }): Promise<{
  total: number;
  items: CodegenRecord[];
}> {
  return request({ url: '/identity/codegen/records', method: 'get', params });
}

/** 生成记录详情（含全部生成文件内容） */
export function getCodegenRecord(id: string): Promise<Record<string, any>> {
  return request({ url: `/identity/codegen/records/${id}`, method: 'get' });
}

// ============ 服务登记与一键生成新服务（③④） ============

/** 已登记的后端服务 */
export interface CodegenService {
  id: string;
  serviceName: string;
  shortName: string;
  routePrefix: string;
  port: number;
  displayName: string;
  isBuiltIn: boolean;
}

/** 一键生成新服务请求（端口缺省自动分配；路由前缀缺省 = 短名小写） */
export interface CodegenServiceScaffold {
  shortName: string;
  displayName: string;
  routePrefix?: string;
  port?: number;
}

/** 一键生成新服务结果 */
export interface CodegenServiceScaffoldResult {
  serviceId: string;
  serviceName: string;
  port: number;
  routePrefix: string;
  files: CodegenFile[];
}

/** 编译验证结果（⑤） */
export interface CodegenBuildVerifyResult {
  success: boolean;
  writtenFiles: string[];
  output: string;
  restartOutput?: string;
  elapsedMs: number;
}

/** 服务清单（生成器目标服务下拉数据源） */
export function getCodegenServices(): Promise<CodegenService[]> {
  return request({ url: '/identity/codegen/services', method: 'get' });
}

/** 一键生成新服务：脚手架全套 + 端口自动分配 + 网关/部署/compose 登记片段，登记后即可选为目标服务 */
export function scaffoldCodegenService(data: CodegenServiceScaffold): Promise<CodegenServiceScaffoldResult> {
  return request({ url: '/identity/codegen/services/scaffold', method: 'post', data });
}

/** 编译验证：生成文件写入源码根目录（站点参数 codegen.repoRoot）并编译；restart=true 时编译通过后执行重启命令 */
export function buildVerifyCodegen(recordId: string, restart: boolean): Promise<CodegenBuildVerifyResult> {
  return request({ url: '/identity/codegen/build-verify', method: 'post', data: { recordId, restart } });
}
