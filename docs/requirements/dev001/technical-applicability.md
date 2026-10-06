# DEV001 技术适用性与验证边界

> 状态说明（2026-10-06）：重建已暂停，等待基线仓库就绪。本文提到的源码、测试、构建配置、hooks、CI、任务卡与证据已从本分支移除，清空前状态见 tag `archive/pmm-rebuild-pre-clear-20261006`。本文仅作为设计输入保留。

当前唯一实测平台为 Linux x86_64 / Bash 5 / Python 3.12。完整结果在任务状态与候选绑定证据中记录；下表不把目标技术写成业务验收。

| 维度 | 本卡实际边界 | 尚未验收的后续事项 |
| --- | --- | --- |
| 语言/运行时 | global.json 固定 .NET SDK 10.0.401，C#14/net10.0；当前运行时10.0.12 | 其他SDK升级和平台需另审 |
| Web宿主 | 单一 Kestrel，只有loopback惯性Razor及工程health | 业务MVC/HTTP/MCP与共享应用规则 |
| 前端 | 编译的本地Razor和页内样式，无CDN、表单或交互业务 | Bootstrap5/jQuery/Axios本地打包及视觉/交互验收 |
| 数据库 | 仅新唯一临时目录中的真实SQLite基础测试 | 产品库、恢复、迁移及并发行为；旧库永禁 |
| 数据访问 | 测试内EFCore.Sqlite10.0.12 Code First synthetic实体 | 产品实体、键、索引、事务和迁移 |
| 后台任务 | 无应用worker/队列启动，仅框架Web宿主服务 | 持久HostedServices、取消、重试、恢复 |
| 缓存/消息 | 不引入Redis、外部broker或网络cache | 没有新增中间件计划 |
| 测试 | 全新xUnit、临时SQLite/文件系统及实际Linux进程/路由/套接字检查 | 业务合同、Playwright与跨平台行为 |
| 托管/CI | 仅独立dot stage，精确版本CI；job级禁自动证书 | main合并、发布、部署均另行授权 |
| 部署/网络 | 无产品部署、账户/凭据、外部业务连接；包只从官方NuGet恢复 | TMDB、可选AI、真实媒体/服务不在本卡 |
| AI/平台/shell | 实际作者/测试作者/独审分离，未知模型为null；Linux/Bash | Windows、macOS、PowerShell7均not-run |

English: This is a Linux-only engineering foundation. Business/UI/cross-platform acceptance is not claimed.
