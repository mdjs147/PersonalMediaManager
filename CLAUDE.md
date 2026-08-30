# 项目开发指引

## 一、协作方式
- 先思考再动手；写代码前先读现有文件，避免重复读已读文件。
- 输出简洁，推理彻底；优先编辑而非整文件重写。
- 单文件超过 100KB 不主动读取，除非明确需要。
- 长会话建议运行 `/cost` 监控缓存命中率；切换无关任务时另起会话。
- **优先**用多 agent（subagent）/ Workflow 编排：任务可拆分 / 可并行（多文件、多维度、扇出搜索/审查/研究、批量改写）就**默认首选**并行，别单线程顺序硬做。只读扇出随意并行；**写改动也默认自动并行、无需先问**——不相交文件按文件集拆分，同改一组文件或涉 git 操作用 `isolation:'worktree'` 隔离后汇总回主树。Workflow 工具仍守 harness 的显式 opt-in（该轮未 opt-in 时主动提议）。**此并行仅指 Claude 干活方式，与 §八产品代码「串行/禁并发」红线无关（后者仍有效）。**
- 子代理选模**默认 Opus 4.8（1M），拿不准不降档**；省略 model 即继承会话顶配模型，等效。仅极简机械活（grep、改名、列文件、短摘要）才显式降 Sonnet/Haiku 省成本。把关：模型 ID 须真实存在（勿臆造）、上下文密集任务认准 `[1m]` 后缀；顶配不可用逐级降级，**Sonnet 4.6 兜底**。
- 写完代码必须自测后再交付。
- 不写客套开场白与收尾废话，方案保持简单直接。
- 用户当次指令始终覆盖本文件。

## 二、语言与文档偏好
- 说明性内容、代码注释、日志、错误/异常文案一律使用中文。
- 文档（docx/md）中文字体优先「宋体」。

## 三、环境与技术栈
- 操作系统：Windows 11
- IDE：Visual Studio
- 技术栈：.NET 10、Vue 3、JavaScript、SQLite、EF

## 四、目录结构约定
- `/src` — 项目代码（7 个 .NET 项目 + Frontend `.esproj`，矩阵见 §十二）
- `/tests` — 测试项目（6 个，见 §十二）
- `/docs` — 设计/规范/需求/开发计划文档（仅存现行有效文档）
- `/db` — 数据库脚本目录
- `/scripts` — 自动化脚本（红线扫描 / agent 注册 / 本地构建）
- `/old` — 过期/废弃文件与已闭环的历史审计文档，**不主动读取**

## 五、代码注释规范

### 通用规则
- 所有注释**中文**。
- `<summary>` 仅写 **1 行超短描述**（IntelliSense 悬停展示）。
- `<remarks>` 写详细内容，与 `<summary>` 严格分离。
- 类与接口 `<summary>` 须精简，详情放 `<remarks>`。

### Controller / API 注释（Scalar UI 规范）
- `<summary>` **必须单行**，**总长度 ≤ 30 字符**。
  - 正例：`<summary>List users / 查询用户</summary>`
  - 反例：多行 summary（Scalar 会拼成超长标题）。
- `<remarks>` 固定结构（按序）：
  1. 请求体 JSON 示例（POST/PUT）。
  2. 成功响应 JSON（必须含 `requestId` 字段）。
  3. 错误码 bullet 列表。
  4. 单条通用错误响应 JSON（必须含 `requestId`）。
- `<response>` 标签必须与 `[ProducesResponseType]` 一一对应。
- 错误响应 JSON 必须包含完整字段：`code`、`message`、`data`、`requestId`。

## 六、API 响应 code（极简 3 码原则）
- `0` Success — 业务成功。
- `1000` BusinessError — 通用业务失败（参数/不存在/冲突/校验/超时/幂等重复全部归此码）。
- `9000` ServerError — 服务器或基础设施错误（DB/缓存/MQ/外部服务/未预期）。
> **最小 code 原则**：同类失败用同一 code + 不同 message；只有前端行为不同才新增 code。

## 七、命名规范（强制）

### 模块前缀枚举（PMM 实际业务域，全栈贯穿）

| 前缀 | 含义 |
|---|---|
| `System_` | 系统级配置（端口、阈值、操作模式） |
| `User_` | 账号与会话 |
| `Watch_` | 文件监控（监控目录、忽略规则） |
| `Parse_` | 媒体名称解析（规则引擎、AI 提供商） |
| `Tmdb_` | TMDB 配置与缓存 |
| `Category_` | 媒体分类（定义、匹配规则） |
| `Media_` | 媒体处理记录（业务主表） |
| `Process_` | 媒体处理过程时间线（步骤明细，如 `Process_Step`） |
| `Subtitle_` | 字幕下载（字幕源配置、下载记录） |
| `Webhook_` | Webhook 订阅与出站日志 |
| `Audit_` | 操作审计、外部调用统计 |

