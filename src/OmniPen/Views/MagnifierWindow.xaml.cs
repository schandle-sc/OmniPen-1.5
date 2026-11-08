using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OmniPen.Interop;

namespace OmniPen.Views;

/// <summary>
/// Kính lúp động: thấu kính tròn bám theo con trỏ, phóng 2× / 3× vùng quanh con trỏ (~30 khung hình/giây).
/// Kỹ thuật: mỗi khung chụp vùng nhỏ quanh con trỏ bằng BitBlt; cửa sổ kính lúp đặt WDA_EXCLUDEFROMCAPTURE
/// nên BitBlt "nhìn xuyên" qua chính nó. Cửa sổ xuyên chuột → vẫn click/gõ bình thường vào ứng dụng bên dưới.
/// </summary>
public partial class MagnifierWindow : Window
{
    private IntPtr _hwnd;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private double _zoom = 2;
    private int _sizePx;
    private bool _excluded;

    public double Zoom => _zoom;

    public MagnifierWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.AddExStyle(_hwnd, Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_NOACTIVATE);
            Win32Helper.SetClickThrough(_hwnd, true);
            _excluded = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
    }

    /// <summary>Bật lần lượt: 2× → 3× → tắt. Trả về true nếu kính lúp đang bật sau khi đổi.</summary>
    public bool Cycle()
    {
        if (!IsVisible) { _zoom = 2; Show(); }
        else if (_zoom < 2.5) _zoom = 3;
        else { Hide(); return false; }
        ZoomText.Text = $"{_zoom:0}×";
        Tick();
        return true;
    }

    private void Tick()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.GetCursorPos(out var cur)) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _sizePx = (int)Math.Round((Width - 20) * dpi.DpiScaleX);
        int outer = (int)Math.Round(Width * dpi.DpiScaleX);

        // Đặt tâm kính lúp ngay tại con trỏ.
        Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST, cur.X - outer / 2, cur.Y - outer / 2, 0, 0,
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);

        if (!_excluded)
        {
            // Windows quá cũ (trước 10 2004) không loại được cửa sổ khỏi ảnh chụp → đặt lệch xuống-phải.
            Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST, cur.X + 24, cur.Y + 24, 0, 0,
                Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
        }

        int src = Math.Max(16, (int)(_sizePx / _zoom));
        try
        {
            LensBrush.ImageSource = NativeMethods.CaptureScreen(cur.X - src / 2, cur.Y - src / 2, src, src);
        }
        catch
        {
            // Màn hình khóa / UAC: bỏ qua khung này.
        }
    }
}
