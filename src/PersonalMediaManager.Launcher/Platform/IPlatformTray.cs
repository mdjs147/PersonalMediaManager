namespace PersonalMediaManager.Launcher.Platform;

/// <summary>系统托盘抽象（隔离 OS 专属 API，便于单测）</summary>
/// <remarks>
/// Windows 实现：System.Windows.Forms.NotifyIcon（net10.0-windows + UseWindowsForms=true）
/// Launcher Program.cs 启动时创建实现并 Show() / SetMenu() 一次，然后调用 RunMessageLoop() 阻塞主线程
/// 图标：按 Tray Icon D 设计稿程序化绘制（无外部 .ico / .icns 资源），多 DPI 始终清晰
/// </remarks>
public interface IPlatformTray : IDisposable
{
    /// <summary>显示托盘图标（首次显示前会按当前 State 渲染图标）</summary>
    void Show();

    /// <summary>隐藏托盘图标（一般在 Dispose 前清理）</summary>
    void Hide();

    /// <summary>设置或刷新上下文菜单项；items 顺序即菜单顺序，Separator = null Label</summary>
    void SetMenu(IReadOnlyList<TrayMenuItem> items);

    /// <summary>切换托盘图标的状态（重绘并刷新到系统托盘）</summary>
    /// <param name="state">期望状态；默认 Running</param>
    void SetState(TrayIconState state);

    /// <summary>设置鼠标悬停 tooltip 文本</summary>
    /// <remarks>
    /// Windows NotifyIcon.Text 上限 127 字符。
    /// 调用方应自行截短到 ≤ 60 字符（规范 §4.1）以保证视觉一致性。
    /// </remarks>
    void SetTooltip(string text);

    /// <summary>显示气泡通知（Windows: BalloonTip）</summary>
    /// <param name="title">标题</param>
    /// <param name="message">正文</param>
    void ShowBalloon(string title, string message);

    /// <summary>阻塞当前线程跑平台消息循环（Windows: Application.Run）</summary>
    void RunMessageLoop();

    /// <summary>托盘图标被左键双击时触发（用于快捷打开 WebUI）</summary>
    /// <remarks>Windows：订阅 NotifyIcon.MouseDoubleClick（仅左键）触发。</remarks>
    event Action DoubleClicked;
}

/// <summary>托盘图标状态（与设计稿 Tray Icon D 的 5 个 state 一一对应）</summary>
public enum TrayIconState
{
    /// <summary>运行中：琥珀填充 + 绿点</summary>
    Running,
    /// <summary>已停止：灰色描边 + 红点</summary>
    Stopped,
    /// <summary>已暂停：琥珀填充 + 双竖条 + 黄点</summary>
    Paused,
    /// <summary>同步中：琥珀填充 + 旋转 chevron + 蓝点（托盘下不画动画，仅切色）</summary>
    Syncing,
    /// <summary>异常：红色描边 + 红点</summary>
    Error,
}

/// <summary>托盘菜单项（Label = null 表示分隔线；OnClick = null 表示禁用项）</summary>
public sealed record TrayMenuItem(string? Label, Action? OnClick = null, bool Checked = false)
{
    /// <summary>分隔线快捷构造</summary>
    public static TrayMenuItem Separator { get; } = new(Label: null);
}
