# PersonalMediaManager 需求文档

> 版本：v0.1.0（已正式发版）
> 最后更新：2026-06-12
> 当前阶段：**已发布 v0.1.0**（2026-06-12，GitHub Releases 单文件 exe）。EF 迁移已收敛为单一 `20260611125131_Init` 基线；自首个正式版起，数据结构 / Schema / HTTP API 的变更需考虑升级兼容（启动时自动执行 EF Migration + 数据根独立于 exe，保证覆盖升级无感）。

---

## 一、项目概述

PersonalMediaManager（下称 PMM）是一款**仅支持 Windows** 的桌面级启动器程序，负责把已经落到本地磁盘的媒体文件**识别、分类、按 Plex 规范重命名并归档**到对应媒体库目录，从 TMDB 站点获取元数据并在本地生成 `.nfo` + 海报，供 Plex / Emby / Jellyfin 等媒体服务器消费。

### 1.1 部署形态

**单一启动器（双击即跑）**

- **Windows**：单文件 `PersonalMediaManager.exe`

普通用户直接双击启动即可使用。启动后默认进入后台运行，同时显示托盘图标。右键托盘图标可：

- 查看当前端口 / 复制 WebUI 地址
- 打开浏览器访问 WebUI
- 开关服务
- 配置端口…（写入 `local.json` 覆盖项并自动重启服务）
- 配置数据库位置…（同上，覆盖默认数据根中的 `pmm.db` 路径）
- 勾选「开机自启」
- 退出程序

通过 WebUI 进行日常交互，局域网多设备可访问。

### 1.2 核心工作流

1. **文件来源**：用户手动放入监控目录、第三方下载器下载完成后落到约定中转目录、其它任意来源（网盘客户端、SMB 共享）
2. **解析识别**：智能规则引擎根据文件名 + 路径解析；命中度低时调用 AI 兜底；结果交给 TMDB API 校准
3. **分类决策**：按用户配置的规则匹配媒体分类（电影 / 电视剧 / 动漫 …）；规则不命中走 AI 兜底
4. **归档落地**：按 Plex 规范重命名并移动 / 复制到目标目录，生成 `.nfo` + 海报；同名冲突按策略处理（默认跳过，可配升级替换 / 保留多版本，见 §3.7）
5. **人工兜底**：无法自动处理的情况进入人工确认队列，WebUI 支持手动修改并重新执行

---

## 二、技术选型

### 2.1 总体技术栈

| 层 | 选型 |
|---|---|
| **前端** | Vue 3 + JavaScript（不使用 TypeScript） + Vite 6 + Pinia + Vue Router；UI 组件库 **Element Plus**（不使用 Naive UI） |
| **后端** | .NET 10 / ASP.NET Core 10（Kestrel 内置 HTTP 服务器，无需 IIS） |
| **数据库** | SQLite + EF Core 10（Code First 迁移，单文件 `pmm.db`） |

### 2.2 详细组件

| 项目 | 选型 |
|------|------|
| 开发语言 | C# 14 / .NET 10 LTS |
| 后端框架 | ASP.NET Core 10 |
| ORM | EF Core 10（含迁移工具 `dotnet ef`） |
| 前端 HTTP | openapi-fetch（基于 OpenAPI 生成的 `schema.d.ts` 类型化调用，中间件统一处理 401 / 错误；不使用 axios） |
| 前端状态 | Pinia |
| AI 接入层 | 统一 `IAiProvider` 接口，支持本地 Ollama 及外部模型 API |
| 媒体元数据 | TMDB API v3 |
| 文件监控 | .NET `FileSystemWatcher` + Quartz.NET 定时任务 |
| 配置方式 | `appsettings.json` + WebUI 配置页面（持久化至 SQLite） |
| 数据库接入 | `Microsoft.Data.Sqlite` + EF Core SQLite Provider |
| 实时推送 | SignalR（`/hubs/logs`、`/hubs/tasks`） |
| 凭据保护 | ASP.NET Core DataProtection API |
| 日志框架 | Serilog（Console + File + SignalR Sink） |
| 鉴权 | JWT（HS256），`Microsoft.AspNetCore.Authentication.JwtBearer` |
| 密码哈希 | BCrypt.Net |
| HTTP 客户端弹性 | TMDB 节流 / 重试 / 退避为自实现极简令牌桶（`TokenBucketRateLimiter`，不依赖 Polly 大套件）；Polly 仅保留用于其它出站 HTTP 场景 |
| 进程模型 | **单进程**——Kestrel HTTP（Web 静态资源 + REST API + SignalR Hub）+ `IHostedService` 后台 worker（FileWatcher / TaskProcessor / WebhookOutbox / Quartz 定时扫描 / 网络盘检测）全部托管在同一启动器进程内 |
| 托盘 / 系统集成 | **Windows 原生**（**禁止引入跨平台 UI 框架**，如 Eto.Forms / Avalonia / WinUI / MAUI）：<br>· Windows：内置 `System.Windows.Forms.NotifyIcon`（`<UseWindowsForms>true</UseWindowsForms>` 启用，.NET SDK 自带，零外部包；NuGet 上 H.NotifyIcon 无 WinForms 变体，禁止引第三方托盘库）<br>· 抽象层：`IPlatformTray` 接口 + Windows 实现（接口保留用于隔离 OS API、便于单元测试）<br>**理由**：启动器仅需托盘图标 + 菜单 + 浏览器跳转，所有交互在 WebUI；OS 原生 API 长期稳定、产物体积最小 |
| 源码托管 / CI&#124;CD | **公开协作**：GitHub `https://github.com/mdjs147/PersonalMediaManager` 承载公开源码、PR 与 Releases；`pr-ci.yml` 用只读权限跑 build/test/红线/schema drift，`release.yml` 仅处理 main Artifact 与 tag Release。<br>**内网团队基础设施**：Azure DevOps Server（非云端 Services）可继续作为内部镜像与 Azure Pipelines 执行面；地址一律使用 `<azure-server>/<collection>/<project>` 占位，实值只在 gitignored `CLAUDE.local.md`。<br>GitHub Actions 仅允许 `.github/workflows/pr-ci.yml` 与 `.github/workflows/release.yml`；禁止 `pull_request_target`、PR Secrets、访问内网 API，以及 `.gitlab-ci.yml` / `Jenkinsfile` 等第三方 CI。 |

### 2.3 部署与运行约束

**Windows：**
```
dotnet publish -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
```
产出单文件 `PersonalMediaManager.exe`。

**前端：** vite 产物写入 `wwwroot/` 后随 `appsettings.json` 一并以 EmbeddedResource 编译进 Host 程序集，单文件 exe 内置（发布产物不外置 `wwwroot/`）。

**最终交付：**
- 单文件 `PersonalMediaManager.exe`（仅 Windows，win-x64；前端与 appsettings 已嵌入）。`pmm.db` 等数据由程序首次运行时在数据根自动生成，不属交付物

**容器化禁止（红线）：**
- 不引入 `Dockerfile` / `docker-compose.yml` / K8s manifest / Helm Chart
- 不引入容器健康检查端点、K8s 探针、`/proc/1/cgroup` 类感知逻辑
- 仅支持作为启动器或 Windows 用户级开机自启运行

### 2.4 运行模式

仅支持以下两种：

| 模式 | 触发方式 | 进程形态 | 用途 |
|---|---|---|---|
| **① 前台启动器（默认）** | 双击 `.exe` | 用户态进程，托盘图标常驻；前台关闭则停止 | 普通用户日常使用 |
| **② 开机自启** | 托盘菜单「勾选开机自启」 | 仍是同一启动器，由 OS 在登录后拉起 | 不想每次手动启动 |

**不做** Windows Service 这种系统级常驻。

- **开机自启**：写注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

### 2.5 进程单例

- 命名 Mutex `Global\PersonalMediaManager`
- 第二次启动检测到已有实例 → 通过 IPC 通知原实例弹出浏览器窗口指向 WebUI → 新实例退出
- **目的**：避免 SQLite 文件锁与端口冲突

### 2.6 自更新策略

- **无自更新机制**（不内置静默下载替换）；但内置「检查更新」提示：`UpdateCheckWorker` 周期查 GitHub Releases `releases/latest`，发现新版在 WebUI 提示（见 §十）
- 用户手动下载新版本覆盖旧 `.exe`
- 数据根（`pmm.db`、密钥环、`jwt-signing-key.txt` 等）独立于 exe 存放，覆盖升级不受影响
- 程序启动时检测 SQLite 表结构差异，自动执行 EF Migration（自 v0.1.0 起以 `20260611125131_Init` 为基线增量升级）

---

## 三、功能模块详细设计

### 3.1 服务初始化与配置（首次启动向导）

首次启动时，WebUI 自动跳转至初始化向导页面。**向导仅一步：创建管理员账号**（用户名 + 密码，必填；密码 ≥ 6 位，无字符种类强制），完成后自动登录并进入仪表盘。

其余配置均为**后续设置页可选项**，按需在「设置」中完成（不强制、无数量门槛）：

- WebUI 监听端口（默认 `http://0.0.0.0:7288`，局域网可访问，**单端口**承载 HTTP + REST API + SignalR；亦可经托盘「配置端口…」修改）
- TMDB API Key
- AI 提供商（建议至少配一个，可配多级升级链，见 §3.3.3）
- 监控目录
- 媒体分类与存放路径

### 3.2 文件监控

#### 三重触发机制

| 方式 | 说明 | 配置项 |
|------|------|--------|
| **实时监控** | `FileSystemWatcher` 监听各监控目录 | 默认开启 |
| **定时扫描** | 全量扫描所有监控目录 | 默认每 12 小时，可配置间隔 |
| **手动触发** | WebUI「立即扫描」按钮 | — |

#### 写入完成判断（双信号）

| 信号 | 适用场景 | 说明 |
|---|---|---|
| **稳定性检测（默认）** | 单文件直接写入 | 文件首次出现后启动定时器（默认 5 秒，可配置），再次检测时若文件大小未变化且未被系统锁定，则判定写入完成 |
| **`.complete` 哨兵文件（强信号）** | 目录级交付（整季种子、多文件包等） | 当 PlexMediaDownloader 等下载器在目录根创建 `.complete` 文件时，立即把整个目录作为一个交付单元入队，不再等待稳定性检测 |

> **哨兵兼容性说明：** 非本项目自家下载器（如 qBittorrent、Aria2、网盘客户端）不会生成 `.complete`。`.complete` 是「PlexMediaDownloader 链路的加速路径」；其他来源仍走稳定性检测。

#### 忽略规则

- **临时扩展名**：`.part`、`.tmp`、`.crdownload`、`.download`、`.!qb`、`.downloading`（与 PlexMediaDownloader 写入约定一致，可在设置中自定义忽略列表）
- **哨兵文件**：`.complete` 本身不进入处理队列，仅作触发信号；处理完成后由 PMM 自动清理该哨兵

#### 目录级交付识别

