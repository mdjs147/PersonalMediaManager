# DEV001 全新工程基座需求与边界

> 状态说明（2026-10-06）：重建已暂停，等待基线仓库就绪。本文提到的源码、测试、构建配置、hooks、CI、任务卡与证据已从本分支移除，清空前状态见 tag `archive/pmm-rebuild-pre-clear-20261006`。本文仅作为设计输入保留。

中文为本卡需求解释的权威稿。来源是已发布的[重置清单](../rebuild-mapping.json)，不读取或复用旧产品源码、测试、数据库、备份、通过数或完成状态。

## 三类设计输入

- 9项已确认产品结果/技术方向保留，逐项见[追踪清单](traceability.json)的 design_inputs.confirmed；确认结果不等于字段、接口或算法已获批准
- 6项工程提案保留为 proposal，包括共享应用层、计划/批准/journal拆分、持久队列/lease、幂等重验、校验保全及最小能力授权；本卡不将这些提案变成已实现业务机制
- 15项产品策略 D01–D15 仍未定，包括 Move/Copy、冲突及保全期限、账户和远程访问、平台顺序、目录复用、NFO、AI预算、完成度分母、清理、恢复、更新、通知及设置消费者；测试施工取值不替用户决定这些策略

## 当前限定能力

建立 .NET 10/C#14 的 Foundation 类库、一个 Web 宿主及全新基础测试工程。宿主只在 IPv4 loopback 监听，默认动态端口；只有 GET/HEAD 的 `/` 惯性 Razor 页面和 `/health` 工程健康响应。健康响应不是 `system.status` 等业务能力的实现。

不注册业务 HTTP/MCP 操作、账户/授权入口、任务 worker、网络服务客户端、数据库服务或真实媒体路径。MVC 视图间接注册的 Data Protection 密钥管理及预热服务被移除；保护/解密操作明确拒绝，不能用于生产认证。不会创建或信任开发证书。

临时工作区只能新建唯一的空测试目录，不能选择或复用旧目录；EF Core 实体仅存在于测试中，只使用该测试目录内新建 SQLite，禁连接池并先释放连接再释放目录。清理会拒绝已观察到的链接/reparse point；这是单所有者、禁止外部更换路径的测试辅助，绝不是生产媒体清理、并发防护或抗路径替换安全机制。

## 219项能力仍未开始

[逐能力追踪](traceability.json)逐一保留全部219个ID、分类和操作种类；每项为 planned / not-run，旧验收一律不继承。共享 transport 约束适用于每项：UI、HTTP、MCP应共享领域/应用规则和授权判断，拒绝、冲突、失效、取消与部分失败的含义必须一致。当前没有这些业务规则的已实现对象，不以工程健康页或治理测试冒充验收。

## 下一张小范围实现卡的进入条件

1. 本卡真实构建、锁定恢复、基础行为测试、独立审查和独立stage CI完成
2. 从219项清单中明确选择一个有界能力/用例，列具体共享规则、状态变化、权限及失败语义
3. 只解决该能力必需的未定策略；不能默认为所有 D01–D15 已决定
4. 先发布其卡和文件ownership，再独立审核依赖/环境/新配置并激活；各实际作者通过自己的开工检查
5. 只用新隔离数据证明该能力，其他能力仍planned；跨平台证据分别取得

## English summary

The nine confirmed outcomes, six engineering proposals and fifteen unresolved policies remain distinct. All 219 business capabilities stay planned/not-run, with no inherited acceptance. This card creates only a fresh inert loopback Razor/health host and temporary-data engineering tests. UI/HTTP/MCP business rules, permissions and failure parity require later bounded reviewed cards. The temporary workspace is a single-owner test helper, not a production or race-resistant cleanup boundary.
