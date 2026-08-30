// 不带 #if PMM_WINDOWS 守卫：本文件配套 PortConfigDialog.resx，条件编译指令包裹类定义会触发
// MSB3042（资源清单名推断不可靠）；Launcher 单 TFM net10.0-windows 下守卫本就恒真，直接摘除。
using System.Drawing;
using System.Windows.Forms;

namespace PersonalMediaManager.Launcher;

/// <summary>端口配置 Modal Dialog</summary>
/// <remarks>
/// 详见 docs/需求规范-启动方式与托盘常驻.md §6.3：
/// - 高 DPI 友好：显式字体 Microsoft YaHei UI 9F + AutoScaleMode=Font + TableLayoutPanel + AutoSize
/// - 屏蔽最大/最小化，居中显示，不显示在任务栏
/// - OK 时走 LocalConfigStore.TryValidatePort 校验（含端口已被监听检测），失败显示内联错误不关闭 Dialog
/// </remarks>
internal sealed class PortConfigDialog : Form
{
    private readonly NumericUpDown _portInput;
    private readonly Label _errorLabel;

    public int SelectedPort { get; private set; }

    public PortConfigDialog(int currentPort)
    {
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "配置端口";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        MinimumSize = new Size(320, 0);

        TableLayoutPanel layout = new()
        {
            ColumnCount = 2,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(
            new Label
            {
                Text = "监听端口：",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 6, 8, 6),
            },
            0, 0);

        _portInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(currentPort, 1, 65535),
            AutoSize = true,
            Width = 120,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 0, 4),
        };
        layout.Controls.Add(_portInput, 1, 0);

        _errorLabel = new Label
        {
            ForeColor = Color.FromArgb(0xC0, 0x39, 0x2B),
            AutoSize = true,
            Text = string.Empty,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(0, 4, 0, 8),
            Anchor = AnchorStyles.Left,
        };
        layout.Controls.Add(_errorLabel, 0, 1);
        layout.SetColumnSpan(_errorLabel, 2);

        FlowLayoutPanel buttonRow = new()
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 0),
        };

        Button cancelBtn = new()
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(8, 0, 0, 0),
        };
        Button okBtn = new()
        {
            Text = "确定",
            AutoSize = true,
        };
        okBtn.Click += OnOkClick;

        // RightToLeft 容器内先 Add 的会被推到右边 —— 想让"确定"在左、"取消"在右，先 Add 取消再 Add 确定
        buttonRow.Controls.Add(cancelBtn);
        buttonRow.Controls.Add(okBtn);

        layout.Controls.Add(buttonRow, 0, 2);
        layout.SetColumnSpan(buttonRow, 2);

        Controls.Add(layout);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        int candidate = (int)_portInput.Value;

        if (!LocalConfigStore.TryValidatePort(candidate, out string? error))
        {
            _errorLabel.Text = error ?? "端口校验失败";
            return;  // 不关闭 Dialog，用户可以改端口重试
        }

        SelectedPort = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }
}
