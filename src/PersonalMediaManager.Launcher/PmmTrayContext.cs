#if PMM_WINDOWS
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonalMediaManager.Application.Common;
using PersonalMediaManager.Application.Services.Scan;
using PersonalMediaManager.Application.Services.Setup;
using PersonalMediaManager.Host.Composition;
using PersonalMediaManager.Infrastructure.Persistence;
using PersonalMediaManager.Launcher.Platform;
using PersonalMediaManager.Launcher.Platform.Windows;

namespace PersonalMediaManager.Launcher;

/// <summary>Launcher 应用上下文：托盘 + Kestrel 异步生命周期管家</summary>
/// <remarks>
/// 详见 docs/需求规范-启动方式与托盘常驻.md §1 §5：
/// 1. 继承 ApplicationContext —— 无主窗口，退出由 ExitThread() 控制
/// 2. 构造函数不阻塞：装托盘 + 弹首版菜单 + fire-and-forget StartWebAppAsync；UI 立即响应
/// 3. Kestrel 启停在线程池跑（Task.Run 隔离），让 WinForms SynchronizationContext 不参与 → 避免 Start/StopAsync 的 continuation 抢占 UI 线程 + 避免 .Wait() 与 UI sync ctx 死锁
/// 4. UI 线程 marshal 用 SynchronizationContext.Post（NotifyIcon 内部已 InstallIfNeeded WindowsFormsSynchronizationContext）
/// 5. Stop 5s 超时：StopAsync(cts) + DisposeAsync；超时 OperationCanceledException 静默吞
/// 6. 幂等：_stopTask 字段 + lock 防重入，重复调 StopWebAppAsync 拿到同一 Task
/// 7. Dispose 守卫：volatile bool _isDisposed，所有 UI 更新入口判断；Dispose 时同步等 _stopTask（消息循环已结束，无死锁）
/// 8. 菜单「启停切换」「配置端口」「配置 db」属 E1.18 / E1.19 / E1.20，本次仅提供生命周期骨架，菜单仍 4 项
/// </remarks>
internal sealed class PmmTrayContext : ApplicationContext
{
    private readonly string[] _initialArgs;
    private readonly AppPaths _paths;
    private readonly IPlatformSingleInstance _singleInstance;
    private readonly IPlatformTray _tray;
    private readonly IPlatformAutoStart _autoStart;
    private readonly bool _isAutostart;
    private readonly SynchronizationContext _uiContext;

    // 生命周期状态：_lifecycleLock 守护 (_app / _hostTask / _stopTask / _port) 多线程读写
    private readonly object _lifecycleLock = new();
    private WebApplication? _app;
    private Task? _stopTask;
    private Task? _trayScanTask;
    private CancellationTokenSource? _trayScanCancellation;
    private int _port;
    private TrayIconState _currentState = TrayIconState.Syncing;  // 构造期默认"启动中…"
    private bool _firewallRuleEnsured;
    private bool _autoStartEnabled;  // 开机自启状态缓存（计划任务查询要 shell-out schtasks，避免每次 RebuildTrayMenu 都调用）；仅 UI 线程读写
    private bool _trayScanInProgress; // 托盘「扫描硬盘」状态；仅 UI 线程读写，用于禁用重复点击
    private volatile bool _isDisposed;

    // 升级检查状态（D9.1）：由 HTTP 调用 /system/update-check 后填充；UI 线程读写
    private UpdateCheckState _updateState = UpdateCheckState.Unknown;
    private System.Threading.Timer? _updatePollTimer;
    private static readonly TimeSpan UpdatePollPeriod = TimeSpan.FromHours(1);
    private static readonly HttpClient UpdateHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions UpdateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PmmTrayContext(string[] args, AppPaths paths, IPlatformSingleInstance singleInstance)
    {
        _initialArgs = args;
        _paths = paths;
        _singleInstance = singleInstance;
        _isAutostart = args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

        // 先创建 Tray —— NotifyIcon 内部会调 WindowsFormsSynchronizationContext.InstallIfNeeded()，
        // 让后续 SynchronizationContext.Current 拿到 UI 线程的 sync context
        _tray = new WindowsTray();
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("WindowsTray 构造未安装 WindowsFormsSynchronizationContext —— 检查 Main 是否在 STAThread 主线程");

        WindowsAutoStart autoStart = new();
        _autoStart = autoStart;
        // 开机自启初始状态读取放线程池（不阻塞构造）：
        // 读真实状态回填 _autoStartEnabled 并刷菜单（菜单首版按默认 false 显示，~200ms 内被异步校正，用户右键前即就位）。
        _ = Task.Run(() =>
        {
            bool enabled;
            try { enabled = autoStart.IsEnabled(); } catch { enabled = false; }
            PostToUi(() =>
            {
                if (_isDisposed || _autoStartEnabled == enabled) return;
                _autoStartEnabled = enabled;
                RebuildTrayMenu();
            });
        });

        // 预解析端口（优先 local.json override，回退 PmmHost.DefaultPort）让首版菜单显示正确端口；
        // 真实端口在 StartWebAppAsync 完成后从 app.Configuration 重读，一般两者相同
        _port = LocalConfigStore.ResolveEffectivePort(paths);

        _singleInstance.OnSecondInstance += OnSecondInstanceTriggered;
        _tray.DoubleClicked += OnTrayDoubleClick;

        // 装首版菜单 + Tooltip + 图标 + 显示托盘（不等 Kestrel 起来，UI 立即响应；首版状态 Syncing"启动中…"）
        RebuildTrayMenu();
        RefreshTooltip();
        _tray.SetState(_currentState);
        _tray.Show();

        // Fire-and-forget 异步启 Kestrel：UI 不阻塞，启动完成后通过 PostToUi 刷菜单 + 弹气泡 + 注入防火墙规则
        _ = StartWebAppAsync();
    }