- 当监控目录下检测到 `.complete` 哨兵文件，把它的同级目录视为「整体交付单元」
- 该单元中的所有视频文件批量进入处理队列，按 §3.3 单独解析（一季多集会被识别为同一剧集的不同集）
- 单元中的非媒体文件（如说明 `.txt`、字幕包）按字幕重命名规则一并处理或忽略

#### 网络盘 / SMB 共享支持

- 监控目录可标记 `IsNetworkShare = true`，表示该目录位于 NAS 或 SMB 挂载点
- 后台 `NetworkShareMonitor` 定期检测可达性（默认每 60 秒尝试 `Directory.Exists` + 轻量目录读取）
- 不可达时：
  - 暂停该目录的 `FileSystemWatcher`
  - 仪表盘高亮告警
  - 不报错入队，避免任务堆积
- 恢复可达后自动重启监控并触发一次该目录的全量扫描

### 3.3 媒体名称解析（规则引擎优先 + AI 按需兜底）

整体策略：**规则引擎先做一次结构化判断 → 拿规则结果直接查 TMDB → 根据 TMDB 返回情况再决定是否调用 AI 进行二次处理**。

设计动机：绝大多数下载文件名（PT 站、BT 站、网盘）已经具备相对规范的结构，正则 + 启发式即可在毫秒级解析出标题/年份/季集，没必要为每个文件都付出 AI 调用成本与延迟。AI 仅作为「难处理样本」的兜底。

#### 3.3.1 智能规则引擎

**输入：** 原始文件路径（含完整目录链）+ 文件名。

**输出：** `ParsedMediaInfo`，字段如下：

| 字段 | 说明 |
|------|------|
| Title | 清洗后的纯净标题（中/英/日均可） |
| Type | `movie` / `tv` / `unknown` |
| Year | 4 位年份（可为 null） |
| Season | 季号（无则 null） |
| Episode | 集号（无则 null） |
| Confidence | 0.0 ~ 1.0，规则引擎自评分 |
| Source | `Rule` / `AI` / `Hybrid`（标记结果来源） |

**核心规则（内置）：**

1. **噪声清洗** —— 去除分辨率、编码、来源、HDR、发行组、字幕语言，以及剧名层的**总季数/总集数后缀**（`6季`、`全24集`、`共26集`、`6部`、`第1-6季` 等总量标记，区别于单季「第N季」——后者保留给季号提取）等标记（`NameCleanerService` 逻辑）
2. **季集模式提取**（命中则置 `type=tv`）：
   - `S01E02`、`s1e1`、`SE01EP02`
   - `第3集`、`第03话`、`E01`、`EP01`、`[01]`
3. **年份提取** —— 4 位年份（1900–2099），出现在标题尾部或括号内时优先采用
4. **类型推断启发式：**
   - 文件名/路径含 `Season` / `第X季` / `S01` → `tv`
   - 文件名/路径含 `电影` / `Movie` / 单文件且无任何剧集标记 → `movie`
   - 仍无法判断 → `unknown`（交给 TMDB 兜底）
5. **路径上下文加成** —— 父目录名作为标题/类型的补充线索：
   - 父目录 `绝命毒师 第一季 1080p` + 文件名 `EP01.mkv` → 父目录解析标题，文件名解析集号
   - 父目录含 `Movies` / `电影` → 提示 `type=movie`
   - 父目录含 `TV` / `剧集` / `Anime` → 提示 `type=tv`

**用户可自定义规则（自由增删改）：**

规则引擎除上述内置规则外，还支持用户在 WebUI **自由新增、编辑、停用、删除**规则。每条规则包含：

| 字段 | 说明 |
|------|------|
| 名称 | 规则备注名（如「国产剧 ZeroBureau 命名」） |
| 启用状态 | 可单独开关 |
| 优先级 | 整数，越小越先匹配 |
| 应用范围 | `FileName` / `ParentFolder` / `FullPath` 三选一 |
| 匹配模式 | .NET 正则，建议使用命名捕获组：`title`、`year`、`season`、`episode` |
| 默认 Type | `movie` / `tv` / 留空（由捕获结果推断） |
| 类型映射 | 命中后强制覆盖类型（可选） |
| 置信度加成 | 命中后追加的置信度（0~0.3） |
| 备注 | 用户说明，便于团队共享 |

**执行顺序：** 用户规则（按优先级） → 内置规则，先命中先采用。

**正则安全：** 所有正则执行强制 `RegexOptions.Compiled + MatchTimeout = 500ms`，避免 ReDoS。WebUI 测试器若检测到超时，提示「正则可能有性能问题」。

**置信度评分规则（默认）：**

| 提取字段命中情况 | 置信度 |
|------------------|--------|
| 标题 + 类型 + 季 + 集 + 年份 全部命中 | 0.95 |
| 标题 + 类型 + 季集（无年份） | 0.85 |
| 标题 + 类型 + 年份（无季集，电影） | 0.85 |
| 剧集缺**集号** | 0.50（集号无法靠 TMDB 反推，转 AI 从路径再挖） |
| 剧集仅缺**季号**（集号在） | 0.70（走 TMDB 直查：单季剧下游自动补 S01、多季剧转人工审核选季，不为季号动用 AI） |
| 标题 + 类型（其余缺字段） | 0.70 |
| 仅标题 | 0.50 |
| 标题无效（各路径层剥噪声后均无有效标题内容，兜底 stem 原文） | ≤ 0.50（技术残渣 / 压制代号不得高置信直查） |
| 啥都解析不到 | 0.10 |

**标题有效性判定**：剥噪声后按 token 粒度检查——剔除纯数字与单字符 token 后，须仍有含 ≥2 个字母/CJK 字符的 token。文件名层无效时按「内→外」回落到父目录 / 祖先目录层取标题（下载站常见「文件夹=剧名、文件名只剩集号/技术参数」的布局由此覆盖）；「压制代号-集号」整串形态（如 `DACZLNF-09`，数字段为 4 位年份的除外）数字段作集号、字母段不参与标题竞选。

置信度阈值由设置项 `ParseConfidenceThreshold` 控制（默认 **0.6**）。低于该值时**不再用规则结果查 TMDB**，先做本地备选标题重搜（见 §3.3.2），全部不中才转 AI。

**已落地增强（在上述初版规则之上）：**

- **路径段全链解析**：不止父目录一层，整条目录链逐段参与标题 / 类型 / 季集线索提取
- **季号增强识别**：中文数字（第十二季）、罗马数字（Ⅱ / II）、篇章名 + 季名对照（如「锻刀村篇」映射到对应季号）
- **文件夹剧集复用（持久化）**：同目录兄弟集复用既有 TMDB 绑定并持久化，重启后 / 规则路径命中时跳过 TMDB 搜索直接复用——即 §3.3.2「第一次 TMDB 查询」存在**复用短路**：命中文件夹复用时不再发起搜索
- **AI 别名兜底第 0 层**：国产剧 / 动漫常见别名先走本地别名对照，命中即免外部 AI 调用
- **归档标题语言回退链**：中文标题 → TMDB 原文标题 → 解析名逐级回退（用于 §3.6 命名）
- **标题有效性 token 判定 + 目录层回落**：技术残渣（`2026 60fps WEB 1`）与压制代号（`YTYHXBYL`）不再被当作标题高置信直查，回落父目录层取剧名（见上「标题有效性判定」）
- **本地备选标题重搜（AI 前置拦截）**：规则引擎随主标题产出备选搜索标题列表 `AlternativeTitles`（混排标题拆分出的 CJK 段 / 粘连副标段 / 拉丁词组段 + 其余路径层标题，纯 CJK 段优先、归一化去重、上限 5 个）；触发 AI 的三类场景（混排 / 低置信 / 首查候选不符）先用备选标题逐个重搜 TMDB（上限同 AI 别名重试数 3），命中 `[1,N]` 候选即免走 AI、`ParseSource` 保持 `Rule`——典型获益：英文主标题在 TMDB zh-CN 搜不到但路径目录带中文剧名（`【…】铁拳教育[全10集]….Teach.You.a.Lesson.S01…`）
- **AI 参与度持久标记**：`Media_Item.AiInvolved` 在真正发起 AI 升级链时置位（复用直通 / 纯规则不算；AI 失败转人工也保留过程事实），作为统计页「AI 参与率」的长期稳定口径（不受 `Audit_AiCall` 90 天保留期影响）
- **候选过多免 AI 裁决（四维打分显著）**：首查候选 > N 时先按 §3.4 四维加权打分（标题/年份/热度/语言）排一次——榜首 ≥ 0.7 且领先次名 ≥ 0.15 视为「唯一可信」直接采纳，免烧 AI（热门标题返回 10+ 候选时年份 + 标题相似度多可唯一锁定）；整理演练（DryRun）同口径预演
- **备选标题交叉投票（多次 TMDB 查询对比替代 AI 裁决）**：打分含糊时，备选标题重搜结果即便同样 > N 也不丢弃，而是与首查候选求交集计票——多个不同检索词都命中同一条目是强消歧信号；唯一最高票且四维得分 ≥ 0.5 即免 AI 采纳（并列票 = 歧义仍在，弃权交 AI），`ParseSource` 保持 `Rule`
- **高置信全零跳 AI + 每日自动重投**：主标题与全部备选（每个查询自带主/回退语言 × 带年/去年 4 层透明回退）全部零结果、且规则置信度达标无混排 → 判定「TMDB 暂未收录」（新番/新剧发布初期的典型形态，AI 重新清洗标题再搜无增量），跳过 AI 直接转 `AwaitingReview(TmdbZeroResult)`；`TmdbZeroResultRetryJob`（6 小时周期）把这类记录（`AiInvolved=false` 口径）按「距上次动作 ≥ 20 小时、入库不超过 `Parse_ZeroResultRetryWindowDays`（默认 14 天）」每日自动重投重走全管线——TMDB 收录后自动归档、循环内永不烧 AI；总开关 `Parse_ZeroResultAutoRetry`（默认开，常规设置 Parse 分组）；审核页对这类记录显示「候补自动重试中」标签
- **人工确认沉淀**：审核页确认 / 改绑 TV 剧集后，人工裁决的绑定立即回写文件夹剧集复用缓存（置信度按 1.0 记）——同目录后续文件（典型如周更新集）免搜索免 AI 也免再次人工，不必等「已归档兄弟集」查库回填
- **兄弟目录同剧复用（分集目录模式）**：追更下载器常「每集单开一个目录」（`剧名[第01集]xxx\`、`剧名[第02集]xxx\`），精确目录键永不复用；持久映射还原在精确目录未命中时按「同父目录 + 目录名剥集号段（第NN集/话/回、SxxExx、E/EP+数字）归一化后相同」找已归档兄弟目录还原 series 身份，命中仍走标题相似度守门，误配最坏回落正常搜索流程

#### 3.3.2 TMDB 查询与 AI 触发决策

**第一次 TMDB 查询：** 用规则引擎的 `Title + Type + Year` 直接调 TMDB。

**决策矩阵：**

| 规则置信度 | TMDB 候选数 | 处理动作 |
|-----------|------------|--------|
| ≥ 阈值（默认 0.6） | 唯一命中 | ✅ 直接采用 |
| ≥ 阈值 | 在 N 个以内（N 默认 3） | ✅ 取相关性最高的（综合相似度排序，见 §3.4） |
| ≥ 阈值 | 候选 > N | 📊 四维打分榜首显著即采纳 → 🔁 本地备选标题重搜 + 交叉投票，均不中才 🤖 调 AI 二次解析（标题混淆，需更准确搜索词） |
| ≥ 阈值 | 零结果 | 🔁 本地备选标题重搜；全零 → ⏳ 判定 TMDB 未收录，跳过 AI 转人工 + 每日自动重投（见 §3.3.1「高置信全零跳 AI」） |
| < 阈值 | —（不查） | 🔁 本地备选标题重搜，不中才 🤖 直接调 AI 二次解析 |
| 命中「特殊字符规则」 | — | 🔁 先按拆分段（CJK / 拉丁）重搜，不中才 🤖 调 AI（标题含中日韩混杂、特殊符号；全零也不跳 AI——AI 的标题清洗与检索别名对混排有真实增量） |

> 🔁 = **本地备选标题重搜**（AI 前置拦截，见 §3.3.1「已落地增强」）：命中即按 ✅ 采用（`ParseSource` 保持 `Rule`，未动用 AI）。
> 📊 = **候选过多免 AI 裁决**：四维打分榜首 ≥ 0.7 且领先次名 ≥ 0.15 直接采纳；打分含糊时备选重搜结果与首查候选**交叉投票**（多个检索词命中同一条目 = 强消歧信号），唯一最高票且得分 ≥ 0.5 即采纳——用多次 TMDB 查询对比替代 AI 裁决，均见 §3.3.1。

**第二次 TMDB 查询（AI 兜底后）：** 用 AI 返回的 `Title + Type + Year` 重新查 TMDB：

| AI 后 TMDB 状态 | 处理动作 |
|----------------|----------|
| 唯一命中 / 候选 ≤ N 个 | ✅ 采用 |
| 候选 > N | ⏳ 进人工确认队列（展示 AI 候选） |
| 零结果 | ⏳ 进人工确认队列（支持手动搜索 / 手动指定 TmdbId） |

#### 3.3.3 AI 提供商架构

统一 `IAiProvider` 接口，所有 AI 提供商实现此接口，可灵活扩展：

```
IAiProvider
  ├── OllamaProvider          # 本地 Ollama
  ├── QwenProvider            # 阿里云百炼 / Qwen API
  ├── DeepSeekProvider        # DeepSeek API
  └── OpenAICompatibleProvider # 任意兼容 OpenAI 格式的服务
