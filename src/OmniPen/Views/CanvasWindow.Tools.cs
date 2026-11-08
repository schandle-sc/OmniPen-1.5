using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;
using OmniPen.Ink;

namespace OmniPen.Views;

/// <summary>Công cụ tự xử lý chuột/bút: hình, compa, thước kẻ, bàn tay, chọn chữ PDF, cuộn chuột.</summary>
public partial class CanvasWindow
{
    private enum CompassPhase { Idle, SettingRadius, Armed, Drawing }

    private int _tableRows = 3, _tableCols = 3;

    private readonly RulerControl _ruler = new() { Visibility = Visibility.Collapsed };

    // Lớp xem trước
    private readonly Shapes.Path _previewPath = new() { StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Shapes.Path _guidePath = new() { Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } };
    private readonly List<Shapes.Rectangle> _selectionRects = new();

    // Trạng thái kéo
    private bool _pointerActive;
    private Point _startPage;     // điểm bắt đầu (tọa độ trang)
    private Point _lastScreen;    // điểm trước (tọa độ màn hình) – cho bàn tay
    private bool _middlePanning;

    // Compa
    private CompassPhase _compass = CompassPhase.Idle;
    private Point _compassCenter;
    private double _compassRadius;
    private double _arcStart, _arcSweep, _arcLastAngle;

    // Chọn chữ PDF
    private int _selStart = -1, _selEnd = -1;
    private Popup? _textPopup;

    private void InitTools()
    {
        InitTeaching();
        OverlayLayer.Children.Add(_ruler);
        PreviewLayer.Children.Add(_guidePath);
        PreviewLayer.Children.Add(_previewPath);

        _pdfRerender.Tick += (_, _) =>
        {
            _pdfRerender.Stop();
            if (_boardOn && CurrentPage.IsPdf) _ = RenderPdfBackgroundAsync(CurrentPage);
        };

        // Preview* (tunnel) ở RootGrid chạy TRƯỚC InkCanvas → công cụ tự xử lý chặn được thao tác trước khi
        // InkCanvas hiểu nhầm thành nét vẽ.
        RootGrid.PreviewMouseDown += OnToolMouseDown;
        RootGrid.PreviewMouseMove += OnToolMouseMove;
        RootGrid.PreviewMouseUp += OnToolMouseUp;
        RootGrid.PreviewMouseWheel += OnMouseWheel;
        RootGrid.LostMouseCapture += (_, _) => { _pointerActive = false; _middlePanning = false; };
    }

    private void UpdateToolPreview()
    {
        _previewPath.Stroke = new SolidColorBrush(_penColor);
        _previewPath.StrokeThickness = _penSize;
    }

    /// <summary>Hủy thao tác đang dở (hình đang kéo, compa, vùng chọn chữ). Trả true nếu có gì để hủy.</summary>
    private bool CancelToolInteraction()
    {
        bool had = _pointerActive || _compass != CompassPhase.Idle || _selStart >= 0;
        _pointerActive = false;
        _middlePanning = false;
        _compass = CompassPhase.Idle;
        _previewPath.Data = null;
        _guidePath.Data = null;
        ClearTextSelection();
        if (RootGrid.IsMouseCaptured) RootGrid.ReleaseMouseCapture();
        return had;
    }

    private static bool IsCustomTool(InkTool t) => t is InkTool.Shape or InkTool.Compass or InkTool.Pan or InkTool.TextSelect or InkTool.Blur;

    private Point _blurStartScreen;

    private bool IsOnRuler(object? source) =>
        source is DependencyObject d && (d == _ruler || IsDescendant(d, _ruler));

