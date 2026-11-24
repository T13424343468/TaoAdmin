import { request } from './request';

// ============ 服务监控 ============

export interface ServiceInstance {
  instanceId: string;
  host: string;
  port: number;
  status: 'UP' | 'DOWN' | 'STARTING' | 'OUT_OF_SERVICE';
  lastHeartbeat?: string;
}

export interface ServiceItem {
  serviceName: string;
  displayName?: string;
  instances: number;
  status: 'UP' | 'DOWN' | 'PARTIAL';
  lastHealthCheck?: string;
  version?: string;
  description?: string;
  instanceList?: ServiceInstance[];
}

/** 服务列表（走网关） */
export function getServices(): Promise<ServiceItem[]> {
  return request<ServiceItem[]>({ url: '/monitor/services', method: 'get' });
}

/** 单个服务健康详情 */
export function getServiceDetail(serviceName: string): Promise<ServiceItem> {
  return request<ServiceItem>({ url: `/monitor/services/${serviceName}`, method: 'get' });
}

// ============ 日志查询 ============

export interface LogQuery {
  pageIndex?: number;
  pageSize?: number;
  startTime?: string;
  endTime?: string;
  level?: string; // DEBUG/INFO/WARN/ERROR/FATAL
  service?: string;
  keyword?: string;
  traceId?: string;
}

export interface LogItem {
  id: string;
  timestamp: string;
  level: string;
  service: string;
  instance?: string;
  traceId?: string;
  thread?: string;
  logger?: string;
  message: string;
  exception?: string;
}

export interface LogPageResult {
  items: LogItem[];
  total: number;
  pageIndex: number;
  pageSize: number;
}

/** 分页查询日志 */
export function getLogs(params: LogQuery): Promise<LogPageResult> {
  return request<LogPageResult>({ url: '/monitor/logs', method: 'get', params });
}

/** 查询单条日志详情（含堆栈） */
export function getLogDetail(id: string): Promise<LogItem> {
  return request<LogItem>({ url: `/monitor/logs/${id}`, method: 'get' });
}

// ============ 在线用户 ============

export interface OnlineUserItem {
  sessionId: string;
  userId: string;
  userName: string;
  nickName?: string;
  ip: string;
  userAgent?: string;
  loginTime: string;
  lastActiveTime: string;
}

/** 当前在线用户名单 */
export function getOnlineUsers(): Promise<OnlineUserItem[]> {
  return request<OnlineUserItem[]>({ url: '/identity/online-users', method: 'get' });
}

/** 强制下线指定会话（该令牌即刻失效） */
export function kickOnlineUser(sessionId: string): Promise<void> {
  return request<void>({ url: `/identity/online-users/${encodeURIComponent(sessionId)}`, method: 'delete' });
}

/** 批量强制下线 */
export function batchKickOnlineUsers(sessionIds: string[]): Promise<{ kicked: number }> {
  return request<{ kicked: number }>({
    url: '/identity/online-users/batch-kick',
    method: 'post',
    data: { sessionIds },
  });
}

// ============ 资源监控 ============

/** 单服务单时刻资源快照（与后端 ResourceSnapshot 对齐） */
export interface ResourceSnapshotItem {
  serviceName: string;
  collectedAt: string;
  cpuPercent: number;
  memoryUsedMB: number;
  memoryTotalMB: number;
  memoryPercent: number;
  diskUsedGB: number;
  diskTotalGB: number;
  diskPercent: number;
  netRxKBps: number;
  netTxKBps: number;
  available: boolean;
}

/** 资源异常阈值（百分比） */
export interface ResourceThresholds {
  cpuPercent: number;
  memoryPercent: number;
  diskPercent: number;
}

/** 资源告警事件（与后端 AlertEvent 对齐） */
export interface ResourceAlertEvent {
  eventId: string;
  type: string;
  severity: string;
  source: string;
  title: string;
  content: string;
  occurredAt: string;
}

/** 后端数值字段可能序列化为字符串，统一归一为 number */
function toNum(v: unknown): number {
  const n = typeof v === 'number' ? v : parseFloat(String(v ?? 0));
  return Number.isFinite(n) ? n : 0;
}

/** 快照数值字段归一（防御后端字符串序列化） */
function normalizeSnapshot(raw: any): ResourceSnapshotItem {
  return {
    serviceName: String(raw?.serviceName ?? ''),
    collectedAt: String(raw?.collectedAt ?? ''),
    cpuPercent: toNum(raw?.cpuPercent),
    memoryUsedMB: toNum(raw?.memoryUsedMB),
    memoryTotalMB: toNum(raw?.memoryTotalMB),
    memoryPercent: toNum(raw?.memoryPercent),
    diskUsedGB: toNum(raw?.diskUsedGB),
    diskTotalGB: toNum(raw?.diskTotalGB),
    diskPercent: toNum(raw?.diskPercent),
    netRxKBps: toNum(raw?.netRxKBps),
    netTxKBps: toNum(raw?.netTxKBps),
    available: Boolean(raw?.available),
  };
}

/** 阈值数值字段归一 */
function normalizeThresholds(raw: any): ResourceThresholds {
  return {
    cpuPercent: toNum(raw?.cpuPercent),
    memoryPercent: toNum(raw?.memoryPercent),
    diskPercent: toNum(raw?.diskPercent),
  };
}

/** 全部服务最新资源快照（含阈值） */
export async function getResourceCurrent(): Promise<{ items: ResourceSnapshotItem[]; thresholds: ResourceThresholds }> {
  const data = await request<any>({ url: '/monitor/resources/current', method: 'get' });
  return {
    items: (data?.items || []).map(normalizeSnapshot),
    thresholds: normalizeThresholds(data?.thresholds),
  };
}

/** 单服务近期趋势（minutes 窗口内的采样序列，旧→新） */
export async function getResourceHistory(
  serviceName: string,
  minutes = 60,
): Promise<{ serviceName: string; minutes: number; items: ResourceSnapshotItem[]; thresholds: ResourceThresholds }> {
  const data = await request<any>({
    url: '/monitor/resources/history',
    method: 'get',
    params: { serviceName, minutes },
  });
  return {
    serviceName: String(data?.serviceName ?? serviceName),
    minutes: toNum(data?.minutes),
    items: (data?.items || []).map(normalizeSnapshot),
    thresholds: normalizeThresholds(data?.thresholds),
  };
}

/** 读取资源异常阈值 */
export async function getResourceThresholds(): Promise<ResourceThresholds> {
  return normalizeThresholds(await request<any>({ url: '/monitor/resources/thresholds', method: 'get' }));
}

/** 保存资源异常阈值（保存即生效） */
export function saveResourceThresholds(data: ResourceThresholds): Promise<string> {
  return request<string>({ url: '/monitor/resources/thresholds', method: 'put', data });
}

/** 近期资源告警事件（新→旧，最多 50 条） */
export function getResourceAlerts(): Promise<{ items: ResourceAlertEvent[] }> {
  return request<{ items: ResourceAlertEvent[] }>({ url: '/monitor/resources/alerts', method: 'get' });
}