新增业务域时按本表风格扩展；不要把新表硬塞进已有前缀。

### 缩写大小写（强制 PascalCase）
- 缩写无论几字母一律 PascalCase：`Tmdb` / `Ai` / `Api` / `Url` / `Id` / `Json` / `Http`
- **禁止**全大写：`TMDB` / `AI` / `API` / `URL` / `JSON`（影响 IntelliSense 排序与可读性）
- 反例修正：`IAIProvider` → `IAiProvider`；`TMDBClient` → `TmdbClient`

### 数据库表名 — Pascal_Pascal 严格两段
- 形式 `Module_Entity`，**实体段一律单数**（例：`User_Account`、`Media_Item`）。
- 多词实体驼峰合并到第二段（例：`Watch_IgnoreRule`、`Parse_AiProvider`）。
- 单段表必须补前缀。

### C# 实体类
- 类名 = 表名去下划线 PascalCase（`User_Account` ↔ `UserAccount`），**一律单数**。
- 文件名 = 类名 + `.cs`。
- `[Table("User_Account")]` 与 `entity.ToTable("User_Account")` 必须一致。
- **DbSet 属性名保留复数**（与集合语义一致，便于 LINQ 阅读）：泛型类型用单数实体名，属性名用复数。

  ```csharp
  public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
  public DbSet<MediaItem>   MediaItems   => Set<MediaItem>();
  ```

### 类后缀约定
| 角色 | 后缀 | 示例 |
|---|---|---|
| 控制器 | `Controller` | `AuthController` |
| 应用服务（编排业务） | `Service` | `ParseTaskService` |
| 充血聚合根 / 实体行为方法所在类 | 无后缀（实体名本身） | `ParseTask` / `AiCallChain` |
| 仓储（仅在需要 Mock 时抽接口） | `Repository` / `IxxxRepository` | `MediaItemRepository` |
| 外部依赖契约 | `IxxxProvider` / `IxxxClient` | `IAiProvider` / `ITmdbClient` |
| 配置 POCO | `Options` | `TmdbOptions` / `AiOptions` |
| ASP.NET 中间件 | `Middleware` | `RequestIdMiddleware` |
| SignalR Hub | `Hub` | `LogHub` / `TaskHub` |
| 后台服务 | `Worker` / `HostedService` | `FileWatcherWorker` |
| 异常类 | `Exception` | `BusinessException` |
| 拦截器 | `Interceptor` | `TimestampInterceptor` |
| DTO（入参/出参） | `Request` / `Response` / `Dto` / `Result` / `Query` | `LoginRequest` / `UserResponse` / `RescanResult` / `HistoryListQuery` |

> **DTO 后缀语义补充**（避免歧义）：
> - `Request` — POST/PUT 请求体（命令意图）
> - `Response` — 服务返回的复合对象（含主数据 + 元数据）
> - `Dto` — 跨层透传的中间结构（非典型请求/响应）
> - `Result` — 单次操作的返回（如 `RescanResult` 重投返回 `(Id, Status)`），与「事件级响应」相比更短促
> - `Query` — GET 查询条件对象（如 `HistoryListQuery` 带分页/过滤），区别于 `Request`（写操作）

## 八、红线规则（强制禁忌）

### EF Core / DbContext 并发
- **禁止**在同一 `DbContext` 实例上 `Task.WhenAll` 并发多个 EF 查询。
- 需要并发查询时：注入 `IDbContextFactory<TContext>`，每个并发分支创建独立 DbContext。

### 批量改名 / BCL 类型保护
- **禁止**对 BCL / 第三方类型参与批量改名（如 `Android.Manifest.Permission`、`System.Security.Permissions`、`Microsoft.AspNetCore.Authorization.*`、`Java.*` 等）。
- 替换策略：用 `\b` 单词边界 + sentinel 占位保护已替换的新名，避免「先改的字符串被后改的规则二次替换」；遇到 `Foo.OldName` 需结合上下文区分 BCL 与项目类型。

### Windows 文件系统协作
- **禁止**用 Linux `sed -i` 修改 Windows 文件：会静默截断。改用 Edit 工具或 PowerShell。
- 挂载目录 listdir 缓存可能 stale：验证文件存在/状态用 `stat`，不要依赖 `ls`。

