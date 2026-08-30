# PersonalMediaManager 阶段 F 操作手册

> 版本：v1.0  
> 最后更新：2026-06-11（移除 macOS 支持，冒烟仅 Windows）  
> 适用范围：发布候选版打 tag 之前的人工 / 半人工验收  
> 上游依据：`docs/开发计划.md` §七、`CLAUDE.md` §六/八/九  
> 阶段 F 自动化部分（F.2 缓存命中 + F.3 安全自检 + F.2 in-memory 端到端基线）已在 `dotnet test` 跑过；本手册只覆盖**必须真人介入**或**依赖外部资源**的部分。

---

## 〇、总则

### 0.1 执行场景

| 场景 | 用本手册的哪几章 |
|---|---|
| 给 Release tag 前的最终验收 | 一 ~ 三 全跑 |
| 修了 D 阶段管线 Bug 想自检 | 一（按改动定位用例） |
| 装新机 / 换运行环境想冒烟 | 三（Windows UX） |
| Bug 复盘 / 故障排查 | 四（故障排查速查） |

### 0.2 前置环境

| 依赖 | 来源 | 备注 |
|---|---|---|
| 真 TMDB API Key | https://www.themoviedb.org/settings/api 注册 | 个人开发者免费；首次配置后写入「设置 → TMDB」加密落库 |
| 真 Ollama | https://ollama.com 本机部署 | 推荐 `ollama run qwen2.5:7b`；监听 `http://localhost:11434`，不需 API Key |
| 真 SMB 网络盘 | 任意一台 NAS（群晖 / 威联通 / Windows 共享） | 测试 IP 文中以 `<nas-ip>` 占位，按实际替换 |
| Windows 11 测试机 | 双击 .exe 验收 | 关闭 Windows Defender 实时扫描或允许例外，避免单文件发布解压被拦 |
| 测试样本媒体文件 | 可空 mkv 占位即可 | 文件名才是关键，内容用 `New-Item -ItemType File` 或 `touch` 创建零字节 |

### 0.3 路径约定

> 产品仅支持 Windows（macOS 支持已移除，2026-06-11 决策）。

| 项 | Windows |
|---|---|
| 数据根 | 优先「exe 旁 `data\`」；exe 目录不可写（如装入 `Program Files\`）时回退 `%LocalAppData%\PersonalMediaManager\`。下文记作 `<数据目录>` |
| 日志 | `<数据目录>\logs\pmm-*.log` |
| 数据库 | `<数据目录>\pmm.db` |
| 海报缓存 | `<数据目录>\cache\posters\` |
| 自启注册 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → 值名 `PersonalMediaManager` |
| WebUI | `http://localhost:7288/` |

---

## 一、F.1 真服务集成 E2E（依赖外部资源）

> 设计目标：覆盖 D9 自动化 E2E 用 Stub 跳过的两类真实外部依赖 — TMDB 服务、AI 提供商；以及 D6.4 NetworkShareMonitorWorker 在真 SMB 共享上的可用性。  
> 执行频次：**每次 Release 前一遍**；常规迭代不跑（避免外网 quota 与 NAS 抖动污染日志）。

### F.1.1 真 TMDB 电影端到端

**用例标识**：F1.1  
**目标**：标准命名电影从「投递到监控目录」到「归档目录 Plex 命名 + nfo + 海报」全链路打通，TMDB 候选与详情走真服务。

**前置准备**：

1. 启动应用：双击 `dist/win/PersonalMediaManager.exe`。
2. WebUI 进初始化向导，设管理员账号 → 完成。
3. 设置 → TMDB → 粘贴真 API Key → 保存。
4. 设置 → 分类 → 新增「电影」分类：
   - 名称：`电影`
   - 媒体类型：`Movie`
   - 目标根：`F:\TestMedia\Movies` — 提前 `mkdir`。
   - 匹配规则：默认 `mediaType == movie` 即可。
5. 设置 → 监控目录 → 新增：路径 = `F:\TestSource`。

**执行步骤**：

```powershell
# Windows
New-Item -Path "F:\TestSource\Inception.2010.1080p.x265.mkv" -ItemType File -Value $null
```

**验收 checklist**：

- [ ] WebUI「待处理」页 ≤2s 出现新文件（D6.1 + D6.2）
- [ ] 30s 内自动转 `Completed`（不需要走 AI 兜底）
- [ ] 目标目录出现 `F:\TestMedia\Movies\Inception (2010)\Inception (2010).mkv`
- [ ] 同目录 `Inception (2010).nfo` 写入 TMDB 中文标题 `<title>盗梦空间</title>` 与 `<tmdbid>27205</tmdbid>`
- [ ] 同目录 `poster.jpg` 已下载（可用图片查看器打开）
- [ ] 数据库 `Media_Item` 行 `TmdbId=27205` `Status=Completed` `ArchivedAt` 非空
- [ ] 日志 `logs/pmm-yyyymmdd.log` 不含 `Bearer ey` / `ApiKey=` 等密钥泄漏