```

**每个提供商配置项：**

- API Base URL（Ollama 默认 `http://localhost:11434`）
- API Key（本地 Ollama 可留空）
- 模型名称（如 `llama3`、`qwen-plus`、`deepseek-chat`）
- 启用状态、优先级（决定主备顺序）
- 套餐配额限额（可选：调用次数上限 / token 总量上限 / 套餐到期时间，见下文「套餐配额限额」）

**AI 兜底链路（多级升级链 + 硬上限）：**

1. 升级序列 = 全部 `Enabled = true` 且未在禁用冷却中的提供商，按 `Priority` 升序排列（`IsPrimary` 标记首选级）
2. 从第一级开始调用；本级**失败 / 抛异常 / 返回置信度低于满意度阈值**时升级到下一级，逐级类推
3. 满意度阈值支持 **per-provider 独立配置**（`Parse_AiProvider` 字段），留空回退全局 `AiConfidenceThreshold`（默认 0.7）
4. 全链耗尽仍无满意结果 → 转人工确认队列

**硬上限规则（`AiCallChain` 聚合守护）：**
- 链路对单个文件的外部 AI 调用次数上限 = **当前可用提供商数**，并钳制到绝对天花板 **10 次**（`MaxHardCap` 成本护栏）
- 同一提供商内**不做逻辑级重试**；限流 / 配额类错误（429 等）不重试，直接升级到下一级
- 仅配置 1 个提供商时即 1 次机会

**瞬时错误内部短重试：** 连接级错误（DNS 失败、TCP 握手失败、首字节超时 < 5 秒）允许本级内部 1 次短重试（500ms 退避），**不计入链路调用次数上限**。逻辑级错误（4xx、返回值无法解析）不重试，直接升级。

**自动禁用机制（防雪崩）：**

| 维度 | 默认值 | 说明 |
|---|---|---|
| 滚动窗口 | 10 分钟 | 失败统计窗口长度 |
| 失败阈值 | 5 次 | 窗口内失败次数达此值触发禁用 |
| 禁用时长 | 30 分钟 | 到时自动恢复 |
| 手动恢复 | `POST /api/settings/ai-providers/enable`（id 放请求体） | Admin 可强制立即解禁 |

禁用状态写 `Parse_AiProvider.DisabledUntil`；选 provider 时跳过未到期记录。

**套餐配额限额（超限自动禁用，防转按量计费）：**

付费 AI 提供商常见「定量套餐」（若干调用次数 / token 总量的资源包）或「定日期套餐」（包月 / 包年），超出后自动转按量计费。为避免超支，每个提供商可选配置三种限额（留空 = 不限，可同时配置、任一命中即禁用）：

| 限额项 | 字段 | 触发方式 |
|---|---|---|
| 调用次数上限 | `QuotaCallLimit` | 累计调用次数达上限 → 写 `QuotaExceededAt` 自动禁用 |
| token 总量上限 | `QuotaTokenLimit` | 累计 token（prompt+completion）达上限 → 写 `QuotaExceededAt` 自动禁用 |
| 套餐到期时间 | `QuotaExpiresAt` | 到期后选 provider 时直接跳过（纯时间过滤，不写标记） |

- **计量口径**：每次对提供商实际发出的 AI HTTP 调用（成功 / 失败 / 超时都计——失败请求也可能计费，保守保护钱包；测试连接不计）；累计值存 `Parse_AiProvider.QuotaUsedCalls / QuotaUsedTokens` 独立计数器，不受 `Audit_AiCall` 保留期清理影响。
- **三态分离**：配额禁用（`QuotaExceededAt`）与用户开关（`Enabled`）、健康熔断（`DisabledUntil`，30 分钟自动恢复）互不干扰；手动 `/enable` 只解健康熔断，**不能**绕过配额禁用。
- **超限动作**：幂等置 `QuotaExceededAt`（并发只置位一次）+ 中文警告日志 + Webhook 事件 `ai.provider_quota_exceeded`（带抑制窗口）。
- **解除方式**：① 编辑提供商放宽限额（调高 / 清除），保存时自动重评估解禁；② `POST /api/settings/ai-providers/reset-quota` 重置用量（清零累计 + 解除禁用，用于新套餐周期 / 续购，限额配置保留）；③ 到期禁用改 `QuotaExpiresAt` 即恢复。

#### 3.3.4 强制匹配标识（pmm.txt / TMDB URL / 文件夹名 {tmdb-NNN}）—— 防错兜底

针对「重制版 / 特殊剧集组 / 冷门片」等规则与 AI 都易判错的场景，提供**用户显式锚定**机制，让系统**跳过自主判断**直接按指定 TMDB 条目归档。两种载体，按需取用：

**载体一（pmm.txt / TMDB URL，锁定类型 + 季 + 剧集组）：** 在媒体所在文件夹放置 `pmm.txt`（固定名、大小写不敏感），内容**直接贴对应 TMDB 页面网址**即可——无需记忆字段格式。自文件目录逐层上溯至监控根，命中**最近**一个标识生效；亦认含 `themoviedb.org` 网址的 `.url` 快捷方式。

**载体二（文件夹名 / 文件名 {tmdb-NNN}，仅锚 id、最省事）：** 直接把 Plex 官方匹配标记写进**文件夹名或文件名**，如目录 `刀剑神域 (2012) {tmdb-45782}` 或文件 `流浪地球 (2019) {tmdb-535292}.mkv`（兼容 `[tmdb-]` / `[tmdbid-]` / 大写，文件名扩展名不影响）。该标记**只锚定 TMDB id**，**电视剧 / 电影类型、季号、集号仍由系统规则引擎正常识别**——适合「我知道是哪部，但不想建标识文件」的场景；电影常是单文件直接命名，文件名标记尤其方便。因为这正是本工具归档落点产出的标记形态，**已归档作品整目录 / 单文件回投会被自动识别、零 TMDB 搜索**。
- 类型解析：先用规则识别出的类型（剧 / 影）拉 TMDB；若类型猜反（TMDB 的 `tv/{id}` 与 `movie/{id}` 是两个独立命名空间，错则 404）**自动翻另一类型兜底**，两者皆失败才转人工。
- 优先级：`pmm.txt` > `.url` > **文件名标记** > 文件夹名标记（前两者信息更全，自带类型 / 季 / 剧集组）；跨层仍是越靠近文件越优先（文件名标记仅在文件所在目录这一层识别）。

**URL 形态自动解析：**

| 贴入的 URL | 解析结果 |
|---|---|
| `/tv/{id}/episode_group/{eg}/group/{g}` | 剧集 + 剧集组 + 分组（重制版等） |
| `/tv/{id}/season/{n}` | 剧集 + 强制季号 |
| `/tv/{id}` | 剧集（仅锚 series） |
| `/movie/{id}` | 电影 |

> 高级覆盖（少用）：`pmm.txt` 也认 `key = value`（`tmdb` / `type` / `season` / `episode_group` / `group` / `title`），与 URL 可混写、显式键覆盖 URL 解析；注释行 `#` `//` `;` 与空行忽略；裸数字一行当 tmdb id。类型缺省 `tv`（剧集组语义本就只剧集有）。

**命中行为：**
1. 用 `TmdbId + 类型` 调 TMDB 详情锚定 series，**跳过**规则置信度判定、特殊字符转 AI、TMDB 搜索、AI 升级链全部分支；解析来源 `ParseSource = Manual`，置信度记 1.0，时间线来源标签 `manual`。其中**类型**：载体一（pmm.txt / URL）用标识自带类型；载体二（文件夹名 {tmdb-NNN}）用规则识别类型、失败翻另一类型兜底。
2. 季 / 集仍取文件名逐集解析；载体一带 `season=` 则强制该季（载体二季号一律由规则识别）。
3. **剧集组模式**（仅载体一带 `episode_group`）：把文件名解析出的「编组内集号（第 N 集）」按编组 `order` 升序映射到**正典 season/episode**（如 HD Remaster 第 1 集 → 正典 S01E02），再走既有正典归档与逐集元数据通道。多分组时由 `group=` 指定。

