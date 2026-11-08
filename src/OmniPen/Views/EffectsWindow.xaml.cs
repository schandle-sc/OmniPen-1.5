using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OmniPen.Interop;
using Shapes = System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace OmniPen.Views;

/// <summary>
/// Lớp hiệu ứng trình chiếu: gợn sóng khi click (trái xanh / phải cam / giữa xanh lá), bong bóng phím tắt,
/// khung focus làm tối mọi thứ trừ cửa sổ dưới con trỏ. Luôn xuyên chuột → không cản trở thao tác.
/// </summary>
public partial class EffectsWindow : Window
{
    private IntPtr _hwnd;
    private Int32Rect _bounds;
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly uint _myPid = (uint)Environment.ProcessId;
    private Border? _lastBubble;

    public bool FocusEnabled => _focusTimer.IsEnabled;

    public EffectsWindow()
    {
        InitializeComponent();
        _focusTimer.Tick += (_, _) => UpdateFocus();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.AddExStyle(_hwnd, Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_NOACTIVATE);
            Win32Helper.SetClickThrough(_hwnd, true);
            CoverAllScreens();
        };
    }

    /// <summary>Phủ toàn desktop ảo bằng pixel vật lý (giống CanvasWindow).</summary>
    public void CoverAllScreens()
    {
        if (_hwnd == IntPtr.Zero) return;
        var screens = WinForms.Screen.AllScreens;
        int l = screens.Min(s => s.Bounds.Left), t = screens.Min(s => s.Bounds.Top);
        int r = screens.Max(s => s.Bounds.Right), b = screens.Max(s => s.Bounds.Bottom);
        _bounds = new Int32Rect(l, t, r - l, b - t);
        Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST, l, t, r - l, b - t,
            Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_NOOWNERZORDER);
    }

    /// <summary>Pixel vật lý trên màn hình → tọa độ DIP trong cửa sổ này.</summary>
    private Point ToLocal(Point screen)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new Point((screen.X - _bounds.X) / dpi.DpiScaleX, (screen.Y - _bounds.Y) / dpi.DpiScaleY);
    }

    private Rect ToLocal(Win32Helper.RECT r)
    {
        Point a = ToLocal(new Point(r.Left, r.Top)), b = ToLocal(new Point(r.Right, r.Bottom));
        return new Rect(a, b);
    }

    // ------------------------------------------------------------------ Gợn sóng click

    public void ShowRipple(Point screen, MouseButton button)
    {
        Point p = ToLocal(screen);
        Color c = button switch
        {
            MouseButton.Right => Color.FromRgb(0xFF, 0x70, 0x43),
            MouseButton.Middle => Color.FromRgb(0x66, 0xBB, 0x6A),
            _ => Color.FromRgb(0x29, 0xB6, 0xF6)
        };

        for (int i = 0; i < 2; i++)
        {
            var ring = new Shapes.Ellipse
            {
                Width = 60,
                Height = 60,
                Stroke = new SolidColorBrush(c),
                StrokeThickness = 4,
                Fill = i == 0 ? new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)) : null,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.2, 0.2)
            };
            Canvas.SetLeft(ring, p.X - 30);
            Canvas.SetTop(ring, p.Y - 30);
            RippleLayer.Children.Add(ring);

            var begin = TimeSpan.FromMilliseconds(i * 120);
            var dur = TimeSpan.FromMilliseconds(550);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var st = (ScaleTransform)ring.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.2, 1.2, dur) { BeginTime = begin, EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1.2, dur) { BeginTime = begin, EasingFunction = ease });
            var fade = new DoubleAnimation(0.95, 0, dur) { BeginTime = begin };
            fade.Completed += (_, _) => RippleLayer.Children.Remove(ring);
            ring.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ------------------------------------------------------------------ Bong bóng phím

    /// <summary>Hiện tổ hợp phím ở giữa cạnh dưới màn hình chính; tối đa 3 bong bóng xếp chồng, mỗi cái tự mờ sau 1,6 giây.</summary>
    public void ShowKeys(string combo)
    {
        // Bấm lặp lại cùng tổ hợp → tăng bộ đếm "×2, ×3" thay vì thêm bong bóng.
        if (_lastBubble is { Tag: string last } && last == combo && KeyStack.Children.Contains(_lastBubble)
            && _lastBubble.Child is StackPanel sp && sp.Children.Count == 2 && sp.Children[1] is TextBlock counter)
        {
            int n = int.TryParse(counter.Text.TrimStart('×', ' '), out int v) ? v + 1 : 2;
            counter.Text = $"  ×{n}";
            counter.Visibility = Visibility.Visible;
            Restart(_lastBubble);
            return;
        }

        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        string[] parts = combo.Split(" + ");
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) keys.Children.Add(new TextBlock { Text = " + ", Foreground = Brushes.White, FontSize = 26, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            keys.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                BorderThickness = new Thickness(1, 1, 1, 3),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 4, 14, 4),
                Child = new TextBlock { Text = parts[i], Foreground = Brushes.White, FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") }
            });
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(keys);
        row.Children.Add(new TextBlock { Text = "", Foreground = Brushes.Gold, FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed });

        var bubble = new Border
        {
            Tag = combo,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0F, 0x17, 0x2A)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5 }
        };

        KeyStack.Children.Add(bubble);
        while (KeyStack.Children.Count > 3) KeyStack.Children.RemoveAt(0);
        _lastBubble = bubble;
        PlaceKeyStack();
        Restart(bubble);

        // Nảy lên
        var tt = new TranslateTransform(0, 20);
        bubble.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Restart(Border bubble)
    {
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.1))));
        fade.Completed += (_, _) =>
        {
            if (bubble.Opacity <= 0.01) KeyStack.Children.Remove(bubble);
        };
        bubble.BeginAnimation(OpacityProperty, fade);
    }

    private void PlaceKeyStack()
    {
        var wa = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).WorkingArea;
        KeyStack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point bottomCenter = ToLocal(new Point(wa.Left + wa.Width / 2.0, wa.Bottom));
        Canvas.SetLeft(KeyStack, bottomCenter.X - KeyStack.DesiredSize.Width / 2);
        Canvas.SetTop(KeyStack, bottomCenter.Y - KeyStack.DesiredSize.Height - 40);
    }

    // ------------------------------------------------------------------ Khung focus

    public void SetFocusEnabled(bool on)
    {
        if (on) { _focusTimer.Start(); UpdateFocus(); }
        else
        {
            _focusTimer.Stop();
            FocusDim.Visibility = FocusFrame.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Tìm cửa sổ trên cùng (theo thứ tự Z) chứa con trỏ, bỏ qua cửa sổ của chính OmniPen, cửa sổ ẩn/thu nhỏ/
    /// bị "cloak" (ứng dụng UWP ẩn), desktop và taskbar. Dùng khung DWM (không gồm bóng đổ) cho viền chính xác.
    /// </summary>
    private void UpdateFocus()
    {
        if (!NativeMethods.GetCursorPos(out var cur)) return;
        Win32Helper.RECT? found = null;

        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return true;
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (pid == _myPid) return true;
            if (NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if ((Win32Helper.GetExStyle(h) & Win32Helper.WS_EX_TRANSPARENT) != 0) return true;
            var cls = new StringBuilder(64);
            NativeMethods.GetClassName(h, cls, 64);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;

            if (NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32Helper.RECT r, 16) != 0)
                if (!Win32Helper.GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 40) return true;
            if (cur.X < r.Left || cur.X >= r.Right || cur.Y < r.Top || cur.Y >= r.Bottom) return true;

            found = r;
            return false; // dừng: đã gặp cửa sổ trên cùng
        }, IntPtr.Zero);

        if (found is not { } rect)
        {
            FocusDim.Visibility = FocusFrame.Visibility = Visibility.Collapsed;
            return;
        }

        Rect hole = ToLocal(rect);
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var cut = new RectangleGeometry(hole, 8, 8);
        FocusDim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, cut);
        FocusDim.Visibility = Visibility.Visible;

        FocusFrame.Width = hole.Width + 4;
        FocusFrame.Height = hole.Height + 4;
        Canvas.SetLeft(FocusFrame, hole.X - 2);
        Canvas.SetTop(FocusFrame, hole.Y - 2);
        FocusFrame.Visibility = Visibility.Visible;
    }
}