**通过条件**：以上 7 项全 ✅。

---

### F.1.2 真 Ollama 兜底

**用例标识**：F1.2  
**目标**：规则引擎与 TMDB 搜索都识别失败的混乱命名，由 AI 兜底解析出 `title + year`，再走第二轮 TMDB → 命中归档。

**前置准备**：

1. 本机部署 Ollama 并启动 `qwen2.5:7b` 模型：
   ```bash
   ollama pull qwen2.5:7b
   ollama serve   # 默认 11434
   ```
2. WebUI 设置 → 解析 → AI 提供商：
   - 类型：`Ollama`
   - 端点：`http://localhost:11434`
   - 模型：`qwen2.5:7b`
   - Enabled：✅
   - 优先级：`10`（最低，仅兜底）
3. 复用 F.1.1 的电影分类与监控目录。

**执行步骤**：

```powershell
New-Item -Path "F:\TestSource\[ABC-RAW]xxx_yyy_zzz_The.Matrix.1999.mkv" -ItemType File -Value $null
```

**验收 checklist**：

- [ ] WebUI「待处理」页能看到该文件先转 `AiParsing` 状态
- [ ] 60s 内 AI 返回 `title=The Matrix, year=1999`（看 `Audit_AiCall` 表）
- [ ] 进入第二轮 TMDB 搜索命中 `tmdbId=603`
- [ ] 最终归档到 `…\Movies\The Matrix (1999)\The Matrix (1999).mkv`
- [ ] `Audit_AiCall` 行：`AttemptCount=1`（如果第一次就成功）/ `Provider=Ollama` / `Success=true`
- [ ] 若 AI 也失败，状态转 `AwaitingReview`，「待审核」页可看到

**通过条件**：要么命中归档（前 5 项 ✅），要么明确进 `AwaitingReview`（第 6 项 ✅）。**不可出现死循环或异常退出**。

---

### F.1.3 真 SMB 网络盘断网恢复

**用例标识**：F1.3  
**目标**：D6.4 `NetworkShareMonitorWorker` 在真共享挂掉时上报告警，恢复后续监控不需重启进程。

**前置准备**：

1. 在 NAS（如 `\\<nas-ip>\share`）开启 SMB 服务，建一个测试目录 `\\<nas-ip>\share\PmmTest`。
2. Windows 资源管理器映射为 `Z:`（保持登录态）。
3. PMM 设置 → 监控目录 → 新增 `Z:\PmmTest`，Priority=100。
4. 等待 30s 让 worker 加载该目录。

**执行步骤**：

1. 投递一个文件确认基线工作：
   ```powershell
   New-Item -Path "Z:\PmmTest\Test1.2020.mkv" -ItemType File -Value $null
   ```
   → 应正常归档（最多到 `AwaitingReview` 也算）。
2. 拔 NAS 网线，或断开本机 SMB 映射以模拟共享不可达：
   ```powershell
   net use Z: /delete /yes
   ```
3. 等待 5 分钟（NetworkShareMonitorWorker 默认探测周期）。
4. WebUI 仪表盘观察「监控目录健康」面板。
5. 恢复 SMB：`net use Z: \\<nas-ip>\share /persistent:yes` 重新挂载。
6. 再投递：`New-Item -Path "Z:\PmmTest\Test2.2021.mkv" -ItemType File -Value $null`。

**验收 checklist**：

- [ ] 步骤 1 文件归档成功
- [ ] 步骤 3 后仪表盘出现红色告警「监控目录不可达：Z:\PmmTest」
- [ ] `logs/pmm-yyyymmdd.log` 中包含 `WARN NetworkShareMonitorWorker ... unreachable`
- [ ] 进程**不退出**（任务管理器 / 托盘图标依然在）
- [ ] 步骤 5 恢复后仪表盘红告警 60s 内消失
- [ ] 步骤 6 新文件 ≤2s 入队 + 正常归档（说明 watcher 已自愈）

**通过条件**：6 项全 ✅；若步骤 4 告警未出现 → D6.4 探测周期或心跳判定有缺陷，开 Bug。

---

## 二、F.2 性能基线（依赖真实 IO 的剩余 3 条）

