using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OmniPen.Interop;
using WinForms = System.Windows.Forms;

namespace OmniPen.Views;

/// <summary>
/// Nút nổi tròn ở góc dưới màn hình khi thanh công cụ thu nhỏ xuống khay hệ thống.
/// Bấm = mở lại thanh công cụ; kéo = di chuyển (phân biệt bằng ngưỡng 4 px).
/// WS_EX_NOACTIVATE để bấm không cướp focus của ứng dụng đang trình chiếu.
/// </summary>
public partial class FloatingButtonWindow : Window
{
    public event EventHandler? OpenRequested;

    private IntPtr _hwnd;
    private Point _down;
    private bool _pressed, _dragged;
    private (int X, int Y)? _initial;

    public FloatingButtonWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.AddExStyle(_hwnd, Win32Helper.WS_EX_NOACTIVATE | Win32Helper.WS_EX_TOOLWINDOW);
        };
        Loaded += (_, _) => Place();
        MouseEnter += (_, _) => HoverRing.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => HoverRing.Visibility = Visibility.Collapsed;
    }

    public void SetInitialPosition(int? x, int? y)
    {
        if (x is int px && y is int py) _initial = (px, py);
    }

    /// <summary>Mặc định: góc dưới-phải vùng làm việc của màn hình chính (ngay trên khay hệ thống).</summary>
    private void Place()
    {
        if (_initial is { } p && WinForms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(p.X + 20, p.Y + 20)))
        {
            Move(p.X, p.Y);
            return;
        }
        var wa = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).WorkingArea;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int size = (int)Math.Round(Width * dpi.DpiScaleX);
        Move(wa.Right - size - 16, wa.Bottom - size - 16);
    }

    private void Move(int x, int y) =>
        Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);

    public (int X, int Y)? GetPhysicalPosition() =>
        _hwnd != IntPtr.Zero && Win32Helper.GetWindowRect(_hwnd, out var r) ? (r.Left, r.Top) : null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _down = e.GetPosition(this);
        _pressed = true;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
        Vector d = e.GetPosition(this) - _down;
        if (!_dragged && d.Length > 4)
        {
            _dragged = true;
            _pressed = false;
            ReleaseMouseCapture();
            DragMove(); // vòng lặp kéo của Windows – trả về khi nhả chuột
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool click = _pressed && !_dragged;
        _pressed = false;
        ReleaseMouseCapture();
        if (click) OpenRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
