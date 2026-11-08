using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OmniPen.Interop;

namespace OmniPen.Views;

/// <summary>
/// Đồng hồ đếm ngược nổi cho bài kiểm tra / hoạt động nhóm. Kéo để di chuyển; nút nhanh 1–45 phút;
/// vòng tiến độ đổi màu xanh → vàng → đỏ; hết giờ thì nhấp nháy và phát âm báo.
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private TimeSpan _total = TimeSpan.FromMinutes(15);
    private TimeSpan _remaining = TimeSpan.FromMinutes(15);
    private DateTime _endAt;
    private bool _running, _rang;

    public CountdownWindow()
    {
        InitializeComponent();
        foreach (int m in new[] { 1, 3, 5, 10, 15, 45 })
        {
            var b = new Button { Content = $"{m}′", Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2), Focusable = false, Cursor = Cursors.Hand };
            int minutes = m;
            b.Click += (_, _) => SetDuration(TimeSpan.FromMinutes(minutes));
            PresetPanel.Children.Add(b);
        }
        _timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.AddExStyle(hwnd, Win32Helper.WS_EX_NOACTIVATE | Win32Helper.WS_EX_TOOLWINDOW);
        };
        Loaded += (_, _) =>
        {
            // Góc trên-phải màn hình chính.
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 20;
            Top = wa.Top + 80;
        };
        Render();
    }

    private void SetDuration(TimeSpan t)
    {
        _running = false;
        _timer.Stop();
        _total = _remaining = t;
        _rang = false;
        BtnStart.Content = "▶  Bắt đầu";
        Render();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _running = false;
            _timer.Stop();
            _remaining = _endAt - DateTime.UtcNow;
            BtnStart.Content = "▶  Tiếp tục";
        }
        else
        {
            if (_remaining <= TimeSpan.Zero) _remaining = _total;
            _endAt = DateTime.UtcNow + _remaining;
            _running = true;
            _rang = false;
            _timer.Start();
            BtnStart.Content = "⏸  Tạm dừng";
        }
        Render();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => SetDuration(_total);

    private void Tick()
    {
        _remaining = _endAt - DateTime.UtcNow;
        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            if (!_rang)
            {
                _rang = true;
                SystemSounds.Exclamation.Play();
            }
            // Nhấp nháy khi hết giờ.
            Card.Opacity = DateTime.UtcNow.Millisecond < 500 ? 1 : 0.55;
            BtnStart.Content = "▶  Bắt đầu lại";
        }
        Render();
    }

    private void Render()
    {
        TimeSpan r = _remaining < TimeSpan.Zero ? TimeSpan.Zero : _remaining;
        int totalSec = (int)Math.Ceiling(r.TotalSeconds);
        TimeText.Text = totalSec >= 3600 ? $"{totalSec / 3600}:{totalSec / 60 % 60:00}:{totalSec % 60:00}" : $"{totalSec / 60:00}:{totalSec % 60:00}";
        if (_remaining > TimeSpan.Zero) Card.Opacity = 1;

        double frac = _total.TotalSeconds <= 0 ? 0 : Math.Clamp(r.TotalSeconds / _total.TotalSeconds, 0, 1);
        Color c = frac > 0.5 ? Color.FromRgb(0x4F, 0xAC, 0xFE) : frac > 0.2 ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0xEF, 0x44, 0x44);
        ProgressArc.Stroke = new SolidColorBrush(c);
        TimeText.Foreground = r == TimeSpan.Zero ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)) : Brushes.White;
        ProgressArc.Data = Arc(frac);
    }

    /// <summary>Cung tròn tiến độ (bắt đầu từ 12 giờ, theo chiều kim đồng hồ) trong khung 150×150, bán kính 70.</summary>
    private static Geometry Arc(double frac)
    {
        if (frac <= 0.0001) return Geometry.Empty;
        const double cx = 75, cy = 75, r = 70;
        if (frac >= 0.9999) return new EllipseGeometry(new Point(cx, cy), r, r);
        double a = frac * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }
}