### Git
- **允许使用 git worktree**（原「禁用」红线已解除）：含 Claude 多 agent / Workflow 自动创建的临时 worktree 隔离，手动 worktree 同样放开。注意 VS 同一时刻只跟踪主工作树，手动开 worktree 时自行留意当前在哪棵树/哪个分支，避免与 VS 已打开分支混淆。
- **允许 GitHub 镜像远端**：与 §9.2 / §9.5 联动 —— origin 同时配置 Azure DevOps Server（主，fetch + push）与 GitHub（push-only 镜像），单条 `git push origin` 自动双推。其它远端（GitLab / Gitee / `dev.azure.com/*` 等云端 Services）仍禁止配置。

### Launcher 托盘库
- **禁止**在 `PersonalMediaManager.Launcher` 引入跨平台 UI 框架：`Eto.Forms` / `Avalonia` / `Microsoft.WindowsAppSDK` / `Microsoft.Maui.*`。
- 托盘锁定 **Windows 原生**：**内置 `System.Windows.Forms.NotifyIcon`**（`<UseWindowsForms>true</UseWindowsForms>` 启用，.NET SDK 自带，零外部包）。
- **Why**：NuGet 上 `H.NotifyIcon` **没有** WinForms 变体（仅 base/WPF/WinUI/Uno/MAUI），内置 NotifyIcon 即开即用零包风险（WinForms 是 .NET 自带的 Windows 原生框架，不在禁止范围）；Launcher 仅需托盘 + 菜单 + 浏览器跳转（交互全在 WebUI），OS 原生 API 最稳、产物最小，引大型 UI 框架是把版本风险捆绑进项目；macOS 支持已彻底移除（不在路线图），`IPlatformTray` / `IPlatformAutoStart` / `IPlatformSingleInstance` 三接口保留仅为**隔离 OS API 便于单测**，非为回填。
- **How to apply**：`Launcher.csproj` 走单 TFM `net10.0-windows`。任何「加个跨平台 UI 框架更省事」的建议都不要给。

## 九、Git 工作流与 CI/CD

### 9.1 Git 工作流
每个任务流程：
1. 开始前：`git checkout -b feature/任务简述` 在主目录创建新分支。
2. 完成并测试通过后，自动执行：
   - `git add -A`
   - `git commit -m "中文说明"`
   - `git checkout main`
   - `git merge 刚才的分支名`
   - `git branch -d 刚才的分支名`
   - `git push origin main`（origin 配了多 push URL，**一推同时落 Azure DevOps + GitHub 镜像**，详见 §9.2 / §9.5）
3. 用中文汇报每步结果。

> **推送 ≠ 发版（强制护栏）**：§9.1 永远**止于 push main**。push 后 GitHub 镜像只触发 main 开发构建 artifact（§9.3.2，7 天留存、不进 Releases、不影响升级检查），**绝不自动打 tag、不自动发版**。
> 发版（打 `v*` tag → GitHub Release）是**独立动作**，**仅当满足下列任一条件**才执行：
> 1. 本次变更就是要升 `PmmProductVersion`（主版本号）；
> 2. 用户**主动**提出要发版。
>
> 两者都不满足时——哪怕本轮攒了多个功能、按 §version 升了 `VersionPrefix` / `PmmDbVersion` / `FrontendVersion` 子版本号——也**只 push 不发版**，更不要主动提议发版（子版本号照常每提交升，主版本号一直留到发版那一刻才动）。

### 9.2 源码托管 = GitHub 公开主仓 + Azure DevOps Server 内网镜像
- **公开事实源**：`https://github.com/mdjs147/PersonalMediaManager.git`，承载公开源码、Issue、PR、Actions 与 Releases；公开开发分支必须从 GitHub `main` 创建。
- **内网兼容面**：Azure DevOps Server（On-Premises，非 `dev.azure.com` 云 SaaS）可保留为团队镜像与 Azure Pipelines 执行面；地址实值只在 gitignored `CLAUDE.local.md`。
- **远端边界**：公开工作区的 `origin` 指向 GitHub；如需同步内网，使用独立命名的 Azure remote 并显式 push，不得再配置一条命令自动双推，避免把私有历史或内网分支误送公网。
- **禁止**配置 GitLab / Gitee / `dev.azure.com/*` 等未经批准的远端，也禁止把 Azure 内网 URL、Collection 实值或凭据写入 tracked 文件。

