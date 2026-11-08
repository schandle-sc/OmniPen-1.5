using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OmniPen.Ink;
using OmniPen.Interop;
using OmniPen.Services;

namespace OmniPen.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>Lựa chọn "Khởi động cùng Windows" (đọc sau khi ShowDialog trả về true).</summary>
    public bool StartWithWindows => StartupCheck.IsChecked == true;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        PaperCombo.ItemsSource = PaperTypeExtensions.Selectable()
            .Select(t => new { Name = t.DisplayName(), Value = t })
            .ToList();

        LoadFrom(settings);
        StartupCheck.IsChecked = StartupService.IsEnabled;
        LatexStatus.Text = LatexService.FindBinFolder(settings.LatexFolder) is null ? "Chưa tìm thấy LaTeX" : "Đã tìm thấy LaTeX ✓";
    }

    private void LoadFrom(AppSettings s)
    {
        HkDraw.Text = s.HotkeyToggleDraw;
        HkEraser.Text = s.HotkeyEraser;
        HkClear.Text = s.HotkeyClear;
        HkBoard.Text = s.HotkeyBoard;
        HkToolbar.Text = s.HotkeyToolbar;
        HkMagnifier.Text = s.HotkeyMagnifier;
        HkCode.Text = s.HotkeyCode;
        HkSnip.Text = s.HotkeySnip;
        AutoShapeCheck.IsChecked = s.AutoShape;
        AllKeysCheck.IsChecked = s.ShowAllKeys;
        PaperCombo.SelectedValue = InkDocument.Normalize(s.DefaultPaper);
        VerticalCheck.IsChecked = s.ToolbarVertical;
        FloatingCheck.IsChecked = s.ShowFloatingButton;
        AutoScrollCheck.IsChecked = s.AutoScroll;
        LatexFolderBox.Text = s.LatexFolder ?? "";
    }

    /// <summary>Bắt tổ hợp phím người dùng nhấn và hiển thị dạng "Ctrl+Shift+D".</summary>
    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;

        // Khi nhấn Alt, WPF báo Key.System – phím thật nằm ở SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Chỉ nhấn phím bổ trợ (đang giữ Ctrl…) → chưa đủ tổ hợp.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            box.Text = "";
            return;
        }

        ModifierKeys mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        if (mods == ModifierKeys.None) return; // Bắt buộc có phím bổ trợ.

        box.Text = new Hotkey(mods, key).ToString();
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        LoadFrom(new AppSettings());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var boxes = new[] { HkDraw, HkEraser, HkClear, HkBoard, HkToolbar, HkMagnifier, HkCode, HkSnip };

        foreach (TextBox b in boxes)
        {
            if (b.Text.Length > 0 && Hotkey.Parse(b.Text) is null)
            {
                MessageBox.Show(this, $"Phím tắt \"{b.Text}\" không hợp lệ.", "OmniPen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var used = boxes.Where(b => b.Text.Length > 0).Select(b => b.Text).ToList();
        if (used.Count != used.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            MessageBox.Show(this, "Có hai chức năng đang dùng cùng một phím tắt.", "OmniPen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.HotkeyToggleDraw = HkDraw.Text;
        _settings.HotkeyEraser = HkEraser.Text;
        _settings.HotkeyClear = HkClear.Text;
        _settings.HotkeyBoard = HkBoard.Text;
        _settings.HotkeyToolbar = HkToolbar.Text;
        _settings.HotkeyMagnifier = HkMagnifier.Text;
        _settings.HotkeyCode = HkCode.Text;
        _settings.HotkeySnip = HkSnip.Text;
        _settings.AutoShape = AutoShapeCheck.IsChecked == true;
        _settings.ShowAllKeys = AllKeysCheck.IsChecked == true;
        if (PaperCombo.SelectedValue is PaperType p) _settings.DefaultPaper = p;
        _settings.ToolbarVertical = VerticalCheck.IsChecked == true;
        _settings.ShowFloatingButton = FloatingCheck.IsChecked == true;
        _settings.AutoScroll = AutoScrollCheck.IsChecked == true;
        _settings.LatexFolder = string.IsNullOrWhiteSpace(LatexFolderBox.Text) ? null : LatexFolderBox.Text.Trim();

        DialogResult = true;
    }
}