    // ============ Kestrel 生命周期 ============

    /// <summary>异步启 Kestrel：线程池跑 CreateApp + Migration + StartAsync；UI 不阻塞</summary>
    /// <remarks>
    /// 守卫两个边界：
    /// 1. _app 已存在则直接返回（避免「启动运行」菜单点击后又被构造期自启重复触发，导致 Kestrel 双实例 + 端口冲突）
    /// 2. 复位 _stopTask 让本次 Start 完成后可以再次走 Stop（不复位则 Stop 会立即返回已完成的旧任务，新启动的 Kestrel 永远停不下来）
    /// </remarks>
    private async Task StartWebAppAsync()
    {
        lock (_lifecycleLock)
        {
            if (_app is not null) return;
            _stopTask = null;
        }

        try
        {
            (WebApplication app, int actualPort) = await Task.Run(async () =>
            {
                WebApplication newApp = PmmHost.CreateApp(_initialArgs, _paths);

                // EF Migration + 幂等 Seed
                // Migrate 走 DatabaseStartupHealer：异常退出残留 WAL 引发的 SQLITE_CORRUPT 误报会自动 checkpoint + 重试一次；
                // 真损坏（integrity_check 非 ok）原样上抛到外层 catch → tray-crash.log + MessageBox
                using (IServiceScope scope = newApp.Services.CreateScope())
                {
                    IDbContextFactory<PmmDbContext> factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PmmDbContext>>();
                    using PmmDbContext ctx = factory.CreateDbContext();
                    ILogger migrateLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("PersonalMediaManager.Launcher.DbStartup");
                    DatabaseStartupHealer.MigrateWithSelfHeal(ctx, migrateLogger);
                    scope.ServiceProvider.GetRequiredService<IDataSeeder>().SeedAsync().GetAwaiter().GetResult();
                }

                // 用 StartAsync（而非 fire-and-forget RunAsync）：await 到 Kestrel + 所有 HostedService 启动完成。
                // 启动失败（如端口 bind 失败）在此抛出**首要异常**（SocketException 等），被外层 catch 拿到真实原因；
                // 而 RunAsync 是 fire-and-forget，启动失败经 host dispose 后只会在别处冒出次生「Cannot access a disposed object」掩盖真因。
                await newApp.StartAsync().ConfigureAwait(false);
                int newPort = newApp.Configuration.GetValue("Web:Port", PmmHost.DefaultPort);
                return (newApp, newPort);
            }).ConfigureAwait(false);

            bool acceptInstance;
            lock (_lifecycleLock)
            {
                // 构造期间已被 Dispose（极端竞态）：不挂字段，直接收尾
                acceptInstance = !_isDisposed;
                if (acceptInstance)
                {
                    _app = app;
                    _port = actualPort;
                }
            }

            if (!acceptInstance)
            {
                // 抢救式收尾：Dispose 已经走过了，自己把 app 停掉避免端口泄漏
                _ = Task.Run(async () =>
                {
                    try { await app.StopAsync().ConfigureAwait(false); } catch { }
                    try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
                });
                return;
            }

            // 启动完成 → 回 UI 线程刷新菜单 + Tooltip + 弹气泡 + 注入防火墙规则
            PostToUi(() =>
            {
                if (_isDisposed) return;

                _currentState = TrayIconState.Running;

                // 防火墙规则只首启注入一次（重启 Kestrel 时不重复申请 UAC）；UAC 拒绝降级到托盘气泡不阻断
                if (!_firewallRuleEnsured)
                {
                    new WindowsFirewall().EnsureRule(_port, msg => _tray.ShowBalloon("防火墙", msg));
                    _firewallRuleEnsured = true;
                }

                RebuildTrayMenu();
                RefreshTooltip();
                _tray.SetState(_currentState);

                if (!_isAutostart)
                {
                    _tray.ShowBalloon("PersonalMediaManager 已启动", $"WebUI: {LocalNetwork.BuildShareUrl(_port)}");
                }

                // 启动完成后触发首次升级检查 + 起周期 timer（D9.1）
                StartUpdateCheckOnceAsync();
                EnsureUpdatePollTimer();
            });
        }
        catch (Exception ex)
        {
            // Kestrel 启动失败（自动 fallback 也无效的极端情况，或 DB 损坏等非端口原因）：设 Error + 气泡，
            // 并主动弹窗引导用户更换端口重试（需求：启动失败时可改端口，兜底 fallback 之外的场景）
            string reason = ex.Message;
            PostToUi(() =>
            {
                if (_isDisposed) return;
                _currentState = TrayIconState.Error;
                RefreshTooltip();
                _tray.SetState(_currentState);
                _tray.ShowBalloon("启动失败", reason);
                PromptChangePortOnFailure(reason);
            });
        }
    }