    private static bool IsDescendant(DependencyObject d, DependencyObject ancestor)
    {
        for (DependencyObject? cur = d; cur is not null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    #region Chuột: hình, compa, bàn tay, chọn chữ

    private void OnToolMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Nút giữa chuột: kéo trang ở mọi công cụ (khi bật bảng).
        if (e.ChangedButton == MouseButton.Middle && _boardOn)
        {
            _middlePanning = true;
            _lastScreen = e.GetPosition(RootGrid);
            RootGrid.CaptureMouse();
            e.Handled = true;
            return;
        }

        // Chuột phải khi dùng compa: đặt lại tâm.
        if (e.ChangedButton == MouseButton.Right && _tool == InkTool.Compass)
        {
            CancelToolInteraction();
            e.Handled = true;
            return;
        }

        // Bấm vào băng che chữ → mở đáp án (khi nhả chuột).
        if (e.ChangedButton == MouseButton.Left && TryBeginTapeTap(e.GetPosition(Surface)))
        {
            RootGrid.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || _mode != InteractionMode.Draw || !IsCustomTool(_tool)) return;
        if (IsOnRuler(e.OriginalSource) || _textPopup?.IsOpen == true) return;

        Point page = e.GetPosition(Surface);
        _lastScreen = e.GetPosition(RootGrid);
        _startPage = page;
        _pointerActive = true;

        switch (_tool)
        {
            case InkTool.Shape:
                BeginGesture();
                break;

            case InkTool.Compass:
                BeginGesture();
                if (_compass == CompassPhase.Armed && (page - _compassCenter).Length > 14 / Math.Max(0.2, PageZoom))
                {
                    _compass = CompassPhase.Drawing;
                    _arcStart = _arcLastAngle = Math.Atan2(page.Y - _compassCenter.Y, page.X - _compassCenter.X);
                    _arcSweep = 0;
                }
                else
                {
                    _compass = CompassPhase.SettingRadius;
                    _compassCenter = page;
                    _compassRadius = 0;
                }
                UpdateCompassPreview(page);
                break;

            case InkTool.TextSelect:
                ClearTextSelection();
                _selStart = _selEnd = HitWord(page);
                UpdateTextSelection();
                break;

            case InkTool.Blur:
                _blurStartScreen = RootGrid.PointToScreen(_lastScreen);
                break;
        }

        RootGrid.CaptureMouse();
        e.Handled = true;
    }

    private void OnToolMouseMove(object sender, MouseEventArgs e)
    {
        Point screen = e.GetPosition(RootGrid);

        if (_middlePanning || (_pointerActive && _tool == InkTool.Pan))
        {
            PanBy(screen.X - _lastScreen.X, screen.Y - _lastScreen.Y);
            _lastScreen = screen;
            e.Handled = true;
            return;
        }

        if (!_pointerActive) return;
        Point page = e.GetPosition(Surface);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_tool)
        {
            case InkTool.Shape:
                _previewPath.Data = ShapeBuilder.ToGeometry(
                    ShapeBuilder.Build(_shape, _startPage, page, shift, _penSize, _tableRows, _tableCols));
                break;

            case InkTool.Compass:
                UpdateCompassPreview(page);
                break;

            case InkTool.Blur:
                _guidePath.StrokeThickness = 2 / Math.Max(0.2, PageZoom);
                _guidePath.Data = new RectangleGeometry(new Rect(_startPage, page));
                break;

            case InkTool.TextSelect:
                int idx = HitWord(page);
                if (idx >= 0 && idx != _selEnd)
                {
                    if (_selStart < 0) _selStart = idx;
                    _selEnd = idx;
                    UpdateTextSelection();
                }
                break;
        }
        e.Handled = true;
    }

