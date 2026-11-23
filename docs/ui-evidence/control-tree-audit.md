# 界面实测证据班：控件树全量枚举与可达占比审计

> 班次：ui_evidence_real（TaoAdmin 线，release/v24）
> 日期：2026-11-23
> 方法：Chromium 真实浏览器逐页导航，DOM 枚举可见 button/input/select/switch/radio/checkbox（含 disabled/checked 状态），脚本 `enumerate_controls.py`，逐页 JSON 落档 `controls/<page>.json`。

## 一、全量枚举结果（21 页 / 322 控件）

| 页面 | 路由 | 控件数 | disabled | 类型分布 |
|---|---|---|---|---|
| dashboard | /dashboard | 4 | 0 | button×4 |
| codegen | /system/codegen | 7 | 0 | button×6, input×1 |
| loginlog | /system/loginlog | 13 | 2 | button×6, input×4, checkbox×2, select×1 |
| file | /file | 14 | 2 | button×7, input×4, checkbox×2, select×1 |
| auditlog | /system/auditlog | 16 | 2 | button×7, input×5, checkbox×2, select×2 |
| notifychannel | /system/notifychannel | 15 | 0 | button×8, input×7 |
| loginsecurity | /system/loginsecurity | 13 | 0 | button×7, input×4, switch×2 |
| region | /system/region | 8 | 0 | button×7, input×1 |
| monitor-resource | /monitor/resource | 8 | 1 | button×8 |
| monitor-online | /monitor/online | 13 | 0 | button×5, checkbox×8 |
| monitor-service | /monitor/service | 5 | 0 | button×5 |
| monitor-log | /monitor/log | 17 | 1 | button×10, input×5, select×2 |
| monitor-apidocs | /monitor/apidocs | 5 | 0 | button×5 |
| job | /task/job | 8 | 2 | button×6, checkbox×2 |
| user | /system/user | 28 | 2 | button×13, input×2, checkbox×12, select×1 |
| role | /system/role | 20 | 2 | button×8, checkbox×10, input×1, select×1 |
| menu | /system/menu | 66 | 0 | button×66（树表逐行操作按钮） |
| api | /system/api | 34 | 1 | button×9, checkbox×22, input×2, select×1 |
| siteconfig | /system/siteconfig | 9 | 0 | button×7, input×2 |
| announcement | /system/announcement | 10 | 2 | button×7, input×1, checkbox×2 |
| pwdpolicy | /system/pwdpolicy | 9 | 0 | button×6, input×3 |

**合计：322 控件，disabled 17 个，真人可达（可见且可用）占比 94.7%。**
disabled 控件均为业务态合理禁用（未勾选行时的批量操作按钮、无数据时的操作项），未发现无理由禁用。

## 二、实测发现并修复的缺陷

### 缺陷 1：资源监控页 404（已修复，commit 84692e0）
- 现象：菜单「资源监控」（/monitor/resource，Component=monitor/resource/index）点击后渲染 404「抱歉，您访问的页面不存在」。
- 根因：`frontend/src/views/monitor/resource/` 目录整个缺失，路由动态映射（`import.meta.glob('@/views/**/*.vue')`）找不到组件回退 404 页。后端接口（/monitor/resources/current|history|thresholds|alerts）与权限码（monitor:resource:list/manage）齐备，纯前端缺页。
- 修复：新增 `resource/index.vue`（服务资源卡片 + 阈值越限标红 + 告警事件表 + 阈值设置弹窗 + 30s 自动刷新）；`api/monitor.ts` 补 5 个接口封装，并对后端数值字段字符串序列化做归一防御（实测后端返回 "44.84" 字符串，不归一会致 `.toFixed()` 抛错、卡片渲染中断——修复过程中真实踩到并消除）。
- 验证：vue-tsc 零错；vitest 30/30 绿；浏览器实测渲染正常（identity 正常 / gateway 内存 85.3% 超阈值告警标红、真实告警事件入表、阈值弹窗 3 输入框可开）。证据：`page-monitor-resource-fixed.png`。

## 三、三态证据

| 状态 | 证据文件 | 说明 |
|---|---|---|
| 空态 | `page-codegen.png` | 代码生成页空表「暂无数据」 |
| 空态 | `state-file-recycle-empty.png` | 文件回收站空态「暂无数据」 |
| 报错态 | `state-404-error.png` | 无效路由 404 页（返回首页/上一页按钮齐全） |
| 报错态 | `page-monitor-resource.png` | 修复前资源监控 404（缺陷 1 原始证据） |
| 加载态 | `page-monitor-resource-fixed.png` | a-spin 加载包裹 + 数据渲染完成态 |

## 四、环境备注（非前端缺陷）

- 文件中心服务（localhost:5003）本环境未部署，文件页出现「连不上文件中心」报错提示——提示文案与交互正常，属环境限制。
- 沙箱内存紧张（4GB 中仅剩约 200MB），Chromium 反复被 OOM 杀掉，用重启法兜底完成全部取证；枚举脚本可重跑（`python3 enumerate_controls.py <page...>`）。

## 五、断点与移交

- 本班五张对账表三处挂账（控件树全量枚举、视觉走查出图、真人触控/控件可达占比）已全部补齐：21 页截图 + 21 页控件 JSON + 本审计文档，均已 commit 并 push 远端核验。
- 无遗留挂账。