**失败兜底（绝不静默错归档）：**
- 标识的 TMDB id 无效 / 详情拉取失败（载体二会先后尝试 tv 与 movie 两种类型）→ 转人工确认队列（文案点明是标识，便于修正）。
- 剧集组集号越界 / 多分组未指定 `group` / 剧集组拉取失败 → 清空集号，由剧集完整性守护转人工确认。

**缓存：** 剧集详情走既有 24h 元数据缓存；剧集组复用搜索缓存表（命名空间隔离），同目录后续文件零额外远端调用。

### 3.4 TMDB 元数据获取

- **查询接口：** 电影用 `/search/movie`，剧集用 `/search/tv`，类型为 `unknown` 时两个都查并合并
- **语言策略：** 请求参数 `language=zh-CN`，若中文标题为空则 fallback 到 `en-US`
- **结果数据：** TmdbId、中文标题、英文原标题、年份、类型、剧集总季数、海报路径、原产国、原始语言、Genres
- **候选阈值 N**：默认 **3**，可在设置中调整
- **候选排序综合打分**（在多候选场景下排序，权重可调）：
  - **标题相似度**（Levenshtein 距离归一化，权重 0.5）
  - **年份接近度**（±0 = 1.0，±1 = 0.7，±2 = 0.4，>2 = 0.1，权重 0.3）
  - **Popularity**（TMDB 自带字段归一化，权重 0.1）
  - **原始语言匹配**（解析为中文 → CN/HK/TW 加分，权重 0.1）

#### 缓存策略

| 缓存类型 | Key | TTL | 失效策略 |
|---|---|---|---|
| **元数据**（`Tmdb_MetadataCache`） | `TmdbId + MediaType` | 24h | 过期自动重查覆盖；设置页支持「清空 TMDB 缓存」 |
| **搜索结果**（`Tmdb_SearchCache`） | `SHA256(query + type + year + language)` | 1h | 短缓存，避免同关键词秒级重发 |
| **海报图** | 文件名 `{TmdbId}.jpg` | 永久 | 存 `<数据根>/cache/posters/`（数据根定义见 §3.11）；归档时复制到目标目录而非每次重下 |

#### 限流

- TMDB 客户端自实现令牌桶节流（`TokenBucketRateLimiter`，不依赖 Polly）：**默认 40 req/s**（官方 ~50，留余量），可经 `Tmdb_Setting.RateLimitPerSecond` 调整
- 429 响应：读取 `Retry-After` 头按指示退避，最多重试 3 次

### 3.5 媒体分类系统

#### 3.5.1 自定义分类配置

用户可在 WebUI 设置中创建任意数量的媒体分类，每个分类包含：

| 字段 | 说明 |
|------|------|
| 分类名称 | 如「电影」、「电视剧」、「动漫」 |
| 媒体类型 | 电影 / 剧集（影响命名规则） |
| 目标根目录 | 此分类文件的存放路径 |
| 自动匹配规则（可选） | 条件组合，见下 |

**默认分类参考（用户可修改）：**

| 分类 | 类型 | 目标目录 |
|------|------|---------|
| 电影 | 电影 | `/Media/Movies` |
| 电视剧 | 剧集 | `/Media/TV` |
| 动漫 | 剧集 | `/Media/Anime` |

#### 3.5.2 自动分类规则引擎

分类判断采用**规则优先 + AI 兜底**策略：

1. **规则引擎（优先）：** 按用户定义的条件顺序匹配，命中则直接分类。支持条件字段：
   - `originCountry`（TMDB 来源国，如 `CN`、`US`、`JP`）
   - `originalLanguage`（原始语言，如 `zh`、`en`、`ja`）
   - `genres`（TMDB 类型标签，如 `Animation`、`Documentary`）
   - `type`（movie / tv）

   示例：
   ```
   IF type = tv AND genres CONTAINS Animation → 动漫
   IF type = tv  → 电视剧
   IF type = movie  → 电影
   ```

2. **AI 判断（兜底）：** 规则无匹配时，将 TMDB 元数据发给 AI，请其推荐分类
3. **仍无法确定时：** 进入人工确认队列，用户手动选择分类

### 3.6 文件命名规范（Plex 标准）

> **TmdbId 匹配标记**：电影目录与电影文件名、剧集根目录在「(年份)」后追加 Plex 官方匹配标记 `{tmdb-<id>}`（如 `{tmdb-27205}`），让 Plex / Emby / Jellyfin 直接按 TMDB ID 强制匹配，规避标题歧义导致的刮削错配。**季目录与单集文件名不带标记**（季/集靠 `SxxEyy` 识别，标记冗余）。归档时 TmdbId 必填，故标记恒存在。

#### 3.6.1 电影

```
{分类目录}/{中文片名 (年份)} {tmdb-<id>}/{中文片名 (年份)} {tmdb-<id>}.{ext}
```

示例：

```
/Media/Movies/
  盗梦空间 (2010) {tmdb-27205}/
    盗梦空间 (2010) {tmdb-27205}.mkv
    盗梦空间 (2010) {tmdb-27205}.zh.srt
    盗梦空间 (2010) {tmdb-27205}-trailer.mp4
    盗梦空间 (2010) {tmdb-27205}.nfo
    poster.jpg
    fanart.jpg
```

#### 3.6.2 剧集（包含动漫，统一规范）

```
{分类目录}/{中文剧名 (年份)} {tmdb-<id>}/Season {季号:02d}/{中文剧名 (年份)} - S{季:02d}E{集:02d}.{ext}
```

示例：

```
/Media/TV/Western/
  绝命毒师 (2008) {tmdb-1396}/
    tvshow.nfo
    poster.jpg
    fanart.jpg
    season01-poster.jpg
    Season 01/
      绝命毒师 (2008) - S01E01.mkv
      绝命毒师 (2008) - S01E01.nfo
      绝命毒师 (2008) - S01E01.zh.srt
```

**多季差异化命名（动漫 / 跨年长寿剧）：**

同一部剧的多季落在同一剧集根目录下（按 `{tmdb-<id>}` 唯一识别），但季目录与季内文件名按季差异化：

- **季目录**：`Season {季号:02d}` 后可追加**季标题**，即 `Season {季号:02d} {季标题}`。季标题取 TMDB `seasons[].name`（过滤掉 `Season N` / `第N季` / `Specials` 等无篇章信息的默认季名），缺失则回退文件解析出的篇章名（如「锻刀村篇」）；两者皆无时退化为纯 `Season {季号:02d}`。季标题仅作人类可读后缀，Plex / Emby / Jellyfin 仍靠 `Season NN` 前缀 + `SxxEyy` 识别，不影响刮削。
- **季内单集文件名年份**：`{中文剧名 (年份)} - SxxEyy` 中的 `(年份)` 用**该季首播年**（TMDB `seasons[].air_date`），而非整剧首播年；季 air_date 缺失（未播季等）时回退整剧首播年。
- **剧集根目录年份**：恒为**整剧首播年**，不随季漂移——Plex 按 `{tmdb-<id>}` + 根目录唯一识别整部剧，若根目录年份按季变化会导致同一部剧被识别成多部，故根目录与季维度解耦。

多季示例（鬼灭之刃，季跨年 + 篇章季名）：

```
/Media/TV/Anime/
  鬼灭之刃 (2019) {tmdb-85937}/
    tvshow.nfo
    Season 01/
      鬼灭之刃 (2019) - S01E01.mkv
    Season 03 锻刀村篇/
      鬼灭之刃 (2023) - S03E01.mkv
```

#### 3.6.3 字幕重命名规则

| 文件名中的语言标记 | 映射语言代码 |
|-----------------|------------|
| CHS、SC、简、Chinese | `zh` |
| CHT、TC、繁、Traditional | `zh-TW` |
| ENG、EN、English | `en` |
| JPN、JP、Japanese | `ja` |
| 无法识别 | `und` |

**字幕处理细则：**

| 场景 | 处理 |
|---|---|
| 单视频多语言字幕（`.zh.srt` + `.en.srt`） | 全部保留，按 `{Plex 标准名}.zh.srt` / `{Plex 标准名}.en.srt` 重命名 |
| 同语言多文件（特效、简体、精校） | 取**最大文件**作为主字幕（不加序号），其余追加序号 `.2.zh.srt` / `.3.zh.srt` |
| 字幕语言无法识别 | 后缀 `.und.srt` |
| 字幕压缩包（`.zip` / `.rar`） | **跳过，记录到日志**（不引入解压依赖，避免安全风险） |
| 内嵌字幕（MKV 内置轨） | 不动 |
| 字幕文件与视频不同名但同目录 | **剧集**按季集标记匹配收编（`S01E01` / `第N集` / `EP01` / `[NN]` 与本集一致，字幕无季号视为同季；双集合并归档按区间匹配）；**电影**同目录不收不同名字幕（与无关字幕机器不可区分，防过抓）；匹配不上留在原地 |
| 字幕子目录（`Subs` / `Subtitles` / `Sub` / `字幕`，大小写不敏感，含嵌套限深 3 层） | 纳入扫描：**剧集**按季集标记匹配（目录段与文件名合并提取，文件名优先、目录段从深到浅——支持 `Subs/EP01/chs.srt` 集号在目录段、`Subs/S2/EP01.srt` 季集拆分）；**电影**收编无任何季集标记的字幕（位置即归属信号），守门条件 = 源目录树内无其它视频文件（防平铺多部电影共享 `Subs` 互抢） |
| 字幕语言在目录段（如 `Subs/CHS/xxx.srt` 按语言分目录） | 文件名识别不出语言时，回退用目录段从深到浅识别 |
| 弱集数标记防误抓 | `[NN]` 与纯数字目录段排除分辨率值（480/576/720/1080/2160）与年份（1900–2099）；集数恰为这些值的极端场景按漏匹配处理 |

#### 3.6.4 .nfo 与海报生成

归档完成后由 `MetadataFinalizer` 顺序执行：

**海报：**
- 主海报 `poster.jpg` —— 电影目录或剧集根目录
- Fanart `fanart.jpg` —— 电影目录或剧集根目录
- 季海报 `season{NN}-poster.jpg` —— 剧集根目录

**.nfo（按 Kodi/Plex/Emby 通用约定生成）：**
- 电影：`{中文片名 (年份)} {tmdb-<id>}.nfo`（与电影文件同名；含 TmdbId、标题、年份、剧情简介、Genres、原产国）
- 剧集根目录：`tvshow.nfo`（位于带 `{tmdb-<id>}` 标记的剧集根目录内）
- 单集：`{中文剧名 (年份)} - S{季:02d}E{集:02d}.nfo`（不带标记）

#### 3.6.5 Plex 边界场景