> 4 条性能指标中：
> - 「TMDB 缓存命中重复处理 < 500ms」→ `F2TmdbCacheHitPerfTests`（自动化已合并）
> - 「检测 → 归档端到端 < 3s（不含 AI）」→ `F2EndToEndBaselineTests`（`tests/PersonalMediaManager.Host.Tests/Performance/`，in-memory 基线，CI 跑）
> - 「文件监控发现延迟 < 2s」→ `F2EndToEndBaselineTests`（同上）
> - 「100 个文件批量扫描 < 5min」→ `F2EndToEndBaselineTests`（同上，缩放到 100 文件 < 30s 验证管线吞吐）
>
> 「真实磁盘 + 真 TMDB + 真 AI」的端到端时间在 F.1 真服务跑完后**顺手用秒表手测**，作为最终交付保底。下文给的是手测脚手架。

### F.2.A 真磁盘单文件归档手测

跑完 F.1.1 后立刻看 `Audit_OperationLog` 表里这一行的 `Duration`，应 < 3000ms。或者跑这条 PowerShell 抓日志：

```powershell
# 日志目录按 §0.3 数据根双落点解析：优先 exe 旁 data\logs\，回退 %LocalAppData%\PersonalMediaManager\logs\
Get-Content "<数据目录>\logs\pmm-$(Get-Date -Format yyyyMMdd).log" |
    Select-String -Pattern "ProcessFileService completed in" |
    Select-Object -Last 1
```

**通过线**：日志最后一条 `completed in XXXms` < 3000，且当次未走 AI 分支。

### F.2.B 真磁盘 100 文件批量手测

```powershell
1..100 | ForEach-Object {
    New-Item -Path ("F:\TestSource\BatchMovie.{0}.2020.mkv" -f $_) -ItemType File -Value $null
}
```

启动秒表，等 WebUI「待处理」清零。**通过线**：< 5 分钟。

> 如果远超基线：先确认是不是 TMDB 429（log 里 `429` 关键字）→ 调 Polly Retry-After；其次确认 AI 是否被错误触发。

---

## 三、F.4 Windows 手工冒烟

> 说明：macOS 支持已移除（2026-06-11 决策），原 macOS UX 冒烟清单已取消（见三.2），本节仅余 Windows 冒烟。  
> Windows 绝大多数项也能自动化，但本节保留人工清单，便于换机部署快速过一遍。

### 三.1 Windows 11 冒烟（7 项）

**机器准备**：
- 一台干净 Win 11 Pro（任何版本），未装过 PMM
- 防火墙默认开
- 关闭「核心隔离」可降低单文件发布解压被拦截概率（可选）

**步骤与 checklist**：

| # | 步骤 | 期望 | ☐ |
|---|---|---|---|
| 1 | 双击 `PersonalMediaManager.exe` | 任务栏托盘出现 PMM 图标；首次启动 ≤3s | ☐ |
| 2 | 等待自动 / 手动开浏览器 | 浏览器跳 `http://localhost:7288/setup` 初始化向导 | ☐ |
| 3 | 设管理员账号 → 完成向导 | 跳转「仪表盘」页，左侧菜单可见，无 401 | ☐ |
| 4 | 添加监控目录 + 投测试 mkv | 状态机走通；最终归档到分类目标根；nfo/海报齐全 | ☐ |
| 5 | 不关进程，再双击 `.exe` | **不报端口冲突**；仅弹出浏览器；任务管理器只 1 个 PMM 进程 | ☐ |
| 6 | 托盘图标右键 → 勾选「开机自动启动」（WebUI 无此开关，自启入口仅托盘菜单） | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 出现 `PersonalMediaManager` 值 = `"C:\...\PersonalMediaManager.exe" --autostart` | ☐ |
| 7 | 局域网另一台设备访问 `http://<本机IP>:7288/` | 能打开 WebUI；首次访问时本机弹防火墙允许 → 同意后即通 | ☐ |

**验证命令片段**：

```powershell
# 验自启注册表（步骤 6）
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PersonalMediaManager

# 验进程单实例（步骤 5）
Get-Process | Where-Object { $_.ProcessName -like 'PersonalMediaManager*' } | Format-Table Id, ProcessName, Path

# 验端口监听（任意步骤）
Get-NetTCPConnection -LocalPort 7288 -State Listen

# 验防火墙规则（步骤 7）
Get-NetFirewallRule -DisplayName 'PersonalMediaManager*' | Format-Table DisplayName, Enabled, Direction, Action
```

**通过条件**：7 项全 ✅；任一失败按四章故障排查。

---

### 三.2 macOS 冒烟（已取消）

macOS 支持已移除（2026-06-11 决策，产品仅支持 Windows），原「macOS Sequoia 15+ 冒烟（5 项 UX）」清单与配套验证命令作废。