    /// <summary>异步停 Kestrel：幂等，多次调拿同一 Task；5s 超时；不阻塞 UI 线程</summary>
    private Task StopWebAppAsync()
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null) return _stopTask;
            if (_app is null) return Task.CompletedTask;  // 没在跑，立即返回
            _stopTask = Task.Run(StopWebAppCoreAsync);
            return _stopTask;
        }
    }

    private async Task StopWebAppCoreAsync()
    {
        WebApplication? app;
        Task? trayScanTask;
        lock (_lifecycleLock)
        {
            // 先置 null 再异步收尾：重入时拿不到旧实例
            app = _app;
            _app = null;

            // 托盘扫描直接复用当前 Host 的 scoped IScanService。停止 / 重启 Host 前先取消并等它释放 scope，
            // 避免根 ServiceProvider Dispose 与长时间磁盘枚举 / 数据库写入并发。
            trayScanTask = _trayScanTask;
            try { _trayScanCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (app is null) return;

        if (trayScanTask is not null)
        {
            try { await trayScanTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* 扫描错误由 CompleteTrayScanAsync 统一反馈；停止流程继续 */ }
        }

        // StartAsync 模式下由 StopAsync 完成优雅停止（触发各 HostedService StopAsync + Kestrel 停监听）；5s 超时兜底后 Dispose
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 5s 超时静默，继续 Dispose */ }
        catch { /* StopAsync 其它异常也不升级，继续 Dispose */ }

        try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    // ============ 托盘菜单 ============

    private void RebuildTrayMenu()
    {
        // 按 _currentState 决定启停文案 + Open/Copy 可点性（规范 §4.4 §6.1 §6.2）
        bool isRunning = _currentState == TrayIconState.Running;
        bool isBusy = _currentState == TrayIconState.Syncing;

        string startStopLabel = _currentState switch
        {
            TrayIconState.Running => "停止运行",
            TrayIconState.Syncing => "启动中…",
            _ => "启动运行",
        };
        Action? startStopAction = isBusy ? null : OnStartStopClick;  // Syncing 时 OnClick=null 禁用

        List<TrayMenuItem> items = new();

        // 新版本提示（D9.1）：顶部插入 "⬆ 有新版本 vX.Y.Z"（点击打开 GitHub URL）
        if (_updateState.ShouldPromptUser)
        {
            items.Add(new($"⬆ 有新版本 v{_updateState.LatestVersion}", () => OpenUrl(_updateState.LatestUrl)));
            items.Add(TrayMenuItem.Separator);
        }

        items.Add(new($"PersonalMediaManager  ·  端口 {_port}"));
        items.Add(TrayMenuItem.Separator);
        items.Add(new(startStopLabel, startStopAction));
        items.Add(new("打开 WebUI", isRunning ? () => OpenBrowser(_port) : null));
        items.Add(new("复制访问地址", isRunning ? CopyAccessUrl : null));
        items.Add(new("扫描硬盘", isRunning && !_trayScanInProgress ? OnScanDiskClick : null));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("配置端口…", isBusy ? null : OnConfigurePortClick));
        items.Add(new("配置数据库位置…", isBusy ? null : OnConfigureDbClick));
        items.Add(new(_autoStartEnabled ? "✓ 开机自启" : "  开机自启",
            OnToggleAutoStart,
            Checked: _autoStartEnabled));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("立即检查更新", isRunning ? () => StartUpdateCheckOnceAsync() : null));
        items.Add(new("关于…", ShowAboutDialog));
        items.Add(new("退出", HandleExit));

        _tray.SetMenu(items);
    }

    /// <summary>「扫描硬盘」菜单回调：复用 IScanService.TriggerAllAsync 扫描全部已启用监控目录</summary>
    private void OnScanDiskClick()
    {
        if (_currentState != TrayIconState.Running || _trayScanInProgress) return;

        WebApplication? app;
        lock (_lifecycleLock) { app = _app; }
        if (app is null)
        {
            _tray.ShowBalloon("无法扫描硬盘", "服务尚未运行");
            return;
        }

        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
        Task<ScanTriggerResult> scanTask = Task.Run(async () =>
        {
            using IServiceScope scope = app.Services.CreateScope();
            IScanService scanService = scope.ServiceProvider.GetRequiredService<IScanService>();
            return await scanService.TriggerAllAsync(force: false, ct: cancellation.Token).ConfigureAwait(false);
        });

        lock (_lifecycleLock)
        {
            _trayScanCancellation?.Dispose();
            _trayScanCancellation = cancellation;
            _trayScanTask = scanTask;
        }

        _trayScanInProgress = true;
        RebuildTrayMenu();
        _ = CompleteTrayScanAsync(scanTask, cancellation);
    }

    /// <summary>等待托盘扫描结束，回 UI 线程恢复菜单并显示结果；扫描主体始终在线程池执行，避免阻塞托盘消息循环</summary>
    private async Task CompleteTrayScanAsync(Task<ScanTriggerResult> scanTask, CancellationTokenSource cancellation)
    {
        string title;
        string message;
        try
        {
            ScanTriggerResult result = await scanTask.ConfigureAwait(false);
            title = "扫描硬盘完成";
            message = $"已扫描 {result.FolderCount} 个监控目录，入队 {result.FilesEnqueued} 个文件";
        }
        catch (OperationCanceledException)
        {
            title = "扫描硬盘已取消";
            message = "服务已停止，扫描未继续执行";
        }
        catch (BusinessException ex)
        {
            title = "无法扫描硬盘";
            message = ex.Message;
        }
        catch (Exception ex)
        {
            title = "扫描硬盘失败";
            message = ex.Message;
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_trayScanTask, scanTask))
                {
                    _trayScanTask = null;
                    _trayScanCancellation = null;
                }
            }
            cancellation.Dispose();
        }

        PostToUi(() =>
        {
            if (_isDisposed) return;
            _trayScanInProgress = false;
            RebuildTrayMenu();
            _tray.ShowBalloon(title, message);
        });
    }

    /// <summary>「开机自启」菜单回调：切换计划任务（规范 §6.5）—— 成功弹气泡、失败弹 MessageBox 不静默</summary>
    /// <remarks>
    /// Enable/Disable 会 shell-out schtasks（创建/删除「最高权限」计划任务，~100-200ms）；点击即时操作可接受短暂阻塞。
    /// schtasks 失败（典型：非提权进程下无权建最高权限任务）抛异常 → 捕获弹 MessageBox，_autoStartEnabled 不变。
    /// </remarks>
    private void OnToggleAutoStart()
    {
        bool enable = !_autoStartEnabled;
        try
        {
            if (enable) _autoStart.Enable(); else _autoStart.Disable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{(enable ? "设置" : "取消")}开机自启失败：{ex.Message}",
                "开机自启",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        _autoStartEnabled = enable;
        _tray.ShowBalloon("开机自启", enable ? "已设置开机自启（下次登录生效）" : "已取消开机自启");
        RebuildTrayMenu();
    }

    /// <summary>「启停切换」菜单回调：立即设 Syncing 防重入 + fire-and-forget 异步切换</summary>
    private void OnStartStopClick()
    {
        TrayIconState before = _currentState;
        if (before == TrayIconState.Syncing) return;  // 已禁用，理论上点不到

        // 立即同步设 Syncing：菜单本项 + Open/Copy 同时进入禁用态防重入
        _currentState = TrayIconState.Syncing;
        RebuildTrayMenu();
        RefreshTooltip();
        _tray.SetState(_currentState);

        if (before == TrayIconState.Running)
        {
            _ = StopFromMenuAsync();
        }
        else
        {
            // Stopped / Error / Paused → 启动 Kestrel
            _ = StartWebAppAsync();
        }
    }

    /// <summary>菜单触发的停止：调 StopWebAppAsync → 完成回 UI 线程设 Stopped + 刷菜单 + Tooltip + 图标</summary>
    private async Task StopFromMenuAsync()
    {
        await StopWebAppAsync().ConfigureAwait(false);
        PostToUi(() =>
        {
            if (_isDisposed) return;
            _currentState = TrayIconState.Stopped;
            RebuildTrayMenu();
            RefreshTooltip();
            _tray.SetState(_currentState);
        });
    }

    /// <summary>启动失败时主动引导更换端口重试（需求2）：确认框 → 端口配置对话框 → 写 local.json → 重启 Kestrel</summary>
    /// <remarks>
    /// 与自动端口 fallback 互补：fallback 兜底「首选端口被占/被保留」的常见情况（自动换端口无感启动）；
    /// 本弹窗兜底「fallback 也无效」（所有备用端口不可用）或「非端口原因失败」（如 DB 损坏，用户可选否）的极端场景，
    /// 让用户在启动失败当下就能改端口重试，而非只看到一条气泡不知所措。全程在 UI 线程（由 PostToUi 调入）。
    /// </remarks>
    private void PromptChangePortOnFailure(string reason)
    {
        DialogResult choice = MessageBox.Show(
            $"服务启动失败：{reason}{Environment.NewLine}{Environment.NewLine}这通常是端口被占用或被系统保留导致。是否更换端口后重试？",
            "启动失败",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button1);
        if (choice != DialogResult.Yes) return;

        int newPort;
        using (PortConfigDialog dlg = new(_port))
        {
            if (dlg.ShowDialog() != DialogResult.OK) return;
            newPort = dlg.SelectedPort;
        }

        try
        {
            LocalConfigStore.SetOverridePort(_paths, newPort);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"写入 local.json 失败：{ex.Message}", "配置端口", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 进入 Syncing 并重启（复用配置变更重启流程；PmmHost 重读 local.json 新端口，仍会经 fallback 探测）
        _currentState = TrayIconState.Syncing;
        RebuildTrayMenu();
        RefreshTooltip();
        _tray.SetState(_currentState);
        _ = RestartForConfigChangeAsync();
    }

    /// <summary>「配置端口…」菜单回调：弹 Dialog → SetOverridePort → 异步停 + 起（PmmHost 重读 local.json 新端口）</summary>
    private void OnConfigurePortClick()
    {
        int newPort;
        using (PortConfigDialog dlg = new(_port))
        {
            if (dlg.ShowDialog() != DialogResult.OK) return;
            newPort = dlg.SelectedPort;
        }

        try
        {
            LocalConfigStore.SetOverridePort(_paths, newPort);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"写入 local.json 失败：{ex.Message}",
                "配置端口",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // 立即同步设 Syncing 防重入：菜单本项 + Open/Copy + 启停 + 配置端口同步禁用
        _currentState = TrayIconState.Syncing;
        RebuildTrayMenu();
        RefreshTooltip();
        _tray.SetState(_currentState);

        _ = RestartForConfigChangeAsync();
    }

    /// <summary>「配置数据库位置…」菜单回调：SaveFileDialog 选 db 路径 → 不存在路径二次确认 → SetOverrideDbPath → 异步停 + 起</summary>
    /// <remarks>
    /// 切到不存在的路径时 EF Migration 会自动建 schema，但无业务数据 → SetupGuard 会拦回初始化向导（规范 §6.4 边界）
    /// 用 MessageBox 二次确认明文告知用户，避免用户以为"切错了"
    /// </remarks>
    private void OnConfigureDbClick()
    {
        string currentDbPath = LocalConfigStore.ReadOverrideDbPath(_paths) ?? _paths.DbFile;
        string initialDir = Path.GetDirectoryName(currentDbPath) ?? _paths.Root;

        string newDbPath;
        using (SaveFileDialog dlg = new())
        {
            dlg.Title = "选择数据库位置";
            dlg.Filter = "SQLite (*.db)|*.db|所有文件 (*.*)|*.*";
            dlg.OverwritePrompt = false;   // 允许选已存在的 db（典型场景：切到备份恢复点）
            dlg.AddExtension = true;
            dlg.DefaultExt = "db";
            dlg.InitialDirectory = initialDir;
            dlg.FileName = Path.GetFileName(currentDbPath);
            dlg.RestoreDirectory = true;

            if (dlg.ShowDialog() != DialogResult.OK) return;
            newDbPath = dlg.FileName;
        }

        if (!LocalConfigStore.TryValidateDbPath(newDbPath, out string? error))
        {
            MessageBox.Show($"路径不合法：{error}", "配置数据库位置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 不存在路径：明文提示「将创建空数据库并触发初始化向导」让用户知情后再继续
        if (!File.Exists(newDbPath))
        {
            DialogResult confirm = MessageBox.Show(
                $"目标路径不存在：\n{newDbPath}\n\n切换后将创建空数据库并触发初始化向导。是否继续？",
                "配置数据库位置",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.OK) return;
        }

        try
        {
            LocalConfigStore.SetOverrideDbPath(_paths, newDbPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"写入 local.json 失败：{ex.Message}", "配置数据库位置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _currentState = TrayIconState.Syncing;
        RebuildTrayMenu();
        RefreshTooltip();
        _tray.SetState(_currentState);

        _ = RestartForConfigChangeAsync();
    }

    /// <summary>配置变更后重启 Kestrel（端口 / db 路径切换共用）：Stop → Start，保持 Syncing 状态直到 Start 成功设 Running</summary>
    /// <remarks>
    /// 不调 StopFromMenuAsync（后者会中间设 Stopped 再被 Start 设 Running，菜单闪烁）；直接走低层 StopWebAppAsync
    /// </remarks>
    private async Task RestartForConfigChangeAsync()
    {
        try { await StopWebAppAsync().ConfigureAwait(false); }
        catch { /* Stop 失败不阻断 Start，继续 */ }

        await StartWebAppAsync().ConfigureAwait(false);
    }

    private void OnSecondInstanceTriggered()
    {
        // IPC 回调可能在非 UI 线程；OpenBrowser 用 Process.Start 线程安全，无需 marshal
        OpenBrowser(_port);
    }

    /// <summary>托盘图标双击：运行中时用默认浏览器打开 WebUI（与「打开 WebUI」菜单项一致）</summary>
    /// <remarks>NotifyIcon.MouseDoubleClick 在 UI 线程触发，直接读 _currentState / _port 无需 marshal；非运行态不打开，避免浏览器指向已停端口。</remarks>
    private void OnTrayDoubleClick()
    {
        if (_currentState != TrayIconState.Running) return;
        OpenBrowser(_port);
    }

    /// <summary>「退出」菜单：UI 立即隐藏托盘 + fire-and-forget Stop + ExitThread 让 Application.Run 立即返回</summary>
    private void HandleExit()
    {
        _tray.Hide();

        // Fire-and-forget 触发 Stop（Dispose 兜底会再次 await 同一 _stopTask）
        _ = StopWebAppAsync();

        // 消息循环结束 → Application.Run 返回 → Main using 调 Dispose → Dispose 同步等 _stopTask
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            base.Dispose(disposing);
            return;
        }
        _isDisposed = true;

        if (disposing)
        {
            try { _singleInstance.OnSecondInstance -= OnSecondInstanceTriggered; } catch { }

            // 升级检查周期 timer
            try { _updatePollTimer?.Dispose(); } catch { }

            // 同步等 Stop 完成；此时已无消息循环（Application.Run 已返回），无 UI sync ctx 死锁风险
            try { StopWebAppAsync().GetAwaiter().GetResult(); } catch { }

            _tray.Dispose();
        }

        base.Dispose(disposing);
    }

    // ============ 工具方法 ============

    /// <summary>按 _currentState + _port 重写 Tooltip 文本（规范 §4.1：超 60 字符截 + …）</summary>
    private void RefreshTooltip()
    {
        string statusText = _currentState switch
        {
            TrayIconState.Running => $"端口 {_port} · 运行中",
            TrayIconState.Syncing => "启动中…",
            TrayIconState.Stopped => "已停止",
            TrayIconState.Paused => "已暂停",
            TrayIconState.Error => "启动失败",
            _ => "未知状态",
        };
        string text = $"PersonalMediaManager {GetAssemblyInfoVersion()} · {statusText}";
        if (text.Length > 60)
        {
            text = text[..59] + "…";  // 截到 59 字符 + 省略号 = 60 字符（Windows NotifyIcon.Text 安全范围）
        }
        _tray.SetTooltip(text);
    }

    /// <summary>读 PmmProductVersion（AssemblyMetadata）作为 Tooltip 显示用的主版本号</summary>
    /// <remarks>
    /// Directory.Build.targets InjectVersionMetadata 把 ProductVersion 烤进 AssemblyMetadataAttribute；
    /// 找不到时降级到 InformationalVersion 截 + 号前段、AssemblyVersion、"0.0.0"。
    /// </remarks>
    private static string GetAssemblyInfoVersion()
    {
        Assembly asm = typeof(PmmTrayContext).Assembly;

        string? product = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ProductVersion")?.Value;
        if (!string.IsNullOrEmpty(product))
        {
            return "v" + product;
        }

        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            if (plus > 0) info = info[..plus];
            int dash = info.IndexOf('-');
            if (dash > 0) info = info[..dash];
            return "v" + info;
        }
        Version? v = asm.GetName().Version;
        return v is not null ? "v" + v : "v0.0.0";
    }

    /// <summary>「关于…」菜单回调：MessageBox 展示 4 套版本号 + commit + buildTime</summary>
    /// <remarks>
    /// 4 套版本号 + buildTime 全部来自 Directory.Build.targets InjectVersionMetadata 注入的 AssemblyMetadataAttribute；
    /// Backend / commit / dirty 来自 AssemblyInformationalVersion（含 +sha[.dirty] 后缀）。
    /// 仅本地 MessageBox 不调 API：托盘进程可能在 Kestrel 启动失败状态下访问"关于"，必须能离线展示。
    /// </remarks>
    private void ShowAboutDialog()
    {
        Assembly asm = typeof(PmmTrayContext).Assembly;
        Dictionary<string, string> meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => !string.IsNullOrEmpty(a.Key))
            .ToDictionary(a => a.Key!, a => a.Value ?? string.Empty, StringComparer.Ordinal);

        string product = meta.GetValueOrDefault("ProductVersion", "0.0.0");
        string dbTarget = meta.GetValueOrDefault("DbVersion", "0.0.0");
        string frontend = meta.GetValueOrDefault("FrontendVersion", "0.0.0");
        string buildTime = meta.GetValueOrDefault("BuildTimeUtc", "未知");

        string backend = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "0.0.0";

        string body = $"PersonalMediaManager v{product}\r\n\r\n"
            + $"后端：{backend}\r\n"
            + $"前端：{frontend}\r\n"
            + $"数据库目标：{dbTarget}\r\n"
            + $"构建时间：{buildTime}\r\n"
            + $".NET：{Environment.Version}\r\n"
            + $"\r\n（数据库实际版本与 needsMigration 状态请在 WebUI 设置 → 关于查看）";

        MessageBox.Show(body, "关于 PersonalMediaManager", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>切回 UI 线程执行 action；marshal 失败不升级（避免 UI 线程已死时 crash）</summary>
    private void PostToUi(Action action)
    {
        _uiContext.Post(_ => { try { action(); } catch { } }, null);
    }

    private static void OpenBrowser(int port)
    {
        string url = $"http://localhost:{port}";
        OpenUrl(url);
    }

    /// <summary>用系统默认浏览器打开任意 URL（升级检查的 GitHub Release 链接也走这里）</summary>
    /// <remarks>
    /// 提权（requireAdministrator）进程直接 ShellExecute 会让浏览器**继承管理员令牌**——浏览器要么拒绝以管理员启动、
    /// 要么串到错误的用户 profile（cookie/会话不对）。故提权时经 explorer.exe 转交：explorer 已在普通完整性级别运行，
    /// 由它按默认协议处理器打开 URL → 浏览器落在用户正常会话、普通完整性级别。非提权时维持原直连 ShellExecute。
    /// </remarks>
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (IsProcessElevated())
            {
                string explorer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                ProcessStartInfo psi = new(explorer) { UseShellExecute = false };
                psi.ArgumentList.Add(url);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch
        {
            // 浏览器调用失败不抛：用户也可以手动复制地址
        }
    }

    /// <summary>当前进程是否以管理员（提权）运行</summary>
    /// <remarks>进程已统一 asInvoker，通常为 false（普通权限）；仅用户手动「以管理员身份运行」时才 true。</remarks>
    private static bool IsProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>「复制访问地址」菜单回调：复制局域网地址（探测不到回退 localhost）+ 气泡反馈</summary>
    /// <remarks>
    /// 复制局域网 IP 而非 localhost：本机点「打开 WebUI」即可，复制地址的唯一刚需是发给手机 / 平板 / 另一台电脑。
    /// 气泡回显复制的地址，顺带让用户知道局域网用这个地址访问。
    /// </remarks>
    private void CopyAccessUrl()
    {
        string url = LocalNetwork.BuildShareUrl(_port);
        CopyToClipboard(url);
        if (!_isDisposed) _tray.ShowBalloon("已复制访问地址", url);
    }

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* 剪贴板可能被其他进程独占；忽略 */ }
    }

    // ============ 升级检查（D9.1） ============

    /// <summary>触发一次 POST /system/update-check/run（loopback 免鉴权）+ 拉一次 GET 刷新状态</summary>
    /// <remarks>
    /// fire-and-forget：异常静默吞，绝不阻断 UI。结果由后续 GET 拉取统一刷菜单 / 弹气泡。
    /// 调用场景：StartWebAppAsync 完成后的首检 + 托盘菜单「立即检查更新」+ EnsureUpdatePollTimer 周期触发。
    /// </remarks>
    private void StartUpdateCheckOnceAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                int port;
                lock (_lifecycleLock) { port = _port; }
                using HttpRequestMessage req = new(HttpMethod.Post, $"http://localhost:{port}/api/system/update-check/run");
                using HttpResponseMessage _ = await UpdateHttpClient.SendAsync(req).ConfigureAwait(false);
            }
            catch
            {
                // 静默：Kestrel 刚起或 GitHub 不可达都不阻断
            }

            await RefreshUpdateStateAsync().ConfigureAwait(false);
        });
    }

    /// <summary>启动周期升级状态拉取（首次延迟 5min，之后每 1h 一次；Worker 在 Host 内已做 GitHub 真实检查）</summary>
    private void EnsureUpdatePollTimer()
    {
        if (_updatePollTimer is not null) return;
        _updatePollTimer = new System.Threading.Timer(
            _ => _ = RefreshUpdateStateAsync(),
            state: null,
            dueTime: TimeSpan.FromMinutes(5),
            period: UpdatePollPeriod);
    }

    /// <summary>调 GET /system/update-check 拉最新状态 → marshal 回 UI 线程更新菜单 / 弹气泡</summary>
    private async Task RefreshUpdateStateAsync()
    {
        UpdateCheckRemoteDto? remote = null;
        try
        {
            int port;
            lock (_lifecycleLock) { port = _port; }
            using HttpRequestMessage req = new(HttpMethod.Get, $"http://localhost:{port}/api/system/update-check");
            using HttpResponseMessage resp = await UpdateHttpClient.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            UpdateCheckApiEnvelope? envelope = await resp.Content.ReadFromJsonAsync<UpdateCheckApiEnvelope>(UpdateJsonOptions).ConfigureAwait(false);
            if (envelope?.Code != 0 || envelope.Data is null) return;
            remote = envelope.Data;
        }
        catch
        {
            return; // 静默
        }

        if (remote is null) return;

        UpdateCheckState newState = UpdateCheckState.From(remote);
        PostToUi(() =>
        {
            if (_isDisposed) return;
            UpdateCheckState prev = _updateState;
            _updateState = newState;

            // 菜单可能需要插入 "⬆ 有新版本" 项
            if (prev.ShouldPromptUser != newState.ShouldPromptUser
                || prev.LatestVersion != newState.LatestVersion)
            {
                RebuildTrayMenu();
            }

            // 气泡：仅在「真有新版本 + 未跳过 + 未气泡过此版本」时弹一次
            if (newState.ShouldPromptUser && !string.IsNullOrEmpty(newState.LatestVersion))
            {
                string? lastBalloon = LocalConfigStore.ReadLastBalloonedVersion(_paths);
                if (!string.Equals(lastBalloon, newState.LatestVersion, StringComparison.Ordinal))
                {
                    _tray.ShowBalloon(
                        "PersonalMediaManager 有新版本",
                        $"v{newState.LatestVersion} 可用，点击托盘菜单前往下载");
                    LocalConfigStore.SetLastBalloonedVersion(_paths, newState.LatestVersion);
                }
            }
        });
    }

    /// <summary>托盘侧持有的升级状态快照（来自后端 /system/update-check 响应）</summary>
    private readonly record struct UpdateCheckState(
        bool ShouldPromptUser,
        string? LatestVersion,
        string? LatestUrl)
    {
        public static UpdateCheckState Unknown => new(false, null, null);

        public static UpdateCheckState From(UpdateCheckRemoteDto dto)
        {
            string? latestVersion = dto.LastCheck?.LatestVersion;
            string? skipped = dto.SkippedVersion;
            bool hasNew = dto.LastCheck?.Success == true
                && dto.LastCheck.HasNewVersion
                && !string.IsNullOrEmpty(latestVersion)
                && !string.Equals(skipped, latestVersion, StringComparison.Ordinal);
            return new UpdateCheckState(hasNew, latestVersion, dto.LastCheck?.LatestUrl);
        }
    }

    /// <summary>反序列化 /system/update-check 响应外壳（ApiResponse 信封）</summary>
    private sealed class UpdateCheckApiEnvelope
    {
        public int Code { get; set; }
        public UpdateCheckRemoteDto? Data { get; set; }
    }

    /// <summary>/system/update-check 响应 data（UpdateCheckSettingsResponse 的子集）</summary>
    private sealed class UpdateCheckRemoteDto
    {
        public string? SkippedVersion { get; set; }
        public UpdateCheckLastCheckDto? LastCheck { get; set; }
    }

    private sealed class UpdateCheckLastCheckDto
    {
        public bool Success { get; set; }
        public bool HasNewVersion { get; set; }
        public string? LatestVersion { get; set; }
        public string? LatestUrl { get; set; }
    }
}
#endif
