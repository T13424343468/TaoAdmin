// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件） ====
// 订单主表 接口定义
import { request } from './request';

export interface BizOrderItem {
  id: string;
  orderNo: string;
  orderAmount: number;
}

export interface BizOrderSave {
  id?: string;
  orderNo: string;
  orderAmount: number;
}

/** 分页查询（query 传设计字段的 eq/range 过滤，如 { order_no: 'x' } 或 { price_from, price_to }） */
export function pageBizOrder(params: { pageIndex?: number; pageSize?: number; keyword?: string } & Record<string, any>): Promise<{ total: number; items: BizOrderItem[] }> {
  return request({ url: '/identity/biz_order', method: 'get', params });
}

/** 详情 */
export function getBizOrder(id: string): Promise<{ master: BizOrderItem; details0: BizOrderItemItem[] }> {
  return request({ url: `/identity/biz_order/${id}`, method: 'get' });
}

/** 保存（新增或更新） */
export function saveBizOrder(data: BizOrderSave): Promise<void> {
  return request({ url: '/identity/biz_order', method: 'post', data });
}

/** 删除（单条） */
export function deleteBizOrder(id: string): Promise<void> {
  return request({ url: `/identity/biz_order/${id}`, method: 'delete' });
}

/** 批量删除 */
export function batchDeleteBizOrder(ids: string[]): Promise<{ deleted: number }> {
  return request({ url: '/identity/biz_order/batch-delete', method: 'post', data: { ids } });
}

/** 运行时配置（字段/字典/查询/导入导出按配置渲染，补配后无需重新生成） */
export function getBizOrderConfig(): Promise<any> {
  return request({ url: '/identity/codegen/designs/913454565788094464/config', method: 'get' });
}
