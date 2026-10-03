# AI 完整诊断（0.5.0）

## 启用与读取

设置 → AI 提供商 → AI 诊断日志，选择 **Full** 后保存。默认仍为 **Standard**；Detailed 仍是受限正文摘要，不代表完整日志。Full 仅影响之后采集的事件，不能补回过去没保存的内容；切换时已经在运行的请求可能只保存部分阶段。排查结束可恢复 Standard；已保存证据按保留策略轮转。

仅管理员可以 GET/PUT `/api/diagnostics/parse/settings`，或 GET `/api/diagnostics/parse/export?mediaItemId=123`（也可使用 runId 或 scanRunId，三者仅选一个）。页面提供按媒体项 ID 导出。级别单独持久保存在应用私有诊断目录，无需数据库迁移；保存失败不会宣称成功。API 和目录权限不会因为选择 Full 而放宽。

## 各阶段独立保存

- 原始解析请求及完整上下文；若构造提示时因上下文预算删减，原输入仍单独保留，实际提示及预算元数据另外记录
- 准备好的 system/user 提示，标记 `prepared_not_yet_sent`；不能把因配额、取消或其它错误未发出的内容说成服务商已收到
- 五个外部协议以及本地模型在组包后、配额预占前保存实际序列化 request content，并标记 prepared；不读取认证头或 URL。大正文采集结束后重新检查取消，再预占并启动 HTTP；只有 SendAsync 已启动才记录轻量 dispatch，不能证明远端收到
- HTTP 实收正文/envelope：在 JSON 反解前保存，包含公开响应字段、使用量、结束标记、未知扩展等。与从 envelope 提取的 assistant content 是两个不同证据，绝不根据抽取结果“还原”原包
- 清理中实际使用的文本：去 Markdown、严格整段 JSON 解析（没有虚构的 JSON 子串恢复）、批量逐 ID 提取、schema/锁定字段过滤、数值范围及来源年份守护、领域校验与拒绝理由
- 最终结构化结果独立保存；人工修正、模型输出、程序接受结果不能混成同一份正文

协议目前请求 `stream=false`。HTTP 传输按块读取也保留实收 UTF-8 正文；取消、读取异常、字节上限或非法 UTF-8 明确说明不完整。对未来 SSE/NDJSON 模型协议不声称支持重组或捕获完整流式语义。本地模型的 HTTP 失败已确定后，错误正文只使用独立 50 ms 诊断读取预算；超限或读取失败明确标记，保留原有 http_error，调用方取消仍传播。

## 原格式和隐私边界

所有正文先经过专用脱敏，再落盘。有效 JSON 只替换敏感 token 的原始字节区间，其余空白、换行、数字写法、属性次序不被格式化。安全的纯文本原样保留；日志只存在脱敏后版本，不保存脱敏前副本。

密钥（包括本次端点密钥的精确匹配）、认证、Cookie、带认证/查询参数的网址、主机绝对路径字段（FullPath/WatchRoot 等）、以及推理/thinking 私有字段会替换为明确省略标记。传给 AI 的相对媒体上下文独立保留。Anthropic thinking 块和 Gemini thought 部分整体省略。无法安全处理的敏感坏 JSON 不保存正文，状态为 `not_recorded`，reason 为 `privacy_filter_failed`。这些省略均不能称为字节完全相同的原始响应。

正文描述符包括 artifactId、state、reason、boundary、redacted、formatPreserved、truncated、observedUtf8Bytes、originalUtf8Bytes（脱敏后完整字节数）、capturedUtf8Bytes 和 SHA-256（只对脱敏后内容计算）。传输事件还含观察到的字节数及声明的 Content-Length；未读完时不把这些数当成完整正文长度。

## 存储、关联与完整性

Full 正文位于独立私有 UTF-8 artifact，不放 SQLite，也不把大正文塞进 JSONL 单事件。默认每段 2 MiB、正文总量 64 MiB、256 段、7 天；原 JSONL 默认 32 MiB/16 文件不变。容量可经 `ParseDiagnostics` 配置限额调整，均有硬上限。每阶段单独生成 artifact；一个批次的共享原包只保存一份，各项事件引用同一 artifact，不重复计算为多个物理请求。

每个物理请求有 requestId，批量另有 batchId/itemId，并与各媒体原有 runId/scanRunId/重试 attempt 关联。单项回退新建 requestId，沿用批次和项关联；其它项不会因为诊断失败被重发。

单正文超过限额时整段不保存，并明确 `artifact_size_limit`；不会悄悄截成前缀。运输层本身截断或取消时，实际已经收到的前缀可以保存，但明确标记 `truncated=true`、部分边界和原因。磁盘失败只产生缺失状态/失败计数，不更改媒体结果、取消、配额或重试次数。

导出包含 events 与独立 artifacts，并验证文件长度、SHA-256、UTF-8 与隐私边界。过期/已轮转、缺文件、完整性损坏、读取失败、导出超限分别标记。每次导出都有 completeness；`completePipelineReplay` 始终为 false，不将本地保留窗口伪称完整业务重放。

轮转只删除应用自己命名的诊断 event/artifact，拒绝符号链接，不清理媒体原件、模型、源码、密钥或其它用户资料。Unix 新目录 0700、新文件 0600；Windows 依赖应用数据目录的继承 ACL，不声称实现额外 ACL 加固。
