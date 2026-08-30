#if PMM_WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PersonalMediaManager.Launcher.Platform.Windows;

/// <summary>Windows 系统托盘实现（基于内置 System.Windows.Forms.NotifyIcon）</summary>
/// <remarks>
/// 设计要点：
/// - 零外部 NuGet 依赖：UseWindowsForms=true 通过 SDK 引入 WinForms
/// - 图标按 Tray Icon D 设计稿用 GDI+ 程序化绘制（琥珀方块 + 双 chevron + 状态点），多 DPI 自然清晰
/// - 5 个状态 Icon 缓存（规范 §4.2）：构造时一次性 BuildIcon × 5，SetState 只切 _notifyIcon.Icon 指针不重建 HICON
///   消除 NotifyIcon._window 在 Dispose 与 UpdateIcon 之间的窄竞争窗口（NRE 风险），且性能更佳
/// - 主题响应 hook：订阅 Microsoft.Win32.SystemEvents.UserPreferenceChanged（Category=General），Dispose 反订阅
///   静态全局事件必须反订阅，否则泄漏整个 WindowsTray 实例（含 NotifyIcon 与 _icons 缓存）
///   当前 handler 仅 ApplyCurrentIcon 不切 light/dark 双版本 —— TrayIconRenderer 现有调色对两种任务栏都已兼容
///   未来美术介入引入双版本时：_icons 改为 Dictionary<(TrayIconState, bool isLight), ...>，handler 查
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme 选缓存
/// - 菜单点击在 UI 线程派发；Action.Invoke 抛异常 → 写托盘气泡兜底
/// - Dispose 时 NotifyIcon.Visible=false + 释放 5 个 HICON + 反订阅静态事件
/// </remarks>
public sealed class WindowsTray : IPlatformTray
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<TrayIconState, (Icon Icon, IntPtr Hicon)> _icons;
    private readonly SynchronizationContext? _uiContext;
    private TrayIconState _state = TrayIconState.Running;
    private bool _disposed;

    public event Action? DoubleClicked;

    public WindowsTray()
    {
        // EnableVisualStyles + SetCompatibleTextRenderingDefault 已由 Program.Main 的
        // ApplicationConfiguration.Initialize() 统一接管（SetCompatibleTextRenderingDefault 整个 app 仅可调一次，重复调抛 InvalidOperationException）
        _menu = new ContextMenuStrip();

        // 预生成 5 个状态的 Icon 缓存：性能 + 消除 SetState 时的 HICON 重建窄竞争
        _icons = new Dictionary<TrayIconState, (Icon, IntPtr)>
        {
            [TrayIconState.Running] = BuildIcon(TrayIconState.Running),
            [TrayIconState.Stopped] = BuildIcon(TrayIconState.Stopped),
            [TrayIconState.Paused] = BuildIcon(TrayIconState.Paused),
            [TrayIconState.Syncing] = BuildIcon(TrayIconState.Syncing),
            [TrayIconState.Error] = BuildIcon(TrayIconState.Error),
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons[_state].Icon,
            Text = "PersonalMediaManager",
            ContextMenuStrip = _menu,
            Visible = false,
        };

        // 左键双击托盘图标 → 触发 DoubleClicked（PmmTrayContext 据此打开 WebUI）；右键双击不触发（右键已是上下文菜单）
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) DoubleClicked?.Invoke();
        };

        // 抓 UI 线程 sync context（NotifyIcon 构造已 InstallIfNeeded WindowsFormsSynchronizationContext）
        _uiContext = SynchronizationContext.Current;

        // 订阅主题切换事件（静态全局事件 —— Dispose 必须反订阅，否则泄漏 WindowsTray 实例）
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Show()
    {
        ThrowIfDisposed();
        _notifyIcon.Visible = true;
    }

    public void Hide()
    {
        if (_disposed) return;
        _notifyIcon.Visible = false;
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        ThrowIfDisposed();
        _menu.Items.Clear();
        foreach (TrayMenuItem item in items)
        {
            if (item.Label is null)
            {
                _menu.Items.Add(new ToolStripSeparator());
                continue;
            }

            ToolStripMenuItem mi = new(item.Label) { Checked = item.Checked, Enabled = item.OnClick is not null };
            if (item.OnClick is { } onClick)
            {
                mi.Click += (_, _) =>
                {
                    try { onClick(); }
                    catch (Exception ex) { ShowBalloon("操作失败", ex.Message); }
                };
            }
            _menu.Items.Add(mi);
        }
    }

    public void SetState(TrayIconState state)
    {
        ThrowIfDisposed();
        if (_state == state) return;
        _state = state;
        ApplyCurrentIcon();
    }

    /// <summary>按 _state 切 _notifyIcon.Icon 指针；try/catch 守 NotifyIcon._window 在 Dispose 与 UpdateIcon 之间的窄竞争</summary>
    private void ApplyCurrentIcon()
    {
        if (_disposed) return;
        if (!_icons.TryGetValue(_state, out (Icon Icon, IntPtr Hicon) pair)) return;
        try
        {
            _notifyIcon.Icon = pair.Icon;
        }
        catch (NullReferenceException)
        {
            // NotifyIcon._window 已 null（Dispose 进行中）—— UpdateIcon 内部对 null window 访问会抛 NRE
        }
        catch (ObjectDisposedException)
        {
            // NotifyIcon 已 Dispose
        }
    }

    /// <summary>SystemEvents.UserPreferenceChanged 回调：主题切换时 marshal 回 UI 线程刷新图标</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Category != UserPreferenceCategory.General) return;

        // 当前 _icons 缓存只有 5 个状态各一份（不分 light/dark），主题切换的视觉刷新由 ApplyCurrentIcon
        // 重新 set 同一 Icon 触发系统重绘完成（足以兼容 TrayIconRenderer 现有调色）；
        // 未来引入 (state, isLight) 双版本时只需在这里查注册表 SystemUsesLightTheme 选择对应缓存
        if (_uiContext is not null)
        {
            _uiContext.Post(_ => ApplyCurrentIcon(), null);
        }
        else
        {
            ApplyCurrentIcon();
        }
    }

    public void SetTooltip(string text)
    {
        ThrowIfDisposed();
        _notifyIcon.Text = text;
    }

    public void ShowBalloon(string title, string message)
    {
        ThrowIfDisposed();
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void RunMessageLoop()
    {
        // Windows 消息循环由 Program.Main 的 Application.Run(PmmTrayContext) 接管，让 ApplicationContext.ExitThread()
        // 控制退出时机；本方法是 no-op，仅为满足 IPlatformTray 接口保留。直接调本方法不会进入消息循环。
        ThrowIfDisposed();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 必须反订阅静态全局事件，否则 SystemEvents 会通过强引用持有 WindowsTray + NotifyIcon + _icons 缓存，整体泄漏
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _notifyIcon.Visible = false;
        // _notifyIcon.Icon 来自 _icons 缓存，不在这里 Dispose（统一在下方循环释放）
        _notifyIcon.Dispose();
        _menu.Dispose();

        // 释放 5 个缓存的 Icon + HICON（GetHicon 创建的 HICON .NET 不负责销毁，显式 DestroyIcon 避免句柄泄漏）
        foreach ((Icon icon, IntPtr hicon) in _icons.Values)
        {
            icon.Dispose();
            if (hicon != IntPtr.Zero) DestroyIcon(hicon);
        }
        _icons.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>按指定状态绘制图标，返回 Icon 与底层 HICON（HICON 所有权交由调用方跟踪并显式 DestroyIcon）</summary>
    private static (Icon Icon, IntPtr Hicon) BuildIcon(TrayIconState state)
    {
        // 托盘标准尺寸 32×32（系统会自动缩到 16/24/32 取需要的；32 master 缩 16 比纯 16 重绘更稳一点）
        using Bitmap bmp = TrayIconRenderer.Render(state, size: 32);
        IntPtr hIcon = bmp.GetHicon();
        Icon icon = Icon.FromHandle(hIcon);
        return (icon, hIcon);
    }
}

/// <summary>按 Tray Icon D 设计稿绘制 chevron mark 到 Bitmap（GDI+）</summary>
/// <remarks>
/// 几何参数与 handoff 包 tray-icon-d.jsx 的 ChevronMark 严格 1:1：
/// 32 master → ≤22 切低密度（单 chevron + 加大状态点 + crispEdges）
/// 颜色：AMBER=#e5a00d / AMBER_DEEP=#15110a / RUN=#4ade80 / STOP/ERR=#ef6b6b / PAUSE=#fbbf24 / SYNC=#60a5fa
/// </remarks>
internal static class TrayIconRenderer
{
    private static readonly Color Amber = Color.FromArgb(0xE5, 0xA0, 0x0D);
    private static readonly Color AmberDeep = Color.FromArgb(0x15, 0x11, 0x0A);
    private static readonly Color Outline = Color.FromArgb(0x6B, 0x73, 0x83);
    private static readonly Color RunDot = Color.FromArgb(0x4A, 0xDE, 0x80);
    private static readonly Color StopDot = Color.FromArgb(0xEF, 0x6B, 0x6B);
    private static readonly Color PauseDot = Color.FromArgb(0xFB, 0xBF, 0x24);
    private static readonly Color SyncDot = Color.FromArgb(0x60, 0xA5, 0xFA);
    private static readonly Color ErrDot = Color.FromArgb(0xEF, 0x6B, 0x6B);

    public static Bitmap Render(TrayIconState state, int size)
    {
        Bitmap bmp = new(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        float k = size / 32f;        // 设计稿坐标 → 实际像素的缩放
        bool tiny = size <= 22;

        bool isOutline = state is TrayIconState.Stopped or TrayIconState.Error;
        Color strokeC = state == TrayIconState.Error ? ErrDot : Outline;
        Color chevronC = isOutline ? strokeC : AmberDeep;
        Color dotC = state switch
        {
            TrayIconState.Running => RunDot,
            TrayIconState.Stopped => StopDot,
            TrayIconState.Paused => PauseDot,
            TrayIconState.Syncing => SyncDot,
            TrayIconState.Error => ErrDot,
            _ => RunDot,
        };

        // ===== 主体方块 =====
        if (isOutline)
        {
            float strokeW = (tiny ? 2.2f : 1.8f) * k;
            float r = (tiny ? 5f : 6.2f) * k;
            using GraphicsPath p = RoundedRectPath(2.6f * k, 2.6f * k, 26.8f * k, 26.8f * k, r);
            using Pen pen = new(strokeC, strokeW);
            g.DrawPath(pen, p);
        }
        else
        {
            float r = (tiny ? 5f : 6.5f) * k;
            using GraphicsPath p = RoundedRectPath(2f * k, 2f * k, 28f * k, 28f * k, r);
            using SolidBrush b = new(Amber);
            g.FillPath(b, p);
        }

        // ===== Glyph：pause 双竖条 / 其他 chevron =====
        if (state == TrayIconState.Paused)
        {
            using SolidBrush b = new(chevronC);
            using GraphicsPath p1 = RoundedRectPath(10f * k, 9f * k, 4f * k, 14f * k, 1.2f * k);
            using GraphicsPath p2 = RoundedRectPath(18f * k, 9f * k, 4f * k, 14f * k, 1.2f * k);
            g.FillPath(b, p1);
            g.FillPath(b, p2);
        }
        else
        {
            float sw = (tiny ? 3f : 2.8f) * k;
            using Pen mainPen = new(chevronC, sw)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            PointF[] chev1 = tiny
                ? new[] { new PointF(11 * k, 9 * k), new PointF(19 * k, 16 * k), new PointF(11 * k, 23 * k) }
                : new[] { new PointF(9.5f * k, 10 * k), new PointF(16 * k, 16 * k), new PointF(9.5f * k, 22 * k) };
            g.DrawLines(mainPen, chev1);

            if (!tiny)
            {
                Color ghost = Color.FromArgb((int)(0.45 * 255), chevronC.R, chevronC.G, chevronC.B);
                using Pen ghostPen = new(ghost, sw)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                PointF[] chev2 = { new(16.5f * k, 10 * k), new(23 * k, 16 * k), new(16.5f * k, 22 * k) };
                g.DrawLines(ghostPen, chev2);
            }
        }

        // ===== 状态点（右下角）=====
        float dotR = (tiny ? 5.5f : 4.2f) * k;
        float dotCx = (tiny ? 26f : 27f) * k;
        float dotCy = (tiny ? 26f : 27f) * k;
        float dotStrokeW = (tiny ? 2f : 1.6f) * k;
        RectangleF dotRect = new(dotCx - dotR, dotCy - dotR, dotR * 2, dotR * 2);
        using (SolidBrush db = new(dotC))
        {
            g.FillEllipse(db, dotRect);
        }
        using (Pen dp = new(Color.FromArgb(0x0A, 0x0E, 0x14), dotStrokeW))
        {
            g.DrawEllipse(dp, dotRect);
        }

        return bmp;
    }

    /// <summary>构造圆角矩形 GraphicsPath（GDI+ 没有内置 API，需要手动拼 4 个 Arc）</summary>
    private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float r)
    {
        GraphicsPath path = new();
        if (r <= 0)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return path;
        }
        float d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
#endif