### 9.3 CI/CD = Azure Pipelines（内网 PR / 主 CI）+ GitHub Actions（公开 PR CI + 发版打包）

**两套平台均有明确边界**：内网 Azure Pipelines 保留团队 PR / 主 CI；公开 GitHub 通过 `pr-ci.yml` 对外部 PR 跑同等 build/test/红线/schema 门禁，`release.yml` 只生成 Windows 安装包（main push 入 Artifact、tag push 入 Releases）。

#### 9.3.1 Azure Pipelines（PR / 主 CI）—— 跑在 self-hosted agent
- 管道文件：
  - 主管道：仓库根 `azure-pipelines.yml`（PR + push to main 触发：build → test → redline-scan）。
  - **可选**：`azure-pipelines/nightly.yml`（cron 触发：长跑测试 / 集成 smoke）。
- **不**承担发版构建：发版打包与开发构建均由下面的 GitHub Actions 处理。
- **Agent**：self-hosted **必然**（Server SKU **无** Microsoft-hosted agent 选项）。仅 Windows agent 单矩阵（team 既有），PR Pipeline 按 Windows 单 agent 编排。
- **测试任务**统一写法：
  ```
  dotnet test --no-build -c Release `
    --logger "trx;LogFileName=test-results.trx" `
    --collect:"XPlat Code Coverage" `
    --results-directory $(Agent.TempDirectory)/TestResults
  ```
  随后用 `PublishTestResults@2`（trx）+ `PublishCodeCoverageResults@2`（cobertura）任务上传。
  > `@2` 需 Azure DevOps Server **2022+**；若部署版本较老，降到 `@1` 即可——**以 Server 实际安装的 task 版本为准**。
- **凭据**：Pipeline Variables / Variable Groups（标记 secret）。Server SKU **无** Azure Key Vault 集成，**唯一**走 Variable Group；禁止入仓库、禁止入 `appsettings*.json`、禁止写进 yaml 明文。
- **CLI 配置**（`az devops`）：organization 字段**必须带 collection 路径**：
  ```
  az devops configure --defaults `
    organization=http://<azure-server>/<collection> `
    project=<project>
  ```
  **不要**写成 `https://dev.azure.com/...`（那是 Services 云端，本项目不适用）。

#### 9.3.2 GitHub Actions（公开 PR CI + 发版打包）—— 跑在 GitHub-hosted runner
- **允许的管道文件仅两份**：
  - `.github/workflows/pr-ci.yml`：仅 `pull_request` → `main`，只读权限，跑 build/test/红线/schema drift；固定墙钟性能基线 `Category=Performance` 仅留在受控 Azure self-hosted agent。
  - `.github/workflows/release.yml`：仅 `push` tag `v*` 与 `push` `main`，负责开发 Artifact 与正式 Release。
- **禁止**在上述文件加入 `workflow_dispatch` / `schedule` / `repository_dispatch`，也禁止新增第三份 workflow；公开 PR CI 不得读取 Secrets 或使用 `pull_request_target`。
- **产物去向二分**（在同一 yaml 内用 `if` 守门实现）：
  - **tag push（vX.Y.Z）** → 创建 / 复用 GitHub Release + 上传单文件 exe（**正式发版**，「客户端检查更新」端点会读到）
  - **main push** → 仅 `actions/upload-artifact@v4` 保留 7 天（**开发构建**，仅 Actions Run 页可下载，**不**进 Releases 页、**不**影响升级检查）
- **职责仅限**：
  1. checkout + setup-dotnet 10.x + setup-node 22
  2. `npm ci`（前端依赖；vite build 由 `.esproj` 随 dotnet publish 自动触发）
  3. `dotnet publish src/PersonalMediaManager.Launcher -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true` → 前端 + appsettings 嵌入的单文件 exe
  4. 校验 publish 输出确为单一 exe（多余文件 = 单文件收敛被破坏，直接 fail）+ 重命名为 `PersonalMediaManager-{ver}-win-x64.exe`，**不再打 zip**
  5. tag push 走 `gh release create` + `gh release upload`；main push 走 `actions/upload-artifact@v4`（用 `secrets.GITHUB_TOKEN`，**不需要 PAT**）
- **版本号校验守门**：`PmmProductVersion` 与 tag 一致性校验仅在 tag push 时执行（main push 跳过，避免重复 commit 误报版本漂移）。
- `release.yml` **不承担**单元测试 / 红线扫描；这些由 `pr-ci.yml` 与 Azure PR Pipeline 承担。构建产物**仅 win-x64**。
- **PAT 红线无关**：runner 用 GitHub 自动注入的 `GITHUB_TOKEN`（一次性、scope 自动限制到本仓 contents:write），不与「客户端升级检查 PAT」共用。