| 场景 | 处理 |
|---|---|
| 特别篇 / OVA | 归入 `Season 00`，命名 `{剧名 (年份)} - S00E{NN}.{ext}` |
| 电影合集 / Collection | 不主动创建 Collection 目录，依赖 TMDB Collection 字段写入 .nfo，由 Plex 自动归集 |
| 电影番外（剧场版动画、衍生短片） | 作为独立电影条目处理（独立 TmdbId） |
| 多版本（导演剪辑 / 院线 / 加长） | 同目录内追加版本后缀 `{片名 (年份)} - {版本}.{ext}`，如「盗梦空间 (2010) - Director's Cut.mkv」 |
| 剧集分卷（同季拆 Volume） | 仍按集号归入同一 Season 目录，忽略 Volume 概念 |

### 3.7 文件操作

| 选项 | 配置 |
|------|------|
| **默认操作** | **移动**（剪切到目标目录） |
| 备选操作 | 复制（保留源文件），可在设置中切换 |
| **同名冲突处理** | 按 `Archive_ConflictPolicy` 四策略可配：**Skip（默认）**——自动跳过，记录到日志，不覆盖已有文件；**Overwrite（升级替换）**——仅当新文件更大（更高清）才替换：删除旧文件并同步清理旧字幕 / 旧 nfo、失效指向该路径的旧记录，新文件不大于已有文件时仍按跳过处理；**KeepBoth（保留多版本）**——自动改用不冲突路径落盘；**Ask（询问）**——不自动决策，将文件放入「待确认」队列（原因标记「名称冲突」），由用户在审核页人工裁定：确认归档即无条件覆盖目标已存在文件（旧文件连同字幕 / nfo 一并删除、失效旧记录），或忽略保留原状 |
| 空目录清理 | 处理完成后若源目录仅剩空文件夹，可选自动清理（默认关闭） |

> **跳过即可见**（Skip 策略，及 Overwrite 判定不替换时）：避免用户「沉默丢失」——所有跳过的文件落 Skipped 终态，出现在仪表盘「跳过待处理」计数中。
> **询问转人工**（Ask 策略）：同名冲突文件不落终态，直接进入「待确认」队列（原因「名称冲突」），用户在审核页逐个裁定「确认归档（覆盖）」或「忽略」。

**与下载器中转目录的特别约定：** 当源目录配置为「中转目录」时，建议开启「自动清理空目录」，让中转目录保持整洁。

### 3.8 人工确认队列

#### 触发场景

| 场景 | 说明 |
|------|------|
| TMDB 零结果 | 完全无法匹配 |
| TMDB 多候选（> N 个） | 需人工选择正确结果 |
| AI 置信度低 | 解析结果不可靠 |
| 分类规则 + AI 均无法判断分类 | 无法确定目标目录 |
| 同名冲突 | 冲突策略判定为跳过时（Skip / Overwrite 不满足替换条件）入队待用户处理 |
| 处理异常 | 文件读写错误等 |

#### WebUI 操作能力

- 展示原始文件名、AI 解析结果、TMDB 候选列表（带海报图）
- 手动输入搜索词重新查询 TMDB
- **手动指定 TMDB ID**（用户粘贴 TMDB URL 或 ID 直接绑定，跳过搜索）
- 修改任意字段：分类、标题、年份、季号、集号
- **季/集重映射**（文件分季方式与 TMDB 不一致时按 TMDB 季结构对齐；兄弟面板按 TMDB 季数自动切换工具）：
  - 文件「多季合并成连续编号」、TMDB 分多季 → 输入绝对集号即按每季集数换算为对应季/集；同目录多文件一键「按绝对集号分季」
  - 文件「标了多季」、TMDB 实为单季 → 同目录多文件一键「合并为单季连续编号」（按原季/集排序重编号为 S01E01.. ，如 S1+S2 → S01E01-E25）
- 确认后立即执行文件操作
- 忽略此文件（从队列移除，保留源文件不处理）
- 支持批量操作：列表头**常驻全选**（全选 / 取消当前过滤结果）、批量确认、批量忽略；抽屉内同目录多集用同一剧集 + 分类一并确认

#### 并发编辑保护

- `Media_Item.RowVersion` 走 EF Core 乐观并发
- 后提交者收到 `1000 BusinessError` + message「记录已被其他用户修改，请刷新」

### 3.9 WebUI 页面结构

| 页面 / 模块 | 主要功能 | 鉴权 |
|------------|---------|------|
| **仪表盘** | 今日处理数量、待处理任务数、最近处理记录、服务运行状态、规则/AI 命中比例统计 | 匿名 |
| **统计分析** | 入库趋势 / 年代 / 评分 / 类型 / 国家 / 分类 / 存储占用 + **AI 参与率 KPI 与识别方式构成**（规则直查 / AI 兜底 / 复用混合 / 强制标识五类构成、AI 参与率 = `AiInvolved` 媒体数 ÷ 窗口内完成归档数、人工审核介入率；「减少 AI 参与度」的效果验证以此为准），支持近30天/近90天/近1年/全部时间窗 | 匿名 |
| **媒体库** | 已归档作品海报墙，按 TMDB 作品聚合（剧集多集归一卡），支持类型/关键词过滤；点卡片进作品详情 | 看：匿名 / 操作：Admin |
| **作品详情** | 单作品 TMDB 元数据 + 该作品全部文件记录（含失败/待确认兄弟集）+ 管理操作（重试/重新处理/删除/整剧/批量）；点单个文件看处理详情 | 看：匿名 / 操作：Admin |
| **待处理队列** | 人工确认任务列表，支持修改 + 确认 + 忽略，标记任务的解析来源（Rule/AI/Hybrid） | 看：匿名 / 操作：Admin |
| **处理历史** | 处理记录日志：按日期/状态/解析来源筛选回看全部终态记录 + 失败重试兜底；行点击转作品详情（有 TmdbId）或文件处理详情 | 看：匿名 / 操作：Admin |
| **日志** | 实时日志流（SignalR）+ 历史日志，支持按级别过滤 | 匿名 |
| **设置 - 常规** | WebUI 端口、文件操作模式、归档冲突策略、最小剩余空间、空目录清理、扫描间隔、备份（开关 / 保留份数 / 立即备份） | Admin |
| **设置 - 监控目录** | 添加 / 删除 / 暂停监控目录；标记某个目录为「中转目录」/「网络共享」 | Admin |
| **设置 - 媒体分类** | 新建 / 编辑 / 删除分类，配置目标路径和自动匹配规则 | Admin |
| **设置 - 解析规则** | 自由新增 / 编辑 / 启停 / 删除规则引擎规则，支持上下移动调整优先级、样例文件名实时测试 | Admin |
| **设置 - 解析测试用例** | 解析回归测试用例管理（失败样本导入 / 批量运行 / 转正与规则建议） | Admin |
| **设置 - AI 提供商** | 配置多级 AI 升级链（地址、Key、模型、优先级、满意度阈值），测试连接，手动解禁 | Admin |
| **设置 - AI 监控** | 按提供商查看调用量 / 成功率 / 平均与 P95 延迟、调用日志 | Admin |
| **设置 - TMDB** | API Key、候选数量阈值、语言偏好、速率限制 | Admin |
| **设置 - 字幕** | Assrt Token（加密落库）、API 地址、超时、走代理开关、连通性测试（显示剩余配额） | Admin |
| **设置 - 代理** | 出站 HTTP 代理（受控出站经代理转发，本地与私有网段直连） | Admin |
| **设置 - 忽略规则** | 配置忽略扩展名、忽略文件名关键词 | Admin |
| **设置 - 媒体扩展名** | 维护「识别为媒体文件」的扩展名清单 | Admin |
| **设置 - 事件 Webhook** | 启用 / Webhook URL / Bearer Token 配置；近 50 条出站事件审计 | Admin |
| **设置 - 账户** | 用户管理（新增 / 删除）、修改密码、查看会话过期、登出 | Admin |
| **设置 - 系统** | 配置导入 / 导出、历史清理、配置重置、备份列表 / 恢复、版本信息 | Admin |
| **设置 - 更新** | 更新检查开关 / 周期 / GitHub PAT（加密落库），立即检查 / 跳过版本 | Admin |

**响应式设计：** WebUI 支持手机 / iPad 访问，局域网内多设备可用。

**前端语言：** 仅简体中文，不预留 i18n 框架。

**通知：** 仅 WebUI 内仪表盘和队列页面展示任务状态（系统级 Win10+ Toast 通知未实现，降级为待定项，见 §十）。如需 IM 推送，由独立的 PlexMediaDownloader 项目（含 Bot 模块）配合 Webhook 实现，本项目不直接对接 IM。

### 3.10 事件 Webhook

#### 支持事件

| 事件 | 触发时机 |
|---|---|
| `media.archived` | 文件归档成功 |
| `media.skipped` | 内容去重命中或归档同名冲突 |
| `media.failed` | 处理异常 |
| `review.created` | 新增人工待确认任务 |
| `backup.failed` | 定时备份失败 |
| `disk.low` | 归档盘剩余空间不足（按盘根 + 抑制窗口去重） |
| `share.unreachable` | 网络共享监控目录掉线（可达性翻转沿触发） |
| `ai.all_unavailable` | 已启用的 AI 提供商全部不可用（未配置 / 全部冷却） |

#### Payload 结构

```json
{
  "event": "media.archived",
  "occurredAt": "2026-05-16T02:00:00+00:00",
  "requestId": "uuid",
  "data": {
    "mediaItemId": 123,
    "sourcePath": "...",
    "targetPath": "...",
    "tmdbId": 27205,
    "type": "movie",
    "title": "盗梦空间",
    "year": 2010,
    "categoryId": 5,
    "metadataPending": false,
    "warnings": []
  }
}
```

**事件字段差异：**

- `media.archived`：`data` 含降级标记 `metadataPending`（bool）与 `warnings`（string[]）——视频已落地但 nfo / 海报等元数据步骤部分失败时 `metadataPending=true` 且 `warnings` 列失败明细（订阅端可据此延迟刷库或提醒补元数据）；正常归档 `metadataPending=false`、`warnings` 为空。
- `media.skipped`：`targetPath` 仅指本记录归档产物，两种触发场景均未产生产物，故为 `null`——内容去重命中未做任何文件操作（已存在副本以处理时间线的 `duplicateOf` 标识）；归档同名冲突不外发他人文件的路径（冲突目标仅记入时间线 detail）。

#### 请求头

- `Content-Type: application/json`
- `User-Agent: PersonalMediaManager/<version>`
- `X-PMM-Event: media.archived`
- `X-PMM-Signature: sha256=<HMAC-SHA256(secret, body)>`（订阅端校验防伪造）

#### 重试策略

- 视为成功：HTTP 2xx
- 失败：非 2xx 或网络异常 → 退避重试 **3 次**，间隔 **30s / 2min / 10min**
- 3 次全失败 → 标记 `Failed` 不再自动重试
- 支持手动重试：`POST /api/settings/webhooks/{id}/retry/{deliveryId}`

#### 出站日志

- 默认保留**近 50 条**
- 设置项可调整范围 50 ~ 500，超出按时间倒序清理

### 3.11 日志系统