    private void OnToolMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _pendingTape is not null)
        {
            TryEndTapeTap(e.GetPosition(Surface));
            RootGrid.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_middlePanning && e.ChangedButton == MouseButton.Middle)
        {
            _middlePanning = false;
            RootGrid.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (!_pointerActive || e.ChangedButton != MouseButton.Left) return;

        _pointerActive = false;
        RootGrid.ReleaseMouseCapture();
        Point page = e.GetPosition(Surface);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_tool)
        {
            case InkTool.Shape:
                _previewPath.Data = null;
                if ((page - _startPage).Length > 3)
                {
                    var polylines = ShapeBuilder.Build(_shape, _startPage, page, shift, _penSize, _tableRows, _tableCols);
                    CommitPolylines(polylines);
                }
                EndGestureNow();
                break;

            case InkTool.Compass:
                if (_compass == CompassPhase.SettingRadius)
                {
                    _compassRadius = (page - _compassCenter).Length;
                    _compass = _compassRadius > 4 ? CompassPhase.Armed : CompassPhase.Idle;
                }
                else if (_compass == CompassPhase.Drawing)
                {
                    if (Math.Abs(_arcSweep) > Math.PI / 180)
                        CommitPolylines(new[] { ShapeBuilder.Arc(_compassCenter, _compassRadius, _arcStart, _arcSweep) });
                    _compass = CompassPhase.Armed;
                }
                EndGestureNow();
                UpdateCompassPreview(page);
                break;

            case InkTool.TextSelect:
                if (_selStart >= 0 && _selEnd >= 0) ShowTextPopup();
                break;

            case InkTool.Blur:
                _guidePath.Data = null;
                Point endScreen = RootGrid.PointToScreen(e.GetPosition(RootGrid));
                var r = new Rect(_blurStartScreen, endScreen); // PointToScreen trả về pixel vật lý
                if (r.Width < 8 || r.Height < 8)
                    BlurRemoveRequested?.Invoke(this, endScreen);
                else
                    BlurRegionRequested?.Invoke(this, new Int32Rect((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)));
                break;
        }
        e.Handled = true;
    }

    private double PageZoom => _boardOn ? CurrentPage.Zoom : 1.0;

    /// <summary>Ghi các đường gấp khúc thành nét mực (cùng một bước Undo vì đang trong nhóm cử chỉ).</summary>
    private void CommitPolylines(IEnumerable<IReadOnlyList<Point>> polylines)
    {
        DrawingAttributes da = ShapeAttributes();
        StrokeCollection strokes = CurrentPage.Strokes;
        foreach (var pl in polylines)
            if (pl.Count > 0) strokes.Add(ShapeBuilder.ToStroke(pl, da));
    }

    #endregion

    #region Compa

    private void UpdateCompassPreview(Point pointer)
    {
        if (_tool != InkTool.Compass || _compass == CompassPhase.Idle)
        {
            _guidePath.Data = null;
            _previewPath.Data = null;
            return;
        }

        double r = _compass == CompassPhase.SettingRadius ? (pointer - _compassCenter).Length : _compassRadius;
        var guide = new GeometryGroup();
        // Dấu tâm.
        double m = 8 / PageZoom;
        guide.Children.Add(new LineGeometry(_compassCenter + new Vector(-m, 0), _compassCenter + new Vector(m, 0)));
        guide.Children.Add(new LineGeometry(_compassCenter + new Vector(0, -m), _compassCenter + new Vector(0, m)));
        // Đường tròn mờ.
        guide.Children.Add(new EllipseGeometry(_compassCenter, r, r));

        if (_compass == CompassPhase.SettingRadius)
        {
            guide.Children.Add(new LineGeometry(_compassCenter, pointer));
        }
        else if (_compass == CompassPhase.Drawing)
        {
            double a = Math.Atan2(pointer.Y - _compassCenter.Y, pointer.X - _compassCenter.X);
            double d = a - _arcLastAngle;
            // Mở gói góc để quét liên tục qua mốc ±180°.
            if (d > Math.PI) d -= 2 * Math.PI;
            if (d < -Math.PI) d += 2 * Math.PI;
            _arcSweep = Math.Clamp(_arcSweep + d, -2 * Math.PI, 2 * Math.PI);
            _arcLastAngle = a;
            guide.Children.Add(new LineGeometry(_compassCenter, pointer));
            _previewPath.Data = ShapeBuilder.ToGeometry(new[] { ShapeBuilder.Arc(_compassCenter, _compassRadius, _arcStart, _arcSweep) });
        }
        else
        {
            _previewPath.Data = null;
        }

        _guidePath.StrokeThickness = 1.5 / PageZoom;
        _guidePath.Data = guide;
    }

    #endregion

    #region Thước kẻ

    public void ToggleRuler()
    {
        if (_ruler.Visibility == Visibility.Visible)
        {
            _ruler.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Đặt thước giữa vùng đang nhìn.
            Rect view = VisiblePageRect();
            Canvas.SetLeft(_ruler, view.X + (view.Width - _ruler.Width) / 2);
            Canvas.SetTop(_ruler, view.Y + view.Height * 0.55);
            _ruler.Angle = 0;
            _ruler.Visibility = Visibility.Visible;
            if (_mode == InteractionMode.Mouse && !_boardOn) SetMode(InteractionMode.Draw);
            if (_tool is not (InkTool.Pen or InkTool.Highlighter)) SetTool(InkTool.Pen);
        }
        RaiseState();
    }

    /// <summary>
    /// Nét bút vừa vẽ: nắn theo thước (nếu cả hai đầu nét sát mép thước) rồi tự cuộn trang nếu cần.
    /// </summary>
    private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        Stroke stroke = e.Stroke;
        switch (_tool)
        {
            case InkTool.Laser: ConvertToLaserTrail(stroke); return;
            case InkTool.Tape: ConvertToTape(stroke); return;
        }

        if (_snapRequested && _tool == InkTool.Pen)
            stroke = SnapToShape(stroke) ?? stroke;
        else if (_ruler.Visibility == Visibility.Visible && _tool is InkTool.Pen or InkTool.Highlighter)
            stroke = SnapToRuler(stroke) ?? stroke;

        AutoScrollAfterStroke(stroke.GetBounds());
    }

    private Stroke? SnapToRuler(Stroke stroke)
    {
        StylusPointCollection pts = stroke.StylusPoints;
        if (pts.Count < 2) return null;

        InkCanvas ink = CurrentPage.Ink;
        GeneralTransform toInk;
        try { toInk = _ruler.TransformToVisual(ink); }
        catch (InvalidOperationException) { return null; } // thước chưa nằm trong cây hiển thị

        Point first = (Point)pts[0], last = (Point)pts[^1];
        double tol = 30 / Math.Max(0.2, PageZoom);
        Point rulerCenter = toInk.Transform(new Point(_ruler.ActualWidth / 2, _ruler.ActualHeight / 2));

        foreach (var (la, lb) in _ruler.LocalEdges)
        {
            Point a = toInk.Transform(la), b = toInk.Transform(lb);
            Vector dir = b - a;
            double len = dir.Length;
            if (len < 1) continue;
            dir /= len;
            var normal = new Vector(-dir.Y, dir.X);

            double d1 = Math.Abs(Vector.Multiply(first - a, normal));
            double d2 = Math.Abs(Vector.Multiply(last - a, normal));
            if (d1 > tol || d2 > tol) continue;

            // Chiếu mọi điểm lên mép thước, lấy đoạn từ điểm chiếu nhỏ nhất đến lớn nhất.
            double tMin = double.MaxValue, tMax = double.MinValue;
            foreach (StylusPoint sp in pts)
            {
                double t = Vector.Multiply((Point)sp - a, dir);
                tMin = Math.Min(tMin, t);
                tMax = Math.Max(tMax, t);
            }
            if (tMax - tMin < 4) continue;

            // Đẩy nét ra NGOÀI thước nửa độ dày bút để nét nằm sát mép như kẻ bằng thước thật.
            if (Vector.Multiply(rulerCenter - a, normal) > 0) normal = -normal;
            Vector offset = normal * (stroke.DrawingAttributes.Width / 2 + 0.5);

            var da = stroke.DrawingAttributes.Clone();
            da.FitToCurve = false;
            da.IgnorePressure = true;
            var straight = new Stroke(new StylusPointCollection(new[] { a + dir * tMin + offset, a + dir * tMax + offset }), da);

            StrokeCollection strokes = ink.Strokes;
            strokes.Remove(stroke);
            strokes.Add(straight);
            return straight;
        }
        return null;
    }

    #endregion

    #region Cuộn chuột

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_boardOn) return;
        StopSmoothScroll();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(RootGrid));
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            PanBy(e.Delta * 0.8, 0);
        else
            PanBy(0, e.Delta * 0.8);
        e.Handled = true;
    }

    #endregion

    #region Chọn chữ PDF

    /// <summary>Từ gần điểm nhất (trong phạm vi 40 DIP), -1 nếu không có.</summary>
    private int HitWord(Point p)
    {
        IReadOnlyList<PdfWord> words = CurrentPage.Words;
        int best = -1;
        double bestDist = 40;
        for (int i = 0; i < words.Count; i++)
        {
            Rect r = words[i].Bounds;
            if (r.Contains(p)) return i;
            double dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    private IEnumerable<PdfWord> SelectedWords()
    {
        if (_selStart < 0 || _selEnd < 0) return Enumerable.Empty<PdfWord>();
        int a = Math.Min(_selStart, _selEnd), b = Math.Max(_selStart, _selEnd);
        return CurrentPage.Words.Skip(a).Take(b - a + 1);
    }

    /// <summary>Gom các từ đã chọn thành từng dòng (khung bao mỗi dòng) để tô/gạch theo dòng.</summary>
    private List<(Rect Bounds, string Text)> SelectedLines()
    {
        var lines = new List<(Rect Bounds, string Text)>();
        foreach (PdfWord w in SelectedWords())
        {
            if (lines.Count > 0)
            {
                var (r, t) = lines[^1];
                double cy = w.Bounds.Top + w.Bounds.Height / 2;
                bool sameLine = cy > r.Top && cy < r.Bottom && w.Bounds.Left >= r.Left - 2;
                if (sameLine)
                {
                    r.Union(w.Bounds);
                    lines[^1] = (r, t + " " + w.Text);
                    continue;
                }
            }
            lines.Add((w.Bounds, w.Text));
        }
        return lines;
    }

    private void UpdateTextSelection()
    {
        foreach (Shapes.Rectangle r in _selectionRects) PreviewLayer.Children.Remove(r);
        _selectionRects.Clear();

        foreach (var (bounds, _) in SelectedLines())
        {
            var rect = new Shapes.Rectangle
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x1E, 0x88, 0xE5))
            };
            Canvas.SetLeft(rect, bounds.X);
            Canvas.SetTop(rect, bounds.Y);
            PreviewLayer.Children.Add(rect);
            _selectionRects.Add(rect);
        }
    }

    private void ClearTextSelection()
    {
        _selStart = _selEnd = -1;
        foreach (Shapes.Rectangle r in _selectionRects) PreviewLayer.Children.Remove(r);
        _selectionRects.Clear();
        if (_textPopup is not null) _textPopup.IsOpen = false;
    }

    /// <summary>Menu nổi cạnh vùng chọn: Sao chép · Tô sáng · Gạch chân · Gạch ngang.</summary>
    private void ShowTextPopup()
    {
        if (_textPopup is null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(PopupButton("Sao chép", CopySelectedText));
            panel.Children.Add(PopupButton("Tô sáng", () => MarkSelection(TextMark.Highlight)));
            panel.Children.Add(PopupButton("Gạch chân", () => MarkSelection(TextMark.Underline)));
            panel.Children.Add(PopupButton("Gạch ngang", () => MarkSelection(TextMark.Strike)));

            _textPopup = new Popup
            {
                Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(4),
                    Child = panel
                },
                Placement = PlacementMode.MousePoint,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade
            };
            _textPopup.Closed += (_, _) =>
            {
                // Đóng menu mà không chọn gì → bỏ vùng chọn.
                if (!_pointerActive) ClearTextSelectionKeepPopup();
            };
        }
        _textPopup.IsOpen = true;
    }

    private void ClearTextSelectionKeepPopup()
    {
        _selStart = _selEnd = -1;
        foreach (Shapes.Rectangle r in _selectionRects) PreviewLayer.Children.Remove(r);
        _selectionRects.Clear();
    }

    private Button PopupButton(string text, Action action)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(2),
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false
        };
        b.Click += (_, _) =>
        {
            action();
            if (_textPopup is not null) _textPopup.IsOpen = false;
        };
        return b;
    }

    private void CopySelectedText()
    {
        var sb = new StringBuilder();
        foreach (var (_, text) in SelectedLines()) sb.AppendLine(text);
        try { Clipboard.SetText(sb.ToString().TrimEnd()); }
        catch { /* Clipboard đang bị app khác giữ */ }
    }

    private enum TextMark { Highlight, Underline, Strike }

    /// <summary>
    /// Tô sáng / gạch chân / gạch ngang = tạo NÉT MỰC theo từng dòng chữ đã chọn
    /// → hoàn tác, tẩy, lưu, xuất file giống hệt nét tay.
    /// </summary>
    private void MarkSelection(TextMark mark)
    {
        var lines = SelectedLines();
        if (lines.Count == 0) return;

        PaperPage page = CurrentPage;
        page.History.BeginGroup();
        foreach (var (r, _) in lines)
        {
            DrawingAttributes da;
            Point a, b;
            switch (mark)
            {
                case TextMark.Highlight:
                    // Đầu bút chữ nhật cao bằng dòng chữ quét ngang → dải tô sáng phủ đúng dòng.
                    da = new DrawingAttributes
                    {
                        Color = Color.FromRgb(0xFF, 0xEB, 0x3B),
                        IsHighlighter = true,
                        StylusTip = StylusTip.Rectangle,
                        Width = 2,
                        Height = Math.Max(4, r.Height),
                        FitToCurve = false,
                        IgnorePressure = true
                    };
                    a = new Point(r.Left, r.Top + r.Height / 2);
                    b = new Point(r.Right, r.Top + r.Height / 2);
                    break;
                case TextMark.Underline:
                    da = LineAttributes(r);
                    a = new Point(r.Left, r.Bottom + 1);
                    b = new Point(r.Right, r.Bottom + 1);
                    break;
                default:
                    da = LineAttributes(r);
                    a = new Point(r.Left, r.Top + r.Height * 0.55);
                    b = new Point(r.Right, r.Top + r.Height * 0.55);
                    break;
            }
            page.Strokes.Add(new Stroke(new StylusPointCollection(new[] { a, b }), da));
        }
        page.History.EndGroup();
        ClearTextSelection();
    }

    private DrawingAttributes LineAttributes(Rect line) => new()
    {
        Color = _penColor,
        Width = Math.Clamp(line.Height / 9, 1.2, 3),
        Height = Math.Clamp(line.Height / 9, 1.2, 3),
        FitToCurve = false,
        IgnorePressure = true
    };

    #endregion
}