### 9.4 CI/CD 红线

#### 9.4.1 仍然禁止的
- **禁止**新增 `.gitlab-ci.yml`、`Jenkinsfile`、`.circleci/`、`.travis.yml` 等任何**第三方** CI 配置（GitHub Actions 之外的非 Azure 工具链）。
- **禁止**公开 PR CI 使用写权限、Secrets、`pull_request_target` 或调用内网服务；GitHub `pr-ci.yml` 仅镜像可公开执行的 build/test/红线/schema 门禁。
- **禁止**出现 `dev.azure.com` 路径（云端 Services 标识，本项目不适用）。
- **禁止**在 GitHub Actions workflow 里调任何 Azure DevOps Server API（避免反向依赖打通公网）。
- **禁止**任何 tracked 文件（代码 / 文档 / 脚本 / yaml）写入内网实值（内网 IP / 主机名 / Collection 名等内部拓扑）或凭据——仓库会镜像到公网 GitHub。内网实值唯一落点 = `CLAUDE.local.md`（已 gitignore），tracked 文档一律用 `<azure-server>` / `<collection>` / `<project>` / `<repo>` 占位；脚本一律参数显式传入。

#### 9.4.2 GitHub Actions 边界（公开 PR CI + 发版）
- **仅允许** `.github/workflows/pr-ci.yml` 与 `.github/workflows/release.yml`。前者只能 `pull_request` → `main` 且 `permissions: contents: read`；后者只能 `push` tag `v*` / `push` `main`。新增第三份 workflow 即违反红线。
- **main push 仅产 Artifact、严禁创建 Release**：必须用 `if: startsWith(github.ref, 'refs/tags/v')` 把 `gh release create` / `gh release upload` 两个 step 守起来；任何形式的 main → Releases 页写入都视为违反红线（污染正式发版渠道、误导升级检查端点）。
- workflow 步骤里**禁止**调 `https://api.github.com/repos/*/actions/*` 等自指 API（避免链式触发死循环）。
- 凭据仅用 `secrets.GITHUB_TOKEN`（GitHub 自动注入）；**禁止**在 Actions secrets 里塞 Azure DevOps PAT / 用户密码等内网凭据。

### 9.5 分发渠道（GitHub 公开源码 + Releases）
- **源码**：GitHub 为公开事实源；Azure DevOps Server 如保留，仅作显式同步的内网镜像（详见 §9.2）。
- **发版产物**：由 GitHub Actions `release.yml` 打包并上传到 GitHub Releases，供：
  1. 终端用户从 https://github.com/mdjs147/PersonalMediaManager/releases 手动下载安装包
  2. 客户端「检查更新」端点读 `api.github.com/repos/mdjs147/PersonalMediaManager/releases/latest`
- **PAT 红线**（客户端「检查更新」用 PAT 的强约束，与 §八 红线表联动）：
  - 用于客户端检查更新的 GitHub PAT 一律走 **`System_Setting.Update_GitHubPat` + `IProtectedFieldService` 加密落库**，与 TMDB ApiKey / AI Provider ApiKey 同等级安全模式
  - **禁止**：写进 `appsettings*.json` / 环境变量 / `.git/config` / 任何 tracked 文件；日志输出走 `SensitiveDataRedactor`（Bearer Token + apikey 等关键字自动脱敏）
  - 推荐 **Fine-grained PAT**（单仓库 + 强制过期 + `Contents: Read-only`），Classic PAT 兼容但不推荐
- **CLI 红线**：本仓库 PowerShell / Bash 自动化脚本里禁止内嵌明文 PAT；需要用 PAT 时走 **Windows Credential Manager**（`git credential approve` 一次性灌入）或显式 stdin 注入，绝不进命令行参数。

## 十、新需求 / 需求变更
- 与当前需求文档对比；有变更则同步更新需求文档与进度表，必要时调整 README。

## 十一、文档输出格式
- 除非用户直接要求 Word，否则统一以 Markdown 格式输出。

## 十二、解决方案结构与项目命名

### 项目矩阵（7 src + 6 tests，强制）

