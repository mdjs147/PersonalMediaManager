# DEV001 技术适用性与验证边界

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

## 依赖与可复现构建

根 Directory.Build.props / Directory.Packages.props 集中配置；三个项目均提交 packages.lock.json。主类库与Web无第三方业务包，SQLite和xUnit仅由测试引用。锁定恢复固定官方 `https://api.nuget.org/v3/index.json`，不引用旧项目输出或旧测试。

所有本地命令显式 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`，CI继承相同job级设置。当前云构建使用 `DOTNET_PROCESSOR_COUNT=1`、`DOTNET_CLI_USE_MSBUILD_SERVER=0`、`MSBUILDDISABLENODEREUSE=1`，项目关闭共享编译；这是进程内构建条件，不减少项目、测试、失败传播或验收门禁。此前默认并行恢复的失败原记录保留，不计为通过。

## 九项通用控制与后续触发

| 控制 | 当前实现 | 再次触发条件 |
| --- | --- | --- |
| 分支/ownership | 已发布DEV001 state、独立任务clone与first-write/recover | 新作者/工作区/任务或恢复 |
| readiness | 当前环境、配置清单、独立context审查 | SDK、环境、源配置、依赖锁变化 |
| verification | 统一verify入口、真实locked restore/build/nonempty tests/inert check | 受影响改动；最终集成全量 |
| independent review | 不同实际执行审查state/context和精确candidate | 新候选、上下文或阻断修正 |
| task tracking | 冻结卡、219项planned追踪、真实DEV001状态 | 状态/范围/验收变化；扩范围先发卡 |
| documentation | 当前README、需求、架构及测试风险说明 | 行为、技术适用性或限制变化 |
| secret handling | 公开树检查与独立语义审查；原始日志本地保全 | 每次拟公开的新内容 |
| trigger coverage | 注册hooks、CI与已接受禁证书回归 | 调用路径、CI或构建方式变化 |
| baseline sync | 固定23a7748资产及更新发现 | 每次写入会话；不可访问如实说明 |

每个测试对应明确风险；小修只跑受影响测试，最终集成节点执行完整检查。Linux过程套接字采样不证明所有时间点都无网络；完整端点/注册集由独立xUnit和代码审查补充。新增空目录、文件、cookie或密钥均应被实际宿主检查捕获。生产访问策略和权限尚未实施，不能从本卡默认取值推导批准。

English: This is a Linux-only engineering foundation. Exact pinned dependencies, single-process local build conditions and all nine generic controls remain explicit. Business/UI/cross-platform acceptance is not claimed. Focused changes receive focused risk-based checks; final integration receives the complete applicable verification.
