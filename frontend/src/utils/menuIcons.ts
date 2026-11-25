import { h, type Component } from 'vue';
import * as AntIcons from '@ant-design/icons-vue';

/**
 * 菜单图标统一注册表（老板令 2026-11-25：左侧所有菜单必须有图标，库没有就自绘制补充）。
 *
 * 三层解析：语义名 → 图标库（XxxOutlined）→ 扩展表 → 自绘 SVG。
 * 菜单数据（SysMenu.Icon）存语义名（如 Setting/Team），渲染与选择器共用本表，禁止各自维护映射。
 */

/** 图标库没有的语义名 → 图标库实际组件名（先例：Timer→ClockCircle，老板 11-22 令建的表） */
export const MENU_ICON_EXTENSIONS: Record<string, string> = {
  Timer: 'ClockCircleOutlined',
};

/**
 * 自绘 SVG 图标注册表：语义名 → 渲染函数。
 * 图标库与扩展表都查不到的语义名在这里登记自绘实现（老板令 2026-11-25）。
 * 当前菜单集已全部命中图标库，此表为空；新增自绘图标时在此登记一行即可，
 * 选择器与侧边栏渲染自动生效，无需改其他代码。
 */
export const CUSTOM_SVG_ICONS: Record<string, () => Component> = {};

/** 语义名 → 图标组件；查不到返回 undefined（调用方不渲染，不报错） */
export function resolveMenuIcon(name?: string): Component | undefined {
  if (!name) return undefined;
  const icon =
    (AntIcons as unknown as Record<string, Component>)[name] ||
    (AntIcons as unknown as Record<string, Component>)[`${name}Outlined`] ||
    (MENU_ICON_EXTENSIONS[name] ? (AntIcons as unknown as Record<string, Component>)[MENU_ICON_EXTENSIONS[name]] : undefined) ||
    CUSTOM_SVG_ICONS[name]?.();
  return icon || undefined;
}

export interface MenuIconOption {
  /** 存入菜单数据的语义名（如 Team），与 resolveMenuIcon 口径一致 */
  value: string;
  /** 展示名（语义名本身） */
  label: string;
  /** 图标组件，供选项预览 */
  icon: Component;
}

let cachedOptions: MenuIconOption[] | null = null;

/** 图标选择器全量选项：图标库 Outlined 全集 + 扩展表 + 自绘 SVG，按名称排序 */
export function menuIconOptions(): MenuIconOption[] {
  if (cachedOptions) return cachedOptions;
  const seen = new Set<string>();
  const options: MenuIconOption[] = [];
  const push = (semantic: string, icon: Component) => {
    if (!semantic || seen.has(semantic)) return;
    seen.add(semantic);
    options.push({ value: semantic, label: semantic, icon });
  };
  for (const [exportName, comp] of Object.entries(AntIcons as Record<string, Component>)) {
    if (!exportName.endsWith('Outlined')) continue;
    if (!comp) continue;
    push(exportName.replace(/Outlined$/, ''), comp);
  }
  for (const [semantic, comp] of Object.entries(MENU_ICON_EXTENSIONS)) {
    const icon = (AntIcons as unknown as Record<string, Component>)[comp];
    if (icon) push(semantic, icon);
  }
  for (const [semantic, render] of Object.entries(CUSTOM_SVG_ICONS)) {
    const icon = render();
    if (icon) push(semantic, icon);
  }
  options.sort((a, b) => a.value.localeCompare(b.value));
  cachedOptions = options;
  return options;
}

/** 侧边栏渲染辅助：语义名 → 渲染函数（供 menu-item icon 槽使用） */
export function renderMenuIcon(name?: string) {
  const icon = resolveMenuIcon(name);
  return icon ? () => h(icon) : undefined;
}