| 项目 | 类型 | 职责 | 主要依赖 NuGet |
|---|---|---|---|
| `PersonalMediaManager.Launcher` | Exe（唯一可执行） | 进程入口、托盘图标、单实例守护（Win Mutex）、开机自启注册、IPC 唤起浏览器；**不写业务** | **Windows 原生**（单 TFM `net10.0-windows`）：**内置 `System.Windows.Forms.NotifyIcon`**（`<UseWindowsForms>true</UseWindowsForms>`，SDK 自带）<br>**禁止**引入 Eto.Forms / Avalonia / WindowsAppSDK / MAUI 等任何跨平台 UI 框架 |
| `PersonalMediaManager.Host` | 类库 | ASP.NET Core 宿主装配：Kestrel + REST API（Controllers）+ SignalR Hub + `IHostedService` 后台 worker（FileWatcher / TaskProcessor / WebhookOutbox / Quartz / NetworkShareMonitor）+ 中间件 + 过滤器；对外暴露 `PmmHost.CreateApp(args, paths)` 工厂 | Microsoft.AspNetCore.App / SignalR |
| `PersonalMediaManager.Application` | 类库 | 应用服务（用例编排）+ 外部依赖契约接口（`IAiProvider` / `ITmdbClient` / `IFileMover` / `IProtectedFieldService` / `ICurrentUser`）+ Request/Response DTO + `Result<T>` + `ApiCode` 常量；**不引 EF Core / ASP.NET Core** | 仅 Microsoft.Extensions.* |
| `PersonalMediaManager.Domain` | 类库 | **充血聚合**（`ParseTask` / `AiCallChain` / `WatchDirectory` / `MediaItem` 等）+ **贫血实体**（CRUD-shape：用户/设置/字典/Webhook/审计/TMDB 缓存）+ 值对象 + 状态枚举 + 领域异常 + 实体基类；**零外部依赖**（连 EF/ASP.NET 都不引） | 无 |
| `PersonalMediaManager.Infrastructure.Persistence` | 类库 | `PmmDbContext` + `IEntityTypeConfiguration` + 迁移 + 拦截器（`Timestamp` / `RowVersion`）+ 仓储实现（若有） | EntityFrameworkCore.Sqlite |
| `PersonalMediaManager.Infrastructure.External` | 类库 | `TmdbClient` + AI Providers（Ollama / Qwen / DeepSeek / OpenAICompatible）+ Webhook 出站 HTTP 发送器；实现 Application 定义的契约 | Microsoft.Extensions.Http.Polly |
| `PersonalMediaManager.Infrastructure.Platform` | 类库 | `FileMover` + `FileSystemWatcher` 适配 + `DataProtection` 包装 + Quartz Job + 跨平台路径解析；实现 Application 定义的契约 | Quartz / DataProtection.Extensions |

### 前端工程（并入解决方案，不计入 7 个 .NET 项目矩阵）

| 项目 | 类型 | 职责 | SDK |
|---|---|---|---|
| `PersonalMediaManager.Frontend` | `.esproj`（JS 工程） | Vue 3 + Vite SPA；`npm run build:host` 产物写入 `Host/wwwroot`；VS 资源管理器可见、可 F5 起 vite dev(5173) | `Microsoft.VisualStudio.JavaScript.SDK`（参考 VS「Vue 应用」模板） |

