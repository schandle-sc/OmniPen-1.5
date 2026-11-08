using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniPen.Services;
using Shapes = System.Windows.Shapes;

namespace OmniPen.Views;

/// <summary>
/// Vòng quay gọi tên ngẫu nhiên. Kết quả được chọn bằng Random.Shared TRƯỚC khi quay, sau đó tính góc dừng
/// để kim chỉ đúng ô đó (hoạt ảnh chậm dần kiểu QuarticEase) → công bằng và không phụ thuộc hoạt ảnh.
/// Danh sách lưu trong cài đặt để dùng lại cho các buổi sau.
/// </summary>
public partial class NamePickerWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0xAC, 0xFE), Color.FromRgb(0xB0, 0x66, 0xFE), Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xFB, 0xBF, 0x24),
        Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xA3, 0xE6, 0x35)
    };

    private readonly AppSettings _settings;
    private List<string> _names = new();
    private readonly List<string> _picked = new();
    private bool _spinning;

    public NamePickerWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        NamesBox.Text = string.IsNullOrWhiteSpace(settings.PickerNames)
            ? "Nguyễn Văn An\nTrần Thị Bình\nLê Hoàng Cường\nPhạm Minh Dũng\nHoàng Thu Hà\nVõ Gia Huy\nĐặng Ngọc Lan\nBùi Quốc Minh"
            : settings.PickerNames;
        ApplyNames();
        Closed += (_, _) =>
        {
            _settings.PickerNames = NamesBox.Text;
            SettingsService.Save(_settings);
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyNames();

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        _picked.Clear();
        ApplyNames();
    }

    private void ApplyNames()
    {
        _names = NamesBox.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0)
            .Where(s => !_picked.Contains(s)).Distinct().ToList();
        CountText.Text = $"Còn {_names.Count} tên trên vòng quay" + (_picked.Count > 0 ? $" · đã gọi {_picked.Count}" : "");
        BtnSpin.IsEnabled = _names.Count > 0;
        DrawWheel();
    }

    private void DrawWheel()
    {
        Wheel.Children.Clear();
        int n = Math.Max(1, _names.Count);
        double seg = 360.0 / n;
        const double r = 200, cx = 200, cy = 200;

        for (int i = 0; i < _names.Count; i++)
        {
            double a0 = (-90 + i * seg) * Math.PI / 180, a1 = (-90 + (i + 1) * seg) * Math.PI / 180;
            var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
            fig.Segments.Add(new LineSegment(new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), true));
            fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), new Size(r, r), 0, seg > 180, SweepDirection.Clockwise, true));
            Wheel.Children.Add(new Shapes.Path
            {
                Data = n == 1 ? new EllipseGeometry(new Point(cx, cy), r, r) : new PathGeometry(new[] { fig }),
                Fill = new SolidColorBrush(Palette[i % Palette.Length]),
                Stroke = Brushes.White,
                StrokeThickness = 2
            });

            // Nhãn tên xoay theo tia giữa ô.
            double mid = -90 + (i + 0.5) * seg;
            var label = new TextBlock
            {
                Text = _names[i].Length > 18 ? _names[i][..17] + "…" : _names[i],
                FontSize = Math.Clamp(260.0 / n, 9, 18),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
                Width = r - 50,
                TextAlignment = TextAlignment.Right
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Đặt mép trái nhãn tại tâm, dời ra 35 rồi xoay quanh tâm vòng quay → chữ chạy dọc tia giữa ô.
            Canvas.SetLeft(label, cx);
            Canvas.SetTop(label, cy - label.DesiredSize.Height / 2);
            label.RenderTransform = new TransformGroup
            {
                Children = { new TranslateTransform(35, 0), new RotateTransform(mid, 0, label.DesiredSize.Height / 2) }
            };
            Wheel.Children.Add(label);
        }
    }

    private void Spin_Click(object sender, RoutedEventArgs e)
    {
        if (_spinning || _names.Count == 0) return;
        _spinning = true;
        BtnSpin.IsEnabled = false;
        ResultText.Text = "…";

        int n = _names.Count;
        int pick = Random.Shared.Next(n);
        double seg = 360.0 / n;
        // Ô i có tâm ở góc -90 + (i+0.5)·seg; kim ở -90°. Cần xoay thêm sao cho tâm ô về đúng kim,
        // lệch ngẫu nhiên trong ô (±35%) cho tự nhiên.
        double jitter = (Random.Shared.NextDouble() - 0.5) * seg * 0.7;
        double current = WheelRotate.Angle % 360;
        double targetMod = ((-(pick + 0.5) * seg - jitter) % 360 + 360) % 360;
        double delta = (targetMod - current + 360) % 360;
        double to = WheelRotate.Angle + 360 * 6 + delta;

        var anim = new DoubleAnimation(WheelRotate.Angle, to, TimeSpan.FromSeconds(5))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        string name = _names[pick];
        anim.Completed += (_, _) =>
        {
            WheelRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            WheelRotate.Angle = to % 360;
            ResultText.Text = "🎉 " + name;
            _spinning = false;
            if (RemoveCheck.IsChecked == true)
            {
                _picked.Add(name);
                ApplyNames();
            }
            BtnSpin.IsEnabled = _names.Count > 0;
        };
        WheelRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
    }
}
