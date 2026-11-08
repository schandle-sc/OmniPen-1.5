using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using OmniPen.Ink;
using OmniPen.Services;
using Shapes = System.Windows.Shapes;

namespace OmniPen.Views;

/// <summary>
/// Công cụ trợ giảng v1.5: bút laser tự mờ, băng che chữ (Cloze), tự nắn hình khi giữ bút,
/// chèn đồ thị, thay nét viết tay bằng văn bản.
/// </summary>
public partial class CanvasWindow
{
    // ---- Tự nắn hình (giữ bút 0,8 giây ở cuối nét)
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _inkDown, _snapRequested;
    private Point _lastMovePos;
    private DateTime _lastMoveTime;
    private double _travel;
    private Shapes.Ellipse? _snapIndicator;
    private bool _autoShape = true;

    // ---- Băng che chữ đang được nhấn
    private FrameworkElement? _pendingTape;
    private Point _tapeDownPos;
    private readonly HashSet<FrameworkElement> _revealing = new();

    private bool IsClickThrough => _mode == InteractionMode.Mouse && !_boardOn;

    public bool AutoShape
    {
        get => _autoShape;
        set => _autoShape = value;
    }

    private void InitTeaching()
    {
        _holdTimer.Tick += OnHoldTick;

        InkHost.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) => OnInkDown(e.GetPosition(Surface))), true);
        InkHost.AddHandler(PreviewStylusDownEvent, new StylusDownEventHandler((_, e) => OnInkDown(e.GetPosition(Surface))), true);
        InkHost.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler((_, e) => OnInkMove(e.GetPosition(Surface))), true);
        InkHost.AddHandler(PreviewStylusMoveEvent, new StylusEventHandler((_, e) => OnInkMove(e.GetPosition(Surface))), true);
        InkHost.AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => OnInkUp()), true);
        InkHost.AddHandler(PreviewStylusUpEvent, new StylusEventHandler((_, _) => OnInkUp()), true);

        // Bút/cảm ứng chạm vào băng che chữ: chặn InkCanvas bắt đầu nét mực (sự kiện Stylus đến TRƯỚC Mouse).
        RootGrid.PreviewStylusDown += (_, e) =>
        {
            if (TryBeginTapeTap(e.GetPosition(Surface))) e.Handled = true;
        };
        RootGrid.PreviewStylusUp += (_, e) =>
        {
            if (TryEndTapeTap(e.GetPosition(Surface))) e.Handled = true;
        };
    }

    #region Tự nắn hình

    private void OnInkDown(Point p)
    {
        _inkDown = true;
        _snapRequested = false;
        _travel = 0;
        _lastMovePos = p;
        _lastMoveTime = DateTime.UtcNow;
        HideSnapIndicator();
        if (_autoShape && _tool == InkTool.Pen && _mode == InteractionMode.Draw) _holdTimer.Start();
    }

    private void OnInkMove(Point p)
    {
        if (!_inkDown) return;
        double d = (p - _lastMovePos).Length;
        if (d < 3) return; // rung tay nhẹ không tính là di chuyển
        _travel += d;
        _lastMovePos = p;
        _lastMoveTime = DateTime.UtcNow;
        if (_snapRequested)
        {
            // Giữ rồi lại vẽ tiếp → hủy nắn hình.
            _snapRequested = false;
            HideSnapIndicator();
        }
    }

    private void OnInkUp()
    {
        _inkDown = false;
        _holdTimer.Stop();
        // Chỉ báo được ẩn sau khi StrokeCollected xử lý xong (nét được nắn).
        Dispatcher.InvokeAsync(HideSnapIndicator, DispatcherPriority.Background);
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        if (!_inkDown || _snapRequested || _tool != InkTool.Pen || _travel < 30) return;
        if ((DateTime.UtcNow - _lastMoveTime).TotalMilliseconds < 800) return;

        _snapRequested = true;
        ShowSnapIndicator(_lastMovePos);
    }

    /// <summary>Vòng tròn xanh nhấp nháy ở đầu bút: "nhả bút để nắn hình".</summary>
    private void ShowSnapIndicator(Point p)
    {
        double r = 14 / Math.Max(0.2, PageZoom);
        _snapIndicator = new Shapes.Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xAC, 0xFE)),
            StrokeThickness = 3 / Math.Max(0.2, PageZoom),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_snapIndicator, p.X - r);
        Canvas.SetTop(_snapIndicator, p.Y - r);
        PreviewLayer.Children.Add(_snapIndicator);
        var pulse = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(350)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _snapIndicator.BeginAnimation(OpacityProperty, pulse);
    }

    private void HideSnapIndicator()
    {
        if (_snapIndicator is null) return;
        PreviewLayer.Children.Remove(_snapIndicator);
        _snapIndicator = null;
    }

    /// <summary>Thay nét vừa vẽ bằng hình chuẩn (nếu nhận dạng được). Cùng nhóm cử chỉ → một bước Undo.</summary>
    private Stroke? SnapToShape(Stroke stroke)
    {
        _snapRequested = false;
        var pts = stroke.StylusPoints.Select(sp => new Point(sp.X, sp.Y)).ToList();
        List<List<Point>>? shapes = ShapeRecognizer.Recognize(pts);
        if (shapes is null) return null;

        StrokeCollection strokes = CurrentPage.Strokes;
        strokes.Remove(stroke);
        Stroke? first = null;
        foreach (var pl in shapes)
        {
            Stroke s = ShapeBuilder.ToStroke(pl, stroke.DrawingAttributes);
            strokes.Add(s);
            first ??= s;
        }
        return first;
    }

    #endregion

    #region Bút laser

    /// <summary>
    /// Nét laser không lưu vào trang: gỡ nét mực (thêm-rồi-bớt trong cùng nhóm cử chỉ = không có bước Undo)
    /// và thay bằng vệt phát sáng trên lớp xem trước, giữ ~2 giây rồi mờ dần trong 0,9 giây.
    /// </summary>
    private void ConvertToLaserTrail(Stroke stroke)
    {
        Geometry geo = stroke.GetGeometry();
        CurrentPage.Strokes.Remove(stroke);

        var glow = new Shapes.Path
        {
            Data = geo,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x7A)),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x17, 0x44),
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 1
            }
        };
        PreviewLayer.Children.Add(glow);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(900))
        {
            BeginTime = TimeSpan.FromSeconds(2.1),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => PreviewLayer.Children.Remove(glow);
        glow.BeginAnimation(OpacityProperty, fade);
    }

    #endregion

    #region Băng che chữ (Cloze tape)

    /// <summary>Nét quẹt bằng công cụ Băng → dải băng che kín khung bao của nét (tối thiểu 20 DIP chiều cao).</summary>
    private void ConvertToTape(Stroke stroke)
    {
        Rect b = stroke.GetBounds();
        CurrentPage.Strokes.Remove(stroke);
        if (b.Width < 8) return;

        double h = Math.Max(20, b.Height);
        var rect = new Rect(b.X - 2, b.Y + b.Height / 2 - h / 2 - 1, b.Width + 4, h + 2);
        FrameworkElement tape = PageElements.CreateTape(rect);

        PaperPage page = CurrentPage;
        InkCanvas ink = page.Ink;
        ink.Children.Add(tape);
        page.History.PushCustom(() => ink.Children.Remove(tape), () => ink.Children.Add(tape));
    }

    private FrameworkElement? HitTape(Point pagePoint) =>
        CurrentPage.Tapes.LastOrDefault(t => ExportService.ElementRect(t).Contains(pagePoint));

    /// <summary>Bấm vào băng (mọi công cụ trừ Chọn và Băng): ghi nhận, chờ nhả để mở.</summary>
    private bool TryBeginTapeTap(Point pagePoint)
    {
        if (IsClickThrough || _tool is InkTool.Select or InkTool.Tape) return false;
        FrameworkElement? tape = HitTape(pagePoint);
        if (tape is null) return false;
        _pendingTape = tape;
        _tapeDownPos = pagePoint;
        return true;
    }

    private bool TryEndTapeTap(Point pagePoint)
    {
        if (_pendingTape is not { } tape) return false;
        _pendingTape = null;
        if ((pagePoint - _tapeDownPos).Length < 12 / Math.Max(0.2, PageZoom))
            RevealTape(tape);
        return true;
    }

    /// <summary>Hiệu ứng "bốc hơi": phóng to nhẹ + mờ + nhòe trong 0,45 giây rồi gỡ băng. Hoàn tác được.</summary>
    private void RevealTape(FrameworkElement tape)
    {
        PaperPage page = CurrentPage;
        InkCanvas ink = page.Ink;
        // Bút/cảm ứng sinh cả sự kiện Stylus lẫn Mouse → chặn mở 2 lần cùng một băng.
        if (!ink.Children.Contains(tape) || !_revealing.Add(tape)) return;

        var scale = new ScaleTransform(1, 1);
        tape.RenderTransformOrigin = new Point(0.5, 0.5);
        tape.RenderTransform = scale;
        var blur = new BlurEffect { Radius = 0 };
        tape.Effect = blur;

        var dur = TimeSpan.FromMilliseconds(450);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            ink.Children.Remove(tape);
            ResetTapeVisual(tape);
            _revealing.Remove(tape);
            page.History.PushCustom(() => ink.Children.Add(tape), () => ink.Children.Remove(tape));
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.18, dur) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.6, dur) { EasingFunction = ease });
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 14, dur));
        tape.BeginAnimation(OpacityProperty, fade);
    }

    private static void ResetTapeVisual(FrameworkElement tape)
    {
        tape.BeginAnimation(OpacityProperty, null);
        tape.Opacity = 1;
        tape.RenderTransform = Transform.Identity;
        tape.Effect = null;
    }

    /// <summary>Mở tất cả băng trên trang hiện tại (menu "Mở hết đáp án").</summary>
    public void RevealAllTapes()
    {
        foreach (FrameworkElement t in CurrentPage.Tapes.ToList()) RevealTape(t);
    }

    #endregion

    #region Chèn đồ thị, thay nét bằng văn bản

    /// <summary>Chèn hệ trục + đồ thị vào giữa vùng đang nhìn: các đường là nét mực, chữ số là ảnh. Một bước Undo.</summary>
    public void InsertGraph(GraphResult g)
    {
        EndGestureNow();
        Rect view = VisiblePageRect();
        double ox = view.X + (view.Width - g.Size.Width) / 2, oy = view.Y + (view.Height - g.Size.Height) / 2;

        var strokes = new StrokeCollection();
        foreach (var (pts, color, width) in g.Lines)
        {
            var da = new DrawingAttributes { Color = color, Width = width, Height = width, FitToCurve = false, IgnorePressure = true };
            strokes.Add(new Stroke(new StylusPointCollection(pts.Select(p => new Point(p.X + ox, p.Y + oy))), da));
        }
        Image labels = PageElements.Create(ExportService.ToPng(g.Labels), new Rect(ox, oy, g.Size.Width, g.Size.Height), "graph");

        PaperPage page = CurrentPage;
        InkCanvas ink = page.Ink;
        page.History.RunWithoutHistory(() => ink.Strokes.Add(strokes));
        ink.Children.Insert(0, labels);
        page.History.PushCustom(
            () => { ink.Strokes.Remove(strokes); ink.Children.Remove(labels); },
            () => { ink.Strokes.Add(strokes); ink.Children.Insert(0, labels); });
        if (_mode != InteractionMode.Draw) SetMode(InteractionMode.Draw);
    }

    /// <summary>Nét đang chọn (công cụ Chọn); nếu không chọn gì thì toàn bộ nét của trang.</summary>
    public StrokeCollection SelectedOrAllStrokes()
    {
        StrokeCollection sel = CurrentPage.Ink.GetSelectedStrokes();
        return sel.Count > 0 ? sel : new StrokeCollection(CurrentPage.Strokes);
    }

    /// <summary>Thay các nét bằng một ảnh (VD chữ đã nhận dạng) tại vị trí của chúng. Một bước Undo.</summary>
    public void ReplaceStrokesWithImage(StrokeCollection strokes, byte[] png, Size size, string kind, string? text)
    {
        if (strokes.Count == 0) return;
        EndGestureNow();
        PaperPage page = CurrentPage;
        InkCanvas ink = page.Ink;
        Rect b = strokes.GetBounds();
        Image img = PageElements.Create(png, new Rect(b.X, b.Y + (b.Height - size.Height) / 2, size.Width, size.Height), kind, text);

        ink.Select(new StrokeCollection());
        page.History.RunWithoutHistory(() => ink.Strokes.Remove(strokes));
        ink.Children.Add(img);
        page.History.PushCustom(
            () => { ink.Children.Remove(img); ink.Strokes.Add(strokes); },
            () => { ink.Strokes.Remove(strokes); ink.Children.Add(img); });
    }

    #endregion
}