- **不计入「7 src」矩阵**：它是 JavaScript 工程而非 .NET 项目；矩阵的引用关系图与红线只约束 7 个 .NET 项目。
- **隔离根 MSBuild**：`Frontend/Directory.Build.props` + `Directory.Build.targets`（均为空壳）切断对仓库根 `Directory.Build.props/.targets` 的继承，避免 `TargetFramework=net10.0` 等 .NET 属性污染 JS 工程；前端版本号仍由 `vite.config.js` 直接读根 props 注入，不受影响。
- **构建集成（深度集成：build/publish slnx 即出前端）**：
  - `Host._PmmBuildFrontend`（`BeforeTargets=_CalculateEmbeddedFilesManifestInputs;BeforeCompile`）显式 `<MSBuild>` 调 `.esproj` 的 Build → 跑 `npm run build:host`。**Debug/Release 均触发，但带 MSBuild 增量**（`Inputs`=前端源码 src/public/index.html/package.json/vite.config.js，排除 vite 生成的 `*.d.ts`；`Outputs`=`wwwroot/index.html`）：仅当前端源码比 wwwroot 新时才重跑 vite，否则短路跳过——**F5 启动 Launcher 即拿到最新前端，未改前端时秒过**。**为何锚到 `_CalculateEmbeddedFilesManifestInputs`**：wwwroot 要嵌入 Host 程序集（见下条），前端产物须在「嵌入清单收集 + Host 编译」之前就绪，挂成 `BeforeTargets=Build` 会晚于编译。`dotnet build *solution*` 不会自动调 `.esproj` 的 Build（只跑 restore 图），项目级 ProjectReference 又会触发，故统一显式触发 + `BuildReference=false` 防重复。前端 HMR / 源码调试可另起 vite dev(5173)，与本构建解耦。
  - `Host._PmmCollectFrontendEmbed`（`AfterTargets=_PmmBuildFrontend`、`BeforeTargets=_CalculateEmbeddedFilesManifestInputs`）在 **execution 阶段**动态把 `wwwroot/**` 加为 `EmbeddedResource`（配合 `GenerateEmbeddedFilesManifest=true`）→ 前端产物连同 `appsettings.json` 编译进 `Host.dll` 随单文件 exe 内置；运行时 `PmmHost` 用 `ManifestEmbeddedFileProvider` 内存直读。**发布产物收敛为单一 exe**（不再外置 `wwwroot/`，原 `Launcher._PmmCopyFrontendToOutput/Publish` 复制 Target 已删；Launcher 发布配置加 `AllowedReferenceRelatedFileExtensions=none` 阻止 `Host.xml` 外溢）。
  - **避开 MSB3030/嵌入快照**：wwwroot 不以 `<Content/EmbeddedResource Include="wwwroot\**">` 静态 glob 纳入项目——vite 的 hash 文件名每次变，VS 设计时（evaluation）会快照旧 hash 清单导致失败；改用 execution 阶段（`_PmmCollectFrontendEmbed`）动态加 `EmbeddedResource`，evaluation 不再快照，从根上消除。
- **CI 联动**：`.esproj` 进 slnx 后 `dotnet restore/build slnx` 会触发 npm install 与 vite，故 Azure PR 管道 build/test 的 Windows agent 在 restore 前加 `NodeTool@0`；GitHub `release.yml` 删手动 `npm run build`/拷贝步骤，由 `dotnet publish`（Release）自动产出前端。

### 测试项目

| 项目 | 测什么 |
|---|---|
| `PersonalMediaManager.Domain.Tests` | 聚合行为、状态机转移、不变式守护 |
| `PersonalMediaManager.Application.Tests` | 应用服务（用 Mock 契约 + In-Memory 仓储） |
| `PersonalMediaManager.Infrastructure.Persistence.Tests` | EF Migration + SQLite in-memory + 拦截器 |
| `PersonalMediaManager.Infrastructure.External.Tests` | TMDB 客户端 / AI 协议与解析 / 升级链 / Webhook 出站发送器（HTTP 桩 + 主备切换） |
| `PersonalMediaManager.Host.Tests` | 集成（WebApplicationFactory + 真实 DbContext） |
| `PersonalMediaManager.Launcher.Tests` | Launcher 本地配置（`LocalConfigStore` 读写 / JSON 损坏自愈 / 端口与连接串覆盖校验）等纯逻辑单元 |

> External 测试已落地（见上表）。Platform（`FileMover` / `FileSystemWatcher` 适配 / DataProtection 包装）测试仍按需新建（涉及真实文件 IO，前期不强求）。

### 引用关系（强制单向，违反即拒绝合并）

```
Launcher → Host
Host → Application + Infrastructure.{Persistence, External, Platform}
Infrastructure.Persistence → Application + Domain
Infrastructure.External    → Application
Infrastructure.Platform    → Application
Application → Domain
Domain → (无)
```

**绝对禁止**：
- Domain 引任何上述项目（必须保持零依赖可单测）
- Application 引 EF Core / ASP.NET Core / Quartz / HttpClient 实现
- 三个 Infrastructure 子项目**互引**（跨域协作必须在 Application 服务层编排）
- Host 直接引 Domain（必须经 Application；防止 Controller 直接 new 聚合）
- 任何项目反向引 Host / Launcher

### 内部目录纪律（用文件夹边界替代项目边界）

