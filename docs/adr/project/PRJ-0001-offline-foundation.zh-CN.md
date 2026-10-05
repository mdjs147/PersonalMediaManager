# PRJ-0001：全新离线工程基座

状态：DEV001范围内工程决定，业务策略未定；实现验收另见任务状态。

## 决定与原因

以已发布重置需求为输入从零建立三个项目：Foundation持有极小配置解析/新临时测试空间；Web为唯一惯性Kestrel宿主；foundation tests独立持有synthetic EF实体。Web不持有数据库、业务存储或网络集成。此分隔支持对真实启动边界单独验收，同时不提前实现未审的领域字段、权限或作业机制。

使用 CreateEmptyBuilder，固定Production且不加入默认环境/JSON/user-secret配置源。启动输入仅为可选 `--port` 的ASCII整数0–65535，地址固定127.0.0.1。GET/HEAD `/` 和 `/health` 是完整路由白名单。默认动态端口用于隔离测试，不能推导未来LAN/远程访问策略。

MVC的AddViews会附带Data Protection。移除其服务/密钥预热，再提供明确拒绝Protect/Unprotect的占位边界；不生成密钥、不实现认证，也不以弱密钥/明文代替保护。后续安全入口必须另卡设计，不能复用这个拒绝器实现生产认证。

临时测试空间只能由Create产生唯一新目录，SQLite路径在其内；host不调用该工厂。Dispose先检查全树链接，发现链接时拒绝并保留测试证据。其合同要求单所有者、不并发改动且不更换路径；不保证抵抗竞态、同路径普通目录替换或恶意进程。真实媒体与生产清理不适用。

## 验收与限制

构建使用明确SDK、中央包版本及每项目lock；新测试验证参数边界、完整路由/注册集、禁密钥、SQLite隔离、链接拒绝及真实进程启动/关闭和运行目录不变。只在Linux实际执行；其他平台不声称通过。所有219项业务能力仍planned/not-run。

## 依据

- 已发布 DEV001 与 rebuild-mapping.json 的已确认方向/提案/未定策略分离
- [.NET CreateEmptyBuilder](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.builder.webapplication.createemptybuilder?view=aspnetcore-10.0)
- [ASP.NET Core MVC视图注册](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Mvc/Mvc.ViewFeatures/src/DependencyInjection/MvcViewFeaturesMvcCoreBuilderExtensions.cs)

English: Build a fresh three-project engineering foundation with one read-only loopback host. Data protection operations fail closed, and temporary data helpers are test-only, single-owner utilities. No business schema, authorization policy, production cleanup or old acceptance is inherited.
