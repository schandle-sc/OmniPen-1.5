using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OmniPen.Views;

/// <summary>
/// Thước kẻ điện tử: kéo thân thước để di chuyển, kéo núm tròn ở đầu phải để xoay (giữ Shift: bắt góc 15°).
/// Vạch chia theo kích thước thật trên giấy: 1 cm = 96 / 2.54 ≈ 37.8 DIP (khi zoom 100%).
/// Nét vẽ bằng bút bắt đầu và kết thúc sát mép thước sẽ được nắn thành đường thẳng dọc theo mép
/// (xử lý trong CanvasWindow.Tools).
/// </summary>
public sealed class RulerControl : FrameworkElement
{
    public const double Cm = 96 / 2.54;
    public const double LengthCm = 20;
    public const double Thickness = 76;
    private const double HandleRadius = 13;

    private readonly RotateTransform _rotate = new();
    private Point _dragStart;
    private double _leftStart, _topStart;
    private bool _moving, _rotating;

    private static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1));
    private static readonly Pen TickPen = Freeze(new Pen(Brushes.Black, 1));
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)));
    private static readonly Typeface Font = new("Segoe UI");

    public RulerControl()
    {
        Width = LengthCm * Cm + 2 * HandleRadius + 12;
        Height = Thickness;
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Cursor = Cursors.SizeAll;
        Focusable = false;
    }

    /// <summary>Góc xoay (độ).</summary>
    public double Angle
    {
        get => _rotate.Angle;
        set
        {
            _rotate.Angle = ((value % 360) + 360) % 360;
            InvalidateVisual();
        }
    }

    /// <summary>Hai mép dài của thước (đoạn thẳng trong hệ tọa độ cục bộ) – dùng để nắn nét.</summary>
    public (Point A, Point B)[] LocalEdges => new[]
    {
        (new Point(0, 0), new Point(ActualWidth, 0)),
        (new Point(0, ActualHeight), new Point(ActualWidth, ActualHeight))
    };

    private Point HandleCenter => new(Width - HandleRadius - 6, Height / 2);

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, Width, Height);
        dc.DrawRoundedRectangle(BodyBrush, BorderPen, rect, 6, 6);

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x0 = 10;
        for (int mm = 0; mm <= LengthCm * 10; mm++)
        {
            double x = x0 + mm * Cm / 10;
            double len = mm % 10 == 0 ? 18 : mm % 5 == 0 ? 12 : 7;
            dc.DrawLine(TickPen, new Point(x, 0), new Point(x, len));          // mép trên
            dc.DrawLine(TickPen, new Point(x, Height), new Point(x, Height - len)); // mép dưới
            if (mm % 10 == 0)
            {
                var ft = new FormattedText((mm / 10).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Font, 11, Brushes.Black, dpi);
                dc.DrawText(ft, new Point(x - ft.Width / 2, 20));
            }
        }

        // Số đo góc ở giữa thước.
        var angle = new FormattedText($"{Angle:0}°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Font, 14, Brushes.DimGray, dpi);
        dc.DrawText(angle, new Point(Width / 2 - angle.Width / 2, Height / 2 - angle.Height / 2 + 6));

        // Núm xoay.
        dc.DrawEllipse(HandleBrush, null, HandleCenter, HandleRadius, HandleRadius);
        var rot = new FormattedText("↻", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Symbol"), 15, Brushes.White, dpi);
        dc.DrawText(rot, new Point(HandleCenter.X - rot.Width / 2, HandleCenter.Y - rot.Height / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Point local = e.GetPosition(this);
        _rotating = (local - HandleCenter).Length <= HandleRadius + 6;
        _moving = !_rotating;
        _dragStart = e.GetPosition(Parent as IInputElement);
        _leftStart = Canvas.GetLeft(this);
        _topStart = Canvas.GetTop(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Point p = e.GetPosition(Parent as IInputElement);

        if (_moving)
        {
            Canvas.SetLeft(this, _leftStart + (p.X - _dragStart.X));
            Canvas.SetTop(this, _topStart + (p.Y - _dragStart.Y));
        }
        else if (_rotating)
        {
            var center = new Point(Canvas.GetLeft(this) + Width / 2, Canvas.GetTop(this) + Height / 2);
            double deg = Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) deg = Math.Round(deg / 15) * 15;
            else if (Math.Abs(deg - Math.Round(deg / 45) * 45) < 2) deg = Math.Round(deg / 45) * 45; // hít nhẹ 0/45/90°
            Angle = deg;
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _moving = _rotating = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