| 项 | 配置 |
|---|---|
| 框架 | Serilog |
| Sink | Console + File + SignalR |
| 文件位置 | `<数据根>\logs`；**数据根** = 优先「exe 旁 `data\`」（绿色软件，数据随 exe），该目录不可写时回退 `%LocalAppData%\PersonalMediaManager` |
| 文件命名 | `pmm-yyyyMMdd.log` |
| 滚动策略 | 按天 + 单文件 10MB 强制切割 |
| 保留时长 | **30 天**自动清理 |
| 默认级别 | `Information`，可在设置中改为 `Debug` |
| SignalR 推送 | 仅 `Information` 及以上推送（避免 Debug 刷屏） |
| 推送限流 | 单连接 100 条/秒，超出丢弃并记一条「日志推送限流」提示 |
| 前端订阅 | 支持级别过滤（Info / Warn / Error）+ 关键词过滤 |
| 历史查询 | `GET /api/logs` 走文件分页读取，不入库 |

### 3.12 账户与认证

#### 鉴权方案

- **JWT HS256**，载荷 `{ userId, username, role, iat, exp }`
- **有效期 30 天**（长期有效）
- **无感续签**：每次请求若 `exp - now < 7 天`，后端在响应头 `X-Token-Refresh` 下发新 Token，前端 openapi-fetch 中间件自动替换 localStorage
- 签名密钥：优先读配置 `Jwt:SigningKey`（`appsettings.json` 已嵌入程序集、只读出厂默认，运行期不可写回）；不存在则随机生成 256 位并持久化到数据根 `jwt-signing-key.txt` 兜底文件

#### 角色模型

| 角色 | 权限 |
|---|---|
| **Admin** | 修改任意配置、管理用户、确认队列、触发扫描 |
| **Viewer** | 仅看仪表盘 / 队列 / 历史 / 日志（只读） |

#### 匿名访问范围

| 范围 | 行为 |
|---|---|
| 仪表盘、待处理队列（看）、处理历史、日志、SignalR Hub | **无需登录** |
| 所有 `/api/settings/**`、`/api/account/**`、`/api/scan/trigger`、`/api/review/*` 写操作 | 强制登录（Admin） |

#### 密码策略

- ≥ 6 位，无字符种类强制
- 存储：BCrypt（cost = 10）

#### 登录失败处理

- **不锁定**账号
- 仅在 `Audit_Operation` 记录失败事件（含 IP、用户名）

#### 首次向导

- 必须创建 1 个 `Admin`
- 不接受空密码
- 完成后才允许进入主界面

### 3.13 字幕下载（手动触发，2026-06-12 新增）

**定位：** 对**已归档完成**（`Completed` 且 `TargetPath` 文件存在）的媒体记录，从外部字幕源搜索并下载字幕到视频旁。**全程手动**：搜索由用户点击触发、字幕由用户从候选中亲自选择，不做任何自动下载/定时下载。

**字幕源：** 首发 **Assrt**（伪·射手网 `api.assrt.net`，免费注册 Token，限流 20 次/分钟）。架构按多源策略设计（`SubtitleProviderType` 枚举 + `ISubtitleProvider` 策略接口 + 字典路由，照 AI 提供商模式），后续可扩展 OpenSubtitles 等（其下载需用户 JWT 且免费档每日 5 次，暂不纳入）。

**交互流：**
1. 入口：处理历史 / 作品详情 / 文件处理详情页对 `Completed` 记录提供「下载字幕」操作
2. 弹窗预填搜索词 = 原始文件名去扩展名（发布组命名在字幕站命中率最高），可编辑重搜
3. 候选列表（名称 / 语言 / 格式 / 上传时间 / 评分）→ 点候选展开文件列表（压缩包字幕由 Assrt 侧展开为单文件直链）→ 逐文件「下载」
4. 弹窗顶部展示该记录已下载字幕，提示避免重复下载

**落地规范：** 复用归档管线 `SubtitleRenamer` 体系——语言码按文件名识别（chs/简→`zh`、cht/繁→`zh-TW`、eng→`en`…识别失败回退源站语言描述映射）；落地名 `{视频基名}.{语言码}{扩展名}`，与视频同目录；同名已存在序号避让 `.2`～`.9`（语言段紧贴扩展名，Plex 才能识别），**绝不覆盖既有文件**。

**安全设计：**
- Token 走 `IProtectedFieldService` 加密落库（与 TMDB ApiKey 同级），请求用 `Authorization: Bearer` 头（日志脱敏已覆盖）
- 下载直链由服务端按候选 Id 重新取得，前端不传 URL（杜绝 SSRF）；落地文件名完全服务端自构，外部文件名只取扩展名且过白名单（`.srt .ass .ssa .sub .vtt .idx .smi`），杜绝路径穿越
- 文件大小安全阀 10MB；客户端限流 1 次/3.2 秒守住 Assrt 配额

**数据：** `Subtitle_Setting`（单例配置）+ `Subtitle_Download`（成功下载记录，媒体删除级联清理）；下载动作不写 `Process_Step` 时间线（时间线只记处理管线阶段，字幕属归档后置手动操作）。

---

## 四、数据库设计概览

### 4.1 表清单

**命名约定：** 表名采用 `业务域_实体` 两段式 PascalCase 格式（如 `Watch_Folder`、`Media_Item`）；业务域按 PMM 实际模块划分，无固定枚举值，新增业务域时按本节风格扩展即可。所有表均含 `CreatedAt` / `UpdatedAt`；需乐观并发的加 `RowVersion`。

当前业务域划分：

| 业务域前缀 | 含义 |
|---|---|
| `System_` | 系统级配置 |
| `User_` | 账号与会话 |
| `Watch_` | 文件监控（监控目录、忽略规则） |
| `Parse_` | 媒体名称解析（规则引擎、AI 提供商） |
| `Tmdb_` | TMDB 配置与缓存 |
| `Category_` | 媒体分类（定义、匹配规则） |
| `Media_` | 媒体处理记录（业务主表） |
| `Subtitle_` | 字幕下载（字幕源配置、下载记录） |
| `Webhook_` | Webhook 订阅与出站日志 |
| `Audit_` | 操作审计、外部调用统计 |

| 表名 | 用途 | 关键字段 |
|---|---|---|
| **System_Setting** | 全局 KV 配置（端口、阈值、操作模式等） | `Key` PK, `Value`, `Category` |
| **User_Account** | 用户账号 | `Id` PK, `Username` UQ, `PasswordHash`, `Role`, `LastLoginAt` |
| **Watch_Folder** | 监控目录 | `Id`, `Path`, `IsTransit`, `IsNetworkShare`, `Enabled`, `Priority` |
| **Watch_IgnoreRule** | 忽略规则（扩展名 / 关键词） | `Id`, `Type` (`Extension`/`Keyword`), `Pattern`, `Enabled` |
| **Parse_Rule** | 解析规则引擎规则 | `Id`, `Name`, `Scope`, `Pattern`, `DefaultType`, `Priority`, `ConfidenceBonus`, `Enabled` |
| **Parse_AiProvider** | AI 提供商配置 | `Id`, `Type`, `BaseUrl`, `ApiKeyEncrypted`, `Model`, `IsPrimary`, `Priority`, `Enabled`, `DisabledUntil` |
| **Tmdb_Setting** | TMDB 配置（独表，便于加密 ApiKey） | `Id`(常 1), `ApiKeyEncrypted`, `Language`, `CandidateThreshold`, `RateLimitPerSecond` |
| **Tmdb_MetadataCache** | TMDB 元数据缓存 | `TmdbId + MediaType` 联合 PK, `Title`, `OriginalTitle`, `Year`, `PosterPath`, `OriginCountry`, `OriginalLanguage`, `Genres` (JSON), `RawJson`, `CachedAt` |
| **Tmdb_SearchCache** | TMDB 搜索结果缓存（短期） | `QueryHash` PK, `Results` (JSON), `CachedAt` |
| **Category_Definition** | 媒体分类定义 | `Id`, `Name`, `MediaType`, `TargetRoot`, `Priority` |
| **Category_MatchRule** | 分类自动匹配规则 | `Id`, `CategoryId` FK → `Category_Definition.Id`, `Conditions` (JSON), `Priority`, `Enabled` |
| **Media_Item** | 媒体处理主记录（含状态机） | `Id`, `SourcePath`, `FileSize`, `FileHash`, `Status`, `ParsedInfo` (JSON), `TmdbId`, `CategoryId`, `TargetPath`, `ParseSource`, `Confidence`, `ErrorMessage`, `RowVersion` |
| **Subtitle_Setting** | 字幕源配置（独表，便于加密 Token） | `Id`(常 1), `AssrtTokenEncrypted`, `AssrtBaseUrl`, `TimeoutSeconds`, `UseProxy`, `RowVersion` |
| **Subtitle_Download** | 字幕成功下载记录 | `Id`, `MediaItemId` FK → `Media_Item.Id` (CASCADE), `Provider`, `SubtitleName`, `FileName`, `TargetPath`, `Language`, `FileSize` |
| **Webhook_Subscription** | Webhook 订阅 | `Id`, `Url`, `SecretEncrypted`, `Events` (JSON 数组), `Enabled` |
| **Webhook_Delivery** | Webhook 出站日志 | `Id`, `SubscriptionId` FK → `Webhook_Subscription.Id`, `Event`, `Payload`, `Status`, `Attempts`, `LastTriedAt`, `LastError` |
| **Audit_Operation** | 操作审计（当前仅 Auth 模块写入，覆盖边界见数据库设计 §1.15） | `Id`, `UserId`, `Action`, `Target`, `Detail`, `Timestamp` |
| **Audit_AiCall** | AI 调用统计（用于失败禁用判定） | `Id`, `ProviderId` FK → `Parse_AiProvider.Id`, `Success`, `LatencyMs`, `ErrorType`, `Timestamp` |
| **Audit_ScheduledTaskRun** | 定时任务运行审计（备份 / 定时扫描等） | （字段详见数据库设计文档） |
| **Parse_TestCase** | 解析规则回归测试用例 | （字段详见数据库设计文档） |
| **System_MediaExtension** | 「识别为媒体文件」的扩展名清单 | （字段详见数据库设计文档） |
| **Process_Step** | 媒体处理时间线（单条记录的逐步骤明细） | （字段详见数据库设计文档） |
| **媒体库富化维度 / 连接表** | `Media_Work` / `Media_Season` / `Media_Episode` / `Media_Person` / `Media_Genre` / `Media_Keyword` / `Media_Company` / `Media_Network`，及连接表 `Media_WorkCredit` / `Media_WorkGenre` / `Media_WorkKeyword` / `Media_WorkCompany` / `Media_WorkNetwork` | 详见数据库设计「媒体库富化」 |

### 4.2 索引建议

- `Media_Item(Status, CreatedAt)` —— 队列扫描
- `Media_Item(SourcePath)` UQ —— 避免同文件重复入队
- `Audit_AiCall(ProviderId, Timestamp)` —— 滚动窗口失败统计
- `Webhook_Delivery(SubscriptionId, Status, LastTriedAt)` —— 失败重试扫描

### 4.3 敏感字段加密

走 ASP.NET Core DataProtection：

- `Parse_AiProvider.ApiKeyEncrypted`
- `Tmdb_Setting.ApiKeyEncrypted`
- `Webhook_Subscription.SecretEncrypted`
- `System_Setting` 中的 `Update_GitHubPat`（更新检查用 GitHub PAT，经 `IProtectedFieldService` 加密落库，见 CLAUDE.md §9.5）
- `User_Account.PasswordHash`（BCrypt，不走 DataProtection）

DataProtection 密钥环存储位置：`<数据根>\keys`（数据根 = 优先「exe 旁 `data\`」，不可写时回退 `%LocalAppData%\PersonalMediaManager`，见 §3.11）。密钥环随数据根走，备份 / 迁移必须与 `pmm.db` 同组搬运，否则加密字段成孤儿密文不可解

---

## 五、HTTP API 概览

### 5.1 约定

- 基地址：`/api`（全局统一前缀，无版本段；`/openapi/v1.json` 为 OpenAPI 文档路由，非 API 基地址）
- 统一响应体：`{ code, message, data, requestId }`（见 CLAUDE.md §六 三码原则）
- 鉴权：`Authorization: Bearer <jwt>`，需鉴权接口未带 token 返 `1000`
- 分页：`?page=1&pageSize=20`，响应 `data: { items, total, page, pageSize }`
- Token 续签：响应头 `X-Token-Refresh: <newJwt>`(条件触发)
- **HTTP 谓词限定**：**仅使用 `GET` 与 `POST` 两个谓词**，不使用 `PUT` / `DELETE` / `PATCH`。
  - `GET` —— 读取（列表、详情、查询）
  - `POST` —— 所有写入操作；新增 = `POST` 资源根（无 `/create` 段），修改 / 删除 = `POST {资源}/update`、`POST {资源}/delete`（`id` 放请求体），其余动作显式放路径末段（`/test`、`/enable`、`/cache/clear`、`/{id}/confirm` 等；少数端点带路径 `{id}`，以下表为准）
  - 单例资源（如 `/api/settings/general`、`/api/settings/tmdb`）：读取用 `GET`，更新统一用 `POST /api/xxx/update`

### 5.2 端点清单

| 模块 | Method | 路径 | 用途 | 鉴权 |
|---|---|---|---|---|
| **认证** | POST | `/api/auth/login` | 登录 | 否 |
| | POST | `/api/auth/logout` | 登出 | 是 |
| | GET | `/api/auth/me` | 当前用户信息 | 是 |
| **初始化** | GET | `/api/setup/status` | 初始化进度 | 否 |
| | POST | `/api/setup/admin` | 创建首个管理员 | 否 |
| | POST | `/api/setup/complete` | 完成向导 | 是 |
| **健康检查** | GET | `/api/health` | 存活 / DB 连通探针 | 否 |
| **账号管理** | GET | `/api/account/users` | 用户列表 | Admin |
| | POST | `/api/account/users/create` | 新增用户 | Admin |
| | POST | `/api/account/users/{id}/delete` | 删除用户 | Admin |
| | POST | `/api/account/password/change` | 改自己密码 | 是 |
| **仪表盘** | GET | `/api/dashboard/stats` | 今日/总计/命中率 | 否 |
| | GET | `/api/dashboard/recent` | 最近处理 | 否 |
| | GET | `/api/dashboard/health`、`/tasks`、`/heatmap`、`/watch-folder-activity` | 运行状态 / 任务卡 / 热力图 / 目录活跃度 | 否 |
| **队列** | GET | `/api/review` | 待确认列表 | 否（看） |
| | POST | `/api/review/{id}/confirm` | 确认归档 | Admin |
| | POST | `/api/review/{id}/ignore` | 忽略 | Admin |
| | POST | `/api/review/batch-confirm` | 批量确认 | Admin |
| | POST | `/api/review/batch-ignore` | 批量忽略 | Admin |
| | GET | `/api/review/{id}/tmdb-search?q=` | 重新搜索 TMDB | Admin |
| | GET | `/api/review/{id}/tmdb-detail` | 候选 TMDB 详情 | Admin |
| | POST | `/api/review/{id}/bind-tmdb` | 手动指定 TmdbId | Admin |
| | POST | `/api/review/preview-paths` | 预览归档目标路径 | Admin |
| | POST | `/api/review/check-files` | 批量检查源文件存在性 | Admin |
| **历史** | GET | `/api/history`、`/pending`、`/{id}` | 历史列表（过滤）/ 待处理 / 详情 | 否 |
| | GET | `/api/history/{id}/nfo`、`/{id}/poster` | 查看 nfo / 海报 | 否 |
| | POST | `/api/history/{id}/rescan` | 重新处理 | Admin |
| | POST | `/api/history/{id}/preview-archive`、`/{id}/manual-archive` | 归档预览 / 手动归档 | Admin |
| | POST | `/api/history/{id}/undo-archive` | 撤销归档（轻量回滚，见 §十） | Admin |
| | POST | `/api/history/{id}/reopen` | 重开进待确认队列 | Admin |
| | POST | `/api/history/rescan-failed`、`/reprocess`、`/delete` | 失败批量重试 / 重新处理 / 删除记录 | Admin |
| **日志** | GET | `/api/logs` | 历史日志（过滤） | 否 |
| **扫描** | POST | `/api/scan/trigger` | 手动全量扫描 | Admin |
| | POST | `/api/scan/folder/{id}` | 单目录扫描 | Admin |
| | POST | `/api/scan/path`、`/api/scan/dry-run` | 指定路径扫描 / 演练（仅预览不落盘） | Admin |
| **设置-通用** | GET | `/api/settings/general` | 读取通用配置 | Admin |
| | POST | `/api/settings/general/update` | 更新通用配置 | Admin |
| **监控目录** | GET | `/api/settings/watch/folders` | 列表 | Admin |
| | POST | `/api/settings/watch/folders` | 新增 | Admin |
| | POST | `/api/settings/watch/folders/update` | 修改（id 在请求体） | Admin |
| | POST | `/api/settings/watch/folders/delete` | 删除（id 在请求体） | Admin |
| | GET | `/api/settings/watch/folders/{id}/test` | 连通性测试（SMB） | Admin |
| **忽略规则** | GET / POST | `/api/settings/watch/ignore-rules`（`/update`、`/delete`） | 列表 / 新增 / 修改 / 删除（id 在请求体） | Admin |
| **媒体扩展名** | GET / POST | `/api/settings/media-extensions`（`/update`、`/delete`） | 媒体扩展名清单 CRUD | Admin |
| **媒体分类** | GET / POST | `/api/settings/categories`（`/update`、`/delete`） | 分类 CRUD | Admin |
| **分类匹配规则** | GET / POST | `/api/settings/category-match-rules`（`/update`、`/delete`） | 自动匹配规则 CRUD | Admin |
| **解析规则** | GET / POST | `/api/settings/parse-rules`（`/update`、`/delete`） | 规则 CRUD | Admin |
| | GET | `/api/settings/parse-rules/builtin` | 内置规则列表 | Admin |
| | POST | `/api/settings/parse-rules/import`、`/test` | 批量导入 / 样例测试 | Admin |
| **解析测试用例** | GET / POST | `/api/settings/parse-testcases`（`/update`、`/delete`、`/run`、`/run-all`、`/import-from-failed`、`/promote`、`/approve`、`/triage`、`/suggest-rule` 等） | 回归用例管理与批量执行 | Admin |
| **AI 提供商** | GET / POST | `/api/settings/ai-providers`（`/update`、`/delete`） | 提供商 CRUD | Admin |
| | POST | `/api/settings/ai-providers/test` | 测连接（id 在请求体） | Admin |
| | POST | `/api/settings/ai-providers/enable` | 手动启用（解禁，id 在请求体） | Admin |
| | GET | `/api/settings/ai-providers/{id}/stats`、`/{id}/logs` | 调用统计 / 调用日志 | Admin |
| **TMDB** | GET | `/api/settings/tmdb` | 读取配置 | Admin |
| | POST | `/api/settings/tmdb/update`、`/test`、`/cache/clear` | 更新 / 测连接 / 清空缓存 | Admin |
| **字幕设置** | GET | `/api/settings/subtitle` | 读取配置（Token 脱敏为 hasToken） | Admin |
| | POST | `/api/settings/subtitle/update`、`/test` | 更新（Token 三态）/ 测连接（返剩余配额） | Admin |
| **字幕下载** | GET | `/api/media/{id}/subtitles/search?keyword=` | 搜索候选（keyword 缺省用原始文件名） | Admin |
| | GET | `/api/media/{id}/subtitles/files?subtitleId=` | 候选文件列表（不暴露直链） | Admin |
| | POST | `/api/media/{id}/subtitles/download` | 下载所选文件落到视频旁 | Admin |
| | GET | `/api/media/{id}/subtitles/downloads` | 该记录已下载字幕列表 | Admin |
| **Webhook** | GET / POST | `/api/settings/webhooks`（`/update`、`/delete`） | 订阅 CRUD | Admin |
| | GET / POST | `/api/settings/webhooks/enabled` | 总开关读 / 写 | Admin |
| | POST | `/api/settings/webhooks/{id}/test` | 发送测试事件 | Admin |
| | GET | `/api/settings/webhooks/deliveries` | 出站日志 | Admin |
| | POST | `/api/settings/webhooks/{id}/retry/{deliveryId}` | 手动重试 | Admin |
| **系统** | POST | `/api/system/export` | 导出 db 快照 + 密钥环（见 §7.2） | Admin |
| | POST | `/api/system/import` | 导入 | Admin |
| | GET | `/api/system/info`、`/api/system/version` | 版本、运行时间、平台 | 否 |
| | POST | `/api/system/backup`、`/restore`；GET `/api/system/backups` | 立即备份 / 恢复 / 备份列表 | Admin |
| | POST | `/api/system/clear-history`、`/reset-config` | 清理历史 / 重置配置 | Admin |
| | GET / POST | `/api/system/update-check`（`/run`、`/test`、`/skip`） | 更新检查配置读取与手动触发 / 测试 / 跳过版本 | Admin |
| **媒体库** | GET / POST | `/api/library...` | 海报墙 / 详情 / 富化 / 存在性扫描（端点明细见文末「媒体库增强需求」） | 浏览匿名 / 刷新扫描 Admin |
| **诊断** | GET | `/api/diag/boom` | 故意抛异常，验证统一错误信封（诊断用） | 否 |

### 5.3 SignalR Hub

| Hub | 路径 | 事件 |
|---|---|---|
| LogHub | `/hubs/logs` | `logEntry { level, message, timestamp, source }` |
| TaskHub | `/hubs/tasks` | `taskStatusChanged { id, status, message }`、`queueChanged { reviewCount, todayProcessed }` |

---

## 六、任务状态机

### 6.1 `Media_Item.Status` 枚举

| 值 | 名称 | 含义 |
|---|---|---|
| 0 | `Detected` | 文件被发现，等待写入完成判定 |
| 10 | `Queued` | 写入完成，已入处理队列 |
| 20 | `Parsing` | 规则引擎解析中 |
| 30 | `TmdbMatching` | TMDB 查询中（规则结果） |
| 40 | `AiParsing` | AI 兜底解析中 |
| 50 | `TmdbRematching` | TMDB 二次查询（AI 结果） |
| 60 | `Classifying` | 分类判断中 |
| 70 | `AwaitingReview` | 进入人工确认队列 |
| 80 | `Archiving` | 文件操作 + nfo/海报生成中 |
| 100 | `Completed` | 成功完成 |
| 110 | `Skipped` | 同名冲突或被忽略规则跳过 |
| 120 | `Ignored` | 用户在确认队列手动忽略 |
| 130 | `Failed` | 处理异常（含 IO/网络/未预期） |

### 6.2 核心流转

```
Detected → Queued → Parsing
        ├→ (置信度≥阈值) → TmdbMatching ┬→ (唯一/≤N) → Classifying
        │                                ├→ (>N 或 零结果) → AiParsing → TmdbRematching ┬→ Classifying
        │                                                                                └→ AwaitingReview
        └→ (置信度<阈值) → AiParsing → TmdbRematching → 同上
Classifying ┬→ (规则命中) → Archiving → Completed | Skipped | Failed
            ├→ (AI 命中) → Archiving
            └→ (均失败)  → AwaitingReview
AwaitingReview → (人工确认) → Archiving | Ignored
```

### 6.3 并发控制

- 后台 `TaskProcessor` 全局 `SemaphoreSlim(1, 1)`：**一次只处理 1 个文件**
- TMDB / AI 等外部调用层节流（自实现令牌桶，不依赖 Polly）：
  - TMDB 默认 40 req/s（`Tmdb_Setting.RateLimitPerSecond` 可配）
  - AI 单 provider 1 req/s

### 6.4 SignalR 推送

每次状态变更广播 `taskStatusChanged { id, status, message }`，前端列表实时刷新；同时广播 `queueChanged` 用于仪表盘计数。

---

## 七、配置管理

### 7.1 配置优先级

- `appsettings.json` 已嵌入程序集（只读出厂默认），仅作 **bootstrap**（端口、初始日志级别等出厂值）；JWT 签名密钥优先读配置 `Jwt:SigningKey`，缺省时生成并持久化到数据根 `jwt-signing-key.txt`（运行期不写回 appsettings，见 §3.12）
- 其余配置项以 `System_Setting` / 各 `*_Settings` 与 `Watch_*` / `Parse_*` / `Tmdb_*` / `Category_*` / `Webhook_*` 等表为准
- **开发阶段约定**：避免同一 key 同时出现在 json 与 DB 中；如出现冲突 **DB 优先**

### 7.2 配置导入导出

- **导出**：`POST /api/system/export` 返回一个 `.zip`，含：
  - `pmm.db` 快照（SQLite `VACUUM INTO` 在线副本）
  - DataProtection 密钥环（加密字段解密所需；**不含 `appsettings.json`**——其已嵌入程序集、只读出厂默认，无需随数据迁移）
- **导入**：`POST /api/system/import` 上传同格式 `.zip`，导入前自动备份当前 `pmm.db`
- **使用场景**：换机迁移、本地备份

---

## 八、安全设计

### 8.1 鉴权

见 §3.12。

### 8.2 凭据保护

- DataProtection 密钥环存本地用户目录（见 §4.3）
- TMDB API Key、AI API Key、Webhook Secret 入库前加密
- 用户密码 BCrypt（cost=10）

### 8.3 网络

- 默认监听 `0.0.0.0:7288`，**仅 HTTP**
- HTTPS 不在 MVP 范围（用户场景为局域网；如需公网访问，建议前置反向代理 Caddy / Nginx）

### 8.4 正则安全

- 用户自定义正则强制 `MatchTimeout = 500ms`，防 ReDoS

### 8.5 路径安全

- 所有文件操作前校验目标路径在配置的「分类目标根目录」之下，防路径穿越
- 上传 `.zip` 导入时严格校验解压目标，防 Zip Slip

### 8.6 平台特定

- **Windows**：首次监听 `0.0.0.0:7288` 会触发防火墙弹窗。产品无安装器（单文件绿色 exe），Launcher 以 asInvoker 运行不提权；首次启动按需弹 UAC 写入入站规则（PowerShell `New-NetFirewallRule`），用户拒绝提权时降级为托盘气泡提示，不阻断运行。

---

## 九、目录与解决方案结构

> **本节已过期**：旧 6 项目结构（PMM.Launcher / PMM.Web / PMM.Core / PMM.Infrastructure / PMM.Shared / PMM.Frontend）**不再使用**。
>
> **当前唯一权威**：`CLAUDE.md` §十二「解决方案结构与项目命名」——7 src + 6 tests 项目矩阵 + 强制单向引用关系图 + 内部目录纪律。
>
> **开发执行口径**：`docs/开发计划.md` §二~§七 已按 `CLAUDE.md` §十二 完整展开各项目的目录与子任务。

---

## 十、待定与后置事项

| 项 | 状态 | 备注 |
|---|---|---|
| HTTPS 支持 | 后置 | 局域网场景不优先；用户如需公网访问自行前置反向代理 |
| i18n | 不做 | 仅简体中文 |
| 自更新（自动覆盖二进制） | 不做 | 不内置静默自更新；用户手动下载新版覆盖安装 |
| 更新检查（GitHub Releases） | 已实现 | `UpdateCheckWorker` 周期查 `releases/latest`（`GitHubUpdateChecker`）；前端 `settings/UpdateSettings.vue` 可开关/调周期；检查用 PAT 加密落库，见 CLAUDE.md §9.5 |
| 数据回滚（归档撤销） | 已实现（轻量版） | 一键撤销最近归档：按审计链 / 文件系统状态反向 move 归档文件回源位（当初 Copy 则删归档副本），记录回退为 Skipped（终态、不自动重归档）；`HistoryService.UndoArchiveAsync` + `POST /history/{id}/undo-archive`，历史页 / 作品详情页菜单触发。原"文件不可逆"前提不成立——归档即 `MoveAsync`，本可逆；Overwrite 删除的旧版本不可逆但与撤销当次归档无关 |
| 数据库自动备份 | 已实现 | `BackupJob` 每日 04:00 触发 `BackupService`：SQLite `VACUUM INTO` 在线快照 + 密钥环打包到 `<数据根>/backups/pmm-backup-*.zip`，按 `Backup_RetainCount` 保留份数清理；开关 `Backup_Enabled`。手动「立即备份」端点 `POST /system/backup`，设置在「常规→备份」 |
| 内容去重（FileHash） | 已实现 | 文件处理落库前算采样 SHA256（小文件全量 / 大文件头 8MB+尾 8MB+大小），命中已 `Completed` 的同 hash 记录则直接 `Skipped`、不重复归档；`IFileHasher` + `ProcessFileService` 接通悬空的 `Media_Item.FileHash` 列 |
| 系统级 Toast 通知 | 待定 | 原 §3.9 规格（Win10+ Toast 推送「任务失败」「人工队列首次非空」）未实现；当前通知能力 = WebUI 实时展示 + Webhook 出站 + 托盘气泡（Launcher 自身事件），是否补做待定 |
| Prometheus / `/metrics` 指标 | 不做 | 桌面应用，无 Prometheus 部署场景 |
| 容器化 | 永久禁止 | 见 §2.3 红线 |

---

## 十一、术语表

| 术语 | 说明 |
|---|---|
| PMM | PersonalMediaManager 缩写 |
| 监控目录 | 用户指定的文件源目录，文件落地后由 PMM 自动处理 |
| 中转目录 | 标记 `IsTransit = true` 的监控目录，下载器先落到此处再由 PMM 搬移；建议开启自动清理空目录 |
| 网络共享目录 | 标记 `IsNetworkShare = true` 的目录，PMM 额外做可达性检测 |
| 分类目标根目录 | 媒体分类的存放路径，PMM 按 Plex 规范在此目录下建立子结构 |
| 待确认队列 | 自动处理失败的文件集合，需用户在 WebUI 手动确认 |
| AI 兜底 | 规则引擎结果不可靠时调用 AI 二次解析的兜底流程 |
| 解析来源（ParseSource） | `Rule` / `AI` / `Hybrid`，标记某条媒体记录的解析路径 |
| 哨兵文件 | `.complete`，由下载器在目录交付完成时创建，PMM 见之即处理 |

## 媒体库增强需求（2026-05-30）

在「已归档作品海报墙 + 作品详情」基础上扩展，覆盖 5 项用户诉求：

1. **更多媒体信息**：作品详情呈现 TMDB 富化元数据——背景图、标语、评分（vote_average/count）、时长、状态（连载/完结）、首播日期、原产国、语言、官网、季/集总数。
2. **剧集分季分集 + 每集简介**：剧集详情按季分 Tab，展开某季惰性拉取该季分集（集名、每集简介、剧照、时长、评分），并标注每集在本地是否已归档（present/缺失）。
3. **文件存在检查**：作品详情对每个文件实时 `File.Exists` 标注；另提供「扫描整库存在性」把缺失结果落库（`Media_Item.FileMissing`），媒体库列表/卡片据此打「缺失 N」标记。
4. **关联信息**：演员、导演/主创（剧组按关键职务白名单保留）、制作公司、电视台、类型(genre)、关键词/标签、国家、语言。数据走关系型维度表 + 连接表（详见数据库设计「媒体库富化」）。
5. **库内搜索与关联**：媒体库支持按 类型/分类/类型(genre)/国家/语言/年份/演员·导演/公司/电视台/关键词 过滤与排序（recent/year/rating/title）；详情页 chip 可点跳转按维度筛选；并给出「相关作品」（按共享 类型/演职员/公司 计分）。

**富化触发**：详情打开惰性富化单部（TTL 内跳过；失败降级返回已有数据），分季分集首次展开惰性拉取，另有「整库刷新元数据」批量回填供历史作品进入搜索。所有 TMDB 图片（背景图/人物照/剧照/Logo）经后端 `/library/tmdb-image` 代理 + 落盘缓存（与海报同隐私模型，浏览器不直连 TMDB）。

**新增 API**（均挂 `/library`）：`facets`、`{tmdbId}/seasons/{n}`、`{tmdbId}/related`、`{tmdbId}/refresh-metadata`、`refresh-metadata-all`、`scan-existence`、`tmdb-image/{size}/{*path}`；`GET /library` 扩展维度/排序查询参数。浏览类匿名只读，刷新/扫描类需登录。
