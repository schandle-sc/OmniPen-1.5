using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace OmniPen.Services;

/// <summary>
/// Biểu tượng khay hệ thống (WinForms NotifyIcon – WPF không có sẵn).
/// NotifyIcon chạy được trong app WPF vì cả hai dùng chung vòng lặp message Win32 của UI thread.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _drawItem;
    private readonly WinForms.ToolStripMenuItem _boardItem;

    public event EventHandler? ShowToolbarRequested;
    public event EventHandler? ToggleDrawRequested;
    public event EventHandler? ToggleBoardRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? AboutRequested;
    public event EventHandler? DonateRequested;
    public event EventHandler? ExitRequested;

    public TrayService()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Hiện thanh công cụ", null, (_, _) => ShowToolbarRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _drawItem = new WinForms.ToolStripMenuItem("Chế độ vẽ", null, (_, _) => ToggleDrawRequested?.Invoke(this, EventArgs.Empty));
        _boardItem = new WinForms.ToolStripMenuItem("Chế độ bảng", null, (_, _) => ToggleBoardRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_drawItem);
        menu.Items.Add(_boardItem);
        menu.Items.Add("Xóa tất cả nét vẽ", null, (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Chụp màn hình", null, (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Cài đặt…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Giới thiệu", null, (_, _) => AboutRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("♥ Ủng hộ tác giả", null, (_, _) => DonateRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Thoát OmniPen", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = $"OmniPen {AppInfo.Version}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ShowToolbarRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void UpdateState(bool drawing, bool board)
    {
        _drawItem.Checked = drawing;
        _boardItem.Checked = board;
    }

    public void ShowTip(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);

    /// <summary>Lấy icon đã nhúng vào chính file exe (ApplicationIcon trong .csproj).</summary>
    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch { /* bỏ qua, dùng icon hệ thống */ }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        // Phải ẩn trước khi Dispose, nếu không icon "ma" còn sót trên khay đến khi rê chuột qua.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