**Domain/**
```
├─ Aggregates/             # 充血聚合，每个聚合一个子目录
│   ├─ ParseTasks/         # ParseTask.cs + 值对象 + 状态枚举 + 领域异常
│   ├─ AiCallChains/
│   ├─ WatchDirectories/
│   └─ MediaItems/
├─ Entities/               # 贫血实体（User / Setting / Category / Webhook / Audit / TmdbCache）
├─ ValueObjects/           # 跨聚合共用值对象
├─ Enums/                  # 跨聚合共用枚举
├─ Exceptions/             # DomainException 基类与领域异常
└─ Common/                 # 实体基类 / 聚合根基类
```

**Application/**
```
├─ Services/               # 应用服务，按业务域子目录（Auth/Parse/Archive/Settings/...）
├─ Contracts/              # 外部依赖接口（IAiProvider / ITmdbClient / IFileMover / IProtectedFieldService / ICurrentUser）
├─ Dtos/                   # Request/Response DTO，按业务域子目录
├─ Common/                 # Result<T> / ApiCode / BusinessException
└─ DependencyInjection/    # AddApplication 扩展
```

**Infrastructure.Persistence/**
```
├─ PmmDbContext.cs
├─ Configurations/         # 每个实体一个 IEntityTypeConfiguration<T>
├─ Migrations/             # dotnet ef migrations 输出
├─ Interceptors/           # TimestampInterceptor / RowVersionInterceptor
├─ Conversions/            # JsonValueComparer<T> / DateTime 转换器
└─ DependencyInjection/    # AddInfrastructurePersistence 扩展
```

**Infrastructure.External/**
```
├─ Tmdb/                   # TmdbClient + Polly 策略 + DTO
├─ Ai/                     # IAiProvider 各实现 + 主备切换
├─ Webhook/                # 出站 HTTP 发送器
└─ DependencyInjection/    # AddInfrastructureExternal 扩展
```

**Infrastructure.Platform/**
```
├─ FileSystem/             # FileMover / FileSystemWatcher 适配 / 跨平台路径
├─ Scheduling/             # Quartz Job
├─ Security/               # DataProtection 包装 / ProtectedFieldService
└─ DependencyInjection/    # AddInfrastructurePlatform 扩展
```

**Host/**
```
├─ Controllers/            # 按业务域子目录（Auth/Settings/Review/...）
├─ Hubs/                   # LogHub / TaskHub
├─ Middleware/             # RequestId / ExceptionHandler / TokenRefresh
├─ HostedServices/         # FileWatcher / TaskProcessor / WebhookOutbox / NetworkShareMonitor
├─ Filters/                # ActionFilter / AuthorizationFilter
├─ Composition/            # PmmHost.CreateApp 工厂
└─ Program.cs              # Launcher 调用入口
```

**Launcher/**（**单 TFM**：`net10.0-windows`，仅支持 Windows；**禁止**引入跨平台 UI 框架）

> 进程托管 `[STAThread] Main` + `PmmTrayContext : ApplicationContext`（无主窗口）；启动期外层 `try/catch` 写 `<AppPaths.Root>\tray-crash.log` + `MessageBox`。命令行仅 `--autostart`（控制是否弹启动气泡）。详见 `docs/需求规范-启动方式与托盘常驻.md`。

```
├─ Program.cs                       # [STAThread] Main → 单例检测 → 起 PmmTrayContext（含 PmmHost.CreateApp + 托盘 + 消息循环）
├─ PmmTrayContext.cs                # ApplicationContext 子类：托盘 + Kestrel 生命周期 + 启停/配置端口/配置 db Dialog
├─ LocalConfigStore.cs              # 静态：local.json 读写（仅 Web:Port + ConnectionStrings:Default 两项 override + 校验 + JSON 损坏自愈）
└─ Platform/
    ├─ IPlatformTray.cs             # 抽象：Show / Hide / SetMenu / OnClick / SetState / ShowBalloon / RunMessageLoop
    ├─ IPlatformAutoStart.cs        # 抽象：IsEnabled / Enable / Disable
    ├─ IPlatformSingleInstance.cs   # 抽象：TryAcquire / NotifyExistingInstance / OnSecondInstance
    └─ Windows/                     # Windows 原生实现（产品仅支持 Windows）
        ├─ WindowsTray.cs           # System.Windows.Forms.NotifyIcon（SDK 内置）+ 程序化绘制图标 + 主题响应
        ├─ WindowsAutoStart.cs      # HKCU\Software\Microsoft\Windows\CurrentVersion\Run 注册表
        ├─ WindowsSingleInstance.cs # Mutex + 命名管道 IPC
        └─ WindowsFirewall.cs       # New-NetFirewallRule（UAC 拒绝降级到托盘气泡）
```

### 发布产物名（与项目名解耦）
- Launcher `<AssemblyName>PersonalMediaManager</AssemblyName>` → 产物 `PersonalMediaManager.exe`
- 其他类库沿用项目名

### 命名空间
- 一律以 `PersonalMediaManager.<项目尾段>` 起头，与文件夹层级同步
- 例：`PersonalMediaManager.Domain.Aggregates.ParseTasks` / `PersonalMediaManager.Infrastructure.Persistence.Configurations`
