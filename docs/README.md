# TaoAdmin 项目文档索引

本目录存放项目全套文档，建议按以下顺序阅读：

| 文件 | 内容 | 适合谁 |
|---|---|---|
| [01-Requirements.md](01-Requirements.md) | 项目背景、功能需求（FR-100~1300）、非功能需求 | 全员 |
| [02-Mindmap.html](02-Mindmap.html) | 项目全貌思维导图（浏览器直接打开） | 全员，快速了解全貌 |
| [03-Architecture.html](03-Architecture.html) | 分层架构：客户端→网关→服务→中间件（浏览器直接打开） | 开发/架构/运维 |
| [04-UserGuide.md](04-UserGuide.md) | 各功能模块操作说明（含多租户/工作流/消息队列） | 使用者/管理员 |
| [05-DevOpsGuide.md](05-DevOpsGuide.md) | 环境搭建、配置清单、部署、日常运维（含 RabbitMQ/限流/熔断/幂等） | 实施/运维 |

> 配置文件的逐项中文注释（含本地/线上示例）在各服务 `appsettings.json`、
> 前端 `vite.config.ts` / `nginx.conf`，以及 `deploy/docker-compose.yml` 中。
