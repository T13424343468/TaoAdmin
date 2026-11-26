// ==== CODEGEN GENERATED FILE（生成区：重复生成会覆盖本文件） ====
// 订单明细 接口定义
import { request } from './request';

export interface BizOrderItemItem {
  id: string;
  orderId: number;
  productName: string;
  quantity: number;
  price: number;
}

export interface BizOrderItemSave {
  id?: string;
  orderId: number;
  productName: string;
  quantity: number;
  price: number;
}

/** 分页查询（query 传设计字段的 eq/range 过滤，如 { order_no: 'x' } 或 { price_from, price_to }） */
export function pageBizOrderItem(params: { pageIndex?: number; pageSize?: number; keyword?: string } & Record<string, any>): Promise<{ total: number; items: BizOrderItemItem[] }> {
  return request({ url: '/identity/biz_order_item', method: 'get', params });
}

/** 详情 */
export function getBizOrderItem(id: string): Promise<BizOrderItemItem> {
  return request({ url: `/identity/biz_order_item/${id}`, method: 'get' });
}

/** 保存（新增或更新） */
export function saveBizOrderItem(data: BizOrderItemSave): Promise<void> {
  return request({ url: '/identity/biz_order_item', method: 'post', data });
}

/** 删除（单条） */
export function deleteBizOrderItem(id: string): Promise<void> {
  return request({ url: `/identity/biz_order_item/${id}`, method: 'delete' });
}

/** 批量删除 */
export function batchDeleteBizOrderItem(ids: string[]): Promise<{ deleted: number }> {
  return request({ url: '/identity/biz_order_item/batch-delete', method: 'post', data: { ids } });
}

/** 运行时配置（字段/字典/查询/导入导出按配置渲染，补配后无需重新生成） */
export function getBizOrderItemConfig(): Promise<any> {
  return request({ url: '/identity/codegen/designs/913454557227520000/config', method: 'get' });
}