---

## 四、故障排查速查

### 4.1 双击 .exe 没反应 / 闪退

| 症状 | 可能原因 | 处理 |
|---|---|---|
| 双击无任何反应 | 单文件发布解压被 Defender / 第三方杀软拦截 | 加白；或临时关闭实时扫描重试 |
| 闪退，事件查看器有 .NET 异常 | 用户机器缺 VC Runtime（SingleFile + SelfContained 已含但偶有边缘） | 装最新 [VC++ Redistributable x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) |
| 进程起来但托盘没出 | `WindowsTray` 初始化异常 | 看 `logs\pmm-yyyymmdd.log` 顶部 `WindowsTray` 异常栈 |
| 端口 7288 被占 | 端口被其它进程占用，Kestrel 启动失败、托盘显示「已停止」（无自动换端口机制；`appsettings.json` 已嵌入 exe，终端用户不可改） | 托盘菜单「配置端口…」改端口（写 local.json）；或 `Get-NetTCPConnection -LocalPort 7288` 找出占用者 |

### 4.2 投递文件后状态卡 `Pending`

1. 看 `logs/pmm-*.log` 是否有 `FileWatcherWorker` 启动行（关键字：`已为监控目录注册 watcher`）。没有 → 检查监控目录 Enabled=true 且路径存在。
2. 看是否走了「写入完成检测」失败 — 关键字 `WaitForCompletionAsync timeout`。原因：文件复制到一半被中断，或来源是大文件未完成。
3. SMB 网络盘：检查 SMB 协议版本（旧 NAS 只开 SMB 1.0，新 Win 默认禁），优先 SMB 2/3。

### 4.3 TMDB 调用失败 / 429

| log 关键字 | 含义 | 处理 |
|---|---|---|
| `401 Unauthorized` | API Key 失效 / 未配 | 设置 → TMDB 重新粘贴 Key |
| `429 Too Many Requests` | 触发节流（应自动 Polly 重试） | 若高频出现且不自愈 → 看 `Audit_OperationLog` 是否有大量 burst；可能是 100 文件批量场景，已经在自愈中，等几分钟 |
| `SocketException` | 出网受限（公司网 / VPN） | 排查网络；TMDB 域名 `api.themoviedb.org` 是否解析 |

### 4.4 AI 兜底全失败

1. 看 `Audit_AiCall` 表：`Success=false` 行的 `FailureSummary` 含错误。
2. 常见：`HttpRequestException` → Ollama 进程没起 / 端口错；`TimeoutException` → 模型太大 / 机器算力不够，换 `qwen2.5:3b`。
3. **硬上限 2 次**：单文件总尝试 2 个 provider 仍失败就转 `AwaitingReview`，**不会无限重试**（CLAUDE.md §一 协作偏好 + dev plan §D.3）。

### 4.5 归档目标目录无法写

| 症状 | 处理 |
|---|---|
| Windows：`UnauthorizedAccessException` | 目标根需当前用户读写权限；UNC 路径要在文件资源管理器手动登一次保留凭据 |
| 跨盘移动慢 | 同盘符 / 同卷下 PMM 走 `MoveFile`（rename，秒级）；跨盘走 `CopyFile + Delete`（按文件大小走 IO） |

### 4.6 自启没生效

- Win：检查注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PersonalMediaManager` 值是否存在且路径正确（带引号）。

### 4.7 日志里出现疑似密钥

立即开 Bug：违反 `dev plan §F.3` + `CLAUDE.md` 安全约束。源码守卫测试 `tests/PersonalMediaManager.Application.Tests/Security/F3SecuritySelfCheckTests.cs`（F.3.4 密钥不入日志）已禁止 `Logxxx` 行带 `ApiKey/Bearer/Password/SecretKey/SigningKey`，仍出现说明绕过路径，必须查清。

---

## 五、阶段 F 完工口径

- F.1 三条用例全 ✅（或明确进 `AwaitingReview` 的兜底分支）。
- F.2 自动化 4 条全绿（§二：TMDB 缓存命中 1 条 + in-memory 基线 3 条）+ 真磁盘手测 2 条达标。
- F.3 自动化 6 条全绿（密钥源码守卫 + ReDoS + 路径穿越 + Zip Slip）。
- F.4 Windows 冒烟清单全 ✅。

满足以上 4 项即可打 release tag，触发 GitHub Actions `.github/workflows/release.yml` 打包发版（CLAUDE §9.3.2）。验收截图 / 日志摘要由手工执行人留档在**仓库外目录**（如桌面或共享盘）——不要放进仓库目录内，防 `git add -A` 误提交。
