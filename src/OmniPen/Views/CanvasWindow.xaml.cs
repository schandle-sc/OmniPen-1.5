using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OmniPen.Ink;
using OmniPen.Interop;
using OmniPen.Services;
using WinForms = System.Windows.Forms;

namespace OmniPen.Views;

/// <summary>Chế độ tương tác toàn cục.</summary>
public enum InteractionMode
{
    /// <summary>Chuột điều khiển ứng dụng phía sau (canvas xuyên chuột, nét vẽ vẫn hiển thị).</summary>
    Mouse,
    /// <summary>Canvas bắt input để vẽ / tẩy / dùng công cụ.</summary>
    Draw
}

public enum InkTool
{
    Pen,
    Highlighter,
    EraseByPoint,
    EraseByStroke,
    /// <summary>Chọn – di chuyển – đổi cỡ nét và ảnh (InkCanvas Select).</summary>
    Select,
    Shape,
    Compass,
    /// <summary>Bàn tay: kéo để di chuyển trang.</summary>
    Pan,
    /// <summary>Chọn chữ trên trang PDF.</summary>
    TextSelect,
    /// <summary>Bút laser / mực tàng hình: nét phát sáng tự mờ dần và biến mất sau 3 giây.</summary>
    Laser,
    /// <summary>Băng che chữ (Cloze): quẹt lên một từ để che bằng dải băng tối; bấm để mở.</summary>
    Tape,
    /// <summary>Làm mờ bảo mật: kéo khung để làm mờ trực tiếp một vùng màn hình.</summary>
    Blur
}

public enum ZoomCommand { In, Out, Reset, FitWidth, FitPage }

/// <summary>Ảnh chụp trạng thái canvas – Toolbar và Tray chỉ việc "vẽ lại" theo record này.</summary>
public sealed record CanvasState(
    InteractionMode Mode,
    InkTool Tool,
    ShapeKind Shape,
    Color PenColor,
    double PenSize,
    bool BoardOn,
    PaperType Paper,
    int PageIndex,
    int PageCount,
    bool CanUndo,
    bool CanRedo,
    bool RulerVisible,
    double Zoom,
    bool AutoScroll,
    bool HasPdfText);

/// <summary>
/// Lớp vẽ trong suốt phủ toàn bộ desktop ảo. Chia thành nhiều file partial:
///   CanvasWindow.xaml.cs   – cửa sổ, đa màn hình, xuyên chuột, công cụ, phím tắt
///   CanvasWindow.Board.cs  – trang, bảng, zoom/pan, tự cuộn, PDF, chèn ảnh, lưu/mở
///   CanvasWindow.Tools.cs  – hình, compa, thước kẻ, bàn tay, chọn chữ PDF
/// Không chứa nút bấm nào – mọi lệnh đến qua phương thức public; mọi thay đổi phát qua StateChanged.
/// </summary>
public partial class CanvasWindow : Window
{
    // alpha = 1/255: vô hình với mắt nhưng đủ để layered window nhận hit-test ở mọi pixel.
    private static readonly Brush HitTestableBrush = CreateFrozenBrush(Color.FromArgb(1, 0, 0, 0));

    private IntPtr _hwnd;
    private HwndSource? _hwndSource;

    private InteractionMode _mode = InteractionMode.Mouse;
    private InkTool _tool = InkTool.Pen;
    private ShapeKind _shape = ShapeKind.Line;
    private Color _penColor;
    private double _penSize;

    public event EventHandler<CanvasState>? CanvasStateChanged;

    /// <summary>Người dùng kéo khung bằng công cụ Làm mờ (pixel vật lý trên màn hình).</summary>
    public event EventHandler<Int32Rect>? BlurRegionRequested;

    /// <summary>Người dùng bấm (không kéo) bằng công cụ Làm mờ → gỡ vùng mờ tại điểm đó (pixel vật lý).</summary>
    public event EventHandler<Point>? BlurRemoveRequested;

    /// <summary>Điểm (pixel vật lý) dùng để chọn màn hình hiển thị bảng – App trả về tâm thanh công cụ.</summary>
    public Func<System.Drawing.Point?>? AnchorProvider { get; set; }

    public InteractionMode Mode => _mode;

    /// <summary>Desktop ảo theo PIXEL VẬT LÝ (dùng cho chụp màn hình, đặt vị trí toolbar…).</summary>
    public Int32Rect PhysicalBounds { get; private set; }

    public CanvasWindow(AppSettings settings)
    {
        InitializeComponent();

        _penColor = ParseColor(settings.PenColor, Colors.Red);
        _penSize = Math.Clamp(settings.PenSize, 1, 40);
        _autoScroll = settings.AutoScroll;
        _tableRows = settings.TableRows;
        _tableCols = settings.TableCols;
        _doc = new InkDocument(settings.DefaultPaper);

        // Gom mọi thay đổi nét từ lúc nhấn đến lúc nhả thành MỘT bước Undo.
        // handledEventsToo = true: InkCanvas tự đánh dấu Handled cho sự kiện bút/chuột.
        InkHost.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => BeginGesture()), true);
        InkHost.AddHandler(PreviewStylusDownEvent, new StylusDownEventHandler((_, _) => BeginGesture()), true);
        InkHost.AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => EndGestureDeferred()), true);
        InkHost.AddHandler(PreviewStylusUpEvent, new StylusEventHandler((_, _) => EndGestureDeferred()), true);
        InkHost.AddHandler(LostMouseCaptureEvent, new MouseEventHandler((_, _) => EndGestureDeferred()), true);
        InkHost.AddHandler(InkCanvas.StrokeCollectedEvent, new InkCanvasStrokeCollectedEventHandler(OnStrokeCollected));

        InitTools();
        ShowCurrentPage();

        SourceInitialized += OnSourceInitialized;
        KeyDown += OnKeyDown;
        Closed += OnClosed;

        // SystemEvents là event STATIC → bắt buộc hủy đăng ký khi đóng để không leak.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    #region HWND & đa màn hình

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // HWND đã tồn tại nhưng cửa sổ CHƯA hiển thị → đặt style/vị trí ở đây để không "nháy".
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        Win32Helper.AddExStyle(_hwnd, Win32Helper.WS_EX_TOOLWINDOW); // ẩn khỏi Alt+Tab

        CoverAllScreens();
        UpdateInputState();
    }

    /// <summary>
    /// Phủ cửa sổ lên bounding box của TẤT CẢ màn hình. Screen.Bounds là PIXEL VẬT LÝ (nhờ PerMonitorV2);
    /// gọi thẳng SetWindowPos thay vì gán Left/Top/Width/Height (DIP, chỉ đúng với DPI của MỘT màn hình).
    /// </summary>
    public void CoverAllScreens()
    {
        if (_hwnd == IntPtr.Zero) return;

        WinForms.Screen[] screens = WinForms.Screen.AllScreens;
        int left = screens.Min(s => s.Bounds.Left);
        int top = screens.Min(s => s.Bounds.Top);
        int right = screens.Max(s => s.Bounds.Right);
        int bottom = screens.Max(s => s.Bounds.Bottom);

        PhysicalBounds = new Int32Rect(left, top, right - left, bottom - top);

        Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST,
            PhysicalBounds.X, PhysicalBounds.Y, PhysicalBounds.Width, PhysicalBounds.Height,
            Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_NOOWNERZORDER);
    }

    /// <summary>Đưa lại lên trên cùng – phòng app topmost khác (trình chiếu, video) đè lên.</summary>
    public void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(CoverAllScreens, DispatcherPriority.Background);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.WM_DPICHANGED)
        {
            // Không chặn (WPF cần để cập nhật scale), nhưng WPF sẽ resize theo "suggested rect"
            // → để WPF xử lý xong rồi phủ lại desktop ảo bằng pixel vật lý.
            Dispatcher.InvokeAsync(CoverAllScreens, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Vùng hiển thị bảng (DIP, hệ tọa độ cửa sổ) = màn hình đang chứa thanh công cụ.
    /// Cửa sổ dùng MỘT hệ số DPI cho toàn bộ client → đổi pixel vật lý sang DIP bằng hệ số đó là đúng.
    /// </summary>
    private Rect BoardViewport()
    {
        WinForms.Screen screen = AnchorProvider?.Invoke() is { } pt
            ? WinForms.Screen.FromPoint(pt)
            : WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var b = screen.Bounds;
        return new Rect(
            (b.Left - PhysicalBounds.X) / dpi.DpiScaleX,
            (b.Top - PhysicalBounds.Y) / dpi.DpiScaleY,
            b.Width / dpi.DpiScaleX,
            b.Height / dpi.DpiScaleY);
    }

    #endregion

    #region Chế độ & xuyên chuột

    public void SetMode(InteractionMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        CancelToolInteraction();
        UpdateInputState();
        RaiseState();
    }

    public void ToggleMode() =>
        SetMode(_mode == InteractionMode.Mouse ? InteractionMode.Draw : InteractionMode.Mouse);

    /// <summary>
    /// Nguồn sự thật DUY NHẤT cho trạng thái input. Ràng buộc PRD: bảng đang bật ⇒ KHÔNG BAO GIỜ xuyên chuột.
    /// </summary>
    private void UpdateInputState()
    {
        bool clickThrough = _mode == InteractionMode.Mouse && !_boardOn;
        InkCanvas ink = CurrentPage.Ink;

        // 1) Cờ Win32: chuột có tới được cửa sổ hay không.
        Win32Helper.SetClickThrough(_hwnd, clickThrough);

        // 2) Nền InkCanvas: nếu Transparent khi vẽ, layered window cho chuột xuyên qua mọi pixel trống.
        ink.Background = clickThrough ? Brushes.Transparent : HitTestableBrush;

        // 3) Chế độ Chuột trên nền bảng: chặn click nhưng không vẽ.
        ink.EditingMode = _mode == InteractionMode.Draw ? ToEditingMode(_tool) : InkCanvasEditingMode.None;

        // 4) Con trỏ cho công cụ tự xử lý (InkCanvas chỉ dùng Cursor khi UseCustomCursor = true).
        Cursor? cursor = _mode != InteractionMode.Draw ? null : _tool switch
        {
            InkTool.Shape or InkTool.Compass or InkTool.Blur => Cursors.Cross,
            InkTool.Pan => Cursors.SizeAll,
            InkTool.TextSelect => Cursors.IBeam,
            _ => null
        };
        ink.UseCustomCursor = cursor is not null;
        ink.Cursor = cursor;

        OverlayLayer.IsHitTestVisible = !clickThrough;

        if (!clickThrough && IsVisible)
        {
            ReassertTopmost();
            // Nhận phím tắt cục bộ (Esc, Ctrl+Z…). Windows có thể từ chối đưa lên foreground – không sao.
            Activate();
            Focus();
        }
    }

    #endregion

    #region Công cụ & thuộc tính bút

    public void SetTool(InkTool tool)
    {
        if (tool == InkTool.TextSelect && CurrentPage.Words.Count == 0) tool = InkTool.Select;
        if (tool == InkTool.Pan && !_boardOn) return;

        CancelToolInteraction();
        _tool = tool;
        ApplyToolAttributes();
        UpdateInputState();
        RaiseState();
    }

    public void SetShape(ShapeKind shape)
    {
        _shape = shape;
        SetTool(InkTool.Shape);
    }

    public void SetTableSize(int rows, int cols)
    {
        _tableRows = Math.Clamp(rows, 1, 30);
        _tableCols = Math.Clamp(cols, 1, 30);
        SetShape(ShapeKind.Table);
    }

    public (int Rows, int Cols) TableSize => (_tableRows, _tableCols);

    public void SetPenColor(Color color)
    {
        _penColor = color;
        ApplyToolAttributes();
        RaiseState();
    }

    public void SetPenSize(double size)
    {
        _penSize = Math.Clamp(size, 1, 40);
        ApplyToolAttributes();
        RaiseState();
    }

    /// <summary>Áp màu/độ dày cho InkCanvas của trang HIỆN TẠI (mỗi trang một InkCanvas).</summary>
    private void ApplyToolAttributes()
    {
        InkCanvas ink = CurrentPage.Ink;
        bool highlighter = _tool == InkTool.Highlighter;
        double width = highlighter ? Math.Max(12, _penSize * 4) : _penSize;

        ink.DefaultDrawingAttributes = _tool switch
        {
            // Laser: đỏ rực, dày vừa; nét sẽ được thay bằng vệt phát sáng tự mờ (CanvasWindow.Tools).
            InkTool.Laser => new DrawingAttributes
            {
                Color = Color.FromRgb(0xFF, 0x17, 0x44), Width = Math.Max(4, _penSize), Height = Math.Max(4, _penSize),
                FitToCurve = true, IgnorePressure = true
            },
            // Băng che chữ: vệt tối bán trong suốt khi đang quẹt, nhả bút thì thành dải băng.
            InkTool.Tape => new DrawingAttributes
            {
                Color = Color.FromArgb(0xA0, 0x11, 0x13, 0x18), Width = 6, Height = 22,
                StylusTip = StylusTip.Rectangle, FitToCurve = false, IgnorePressure = true
            },
            _ => new DrawingAttributes
            {
                Color = _penColor,
                Width = width,
                Height = width,
                FitToCurve = !highlighter,     // Bút thường: làm mượt Bezier.
                IgnorePressure = highlighter,  // Dạ quang: dày đều; bút thường: theo lực nhấn.
                IsHighlighter = highlighter,   // WPF tự vẽ mờ & đặt nét dạ quang DƯỚI nét thường.
                StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse
            }
        };

        double eraser = Math.Max(16, _penSize * 5);
        ink.EraserShape = new EllipseStylusShape(eraser, eraser);
        UpdateToolPreview();
    }

    /// <summary>Thuộc tính nét cho hình vẽ bằng công cụ (hình, compa, gạch chân…).</summary>
    private DrawingAttributes ShapeAttributes() => new()
    {
        Color = _penColor,
        Width = _penSize,
        Height = _penSize,
        FitToCurve = false,
        IgnorePressure = true
    };

    private static InkCanvasEditingMode ToEditingMode(InkTool tool) => tool switch
    {
        InkTool.Pen or InkTool.Highlighter or InkTool.Laser or InkTool.Tape => InkCanvasEditingMode.Ink,
        InkTool.EraseByPoint => InkCanvasEditingMode.EraseByPoint,
        InkTool.EraseByStroke => InkCanvasEditingMode.EraseByStroke,
        InkTool.Select => InkCanvasEditingMode.Select,
        _ => InkCanvasEditingMode.None // Hình, compa, bàn tay, chọn chữ: tự xử lý chuột.
    };

    #endregion

    #region Lịch sử

    private void BeginGesture() => CurrentPage.History.BeginGroup();

    /// <summary>
    /// Đóng nhóm Undo SAU khi InkCanvas xử lý xong lần nhả bút: PreviewMouseUp chạy TRƯỚC khi InkCanvas
    /// thêm nét (StrokeCollected) → nếu đóng nhóm ngay, nét vừa vẽ (và nét thay thế khi bám thước)
    /// sẽ thành nhiều bước Undo rời rạc.
    /// </summary>
    private void EndGestureDeferred()
    {
        PaperPage page = CurrentPage;
        Dispatcher.InvokeAsync(() => page.History.EndGroup(), DispatcherPriority.Background);
    }

    private void EndGestureNow() => CurrentPage.History.EndGroup();

    public void Undo() => CurrentPage.History.Undo();
    public void Redo() => CurrentPage.History.Redo();

    /// <summary>Xóa nét của trang hiện tại (Undo được). Ảnh chèn giữ nguyên.</summary>
    public void ClearAll() => CurrentPage.Strokes.Clear();

    #endregion

    #region Phím tắt cục bộ (khi canvas đang nhận input)

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Ô nhập liệu (nếu có) tự xử lý phím của nó.
        if (e.OriginalSource is TextBox) return;

        switch (e.Key)
        {
            case Key.Escape when CancelToolInteraction():
                break;
            case Key.Delete or Key.Back when _tool == InkTool.Select && DeleteSelection():
                break;
            case Key.Escape when _mode == InteractionMode.Draw:
                SetMode(InteractionMode.Mouse); break;
            case Key.Z when ctrl && !shift:
                Undo(); break;
            case Key.Y when ctrl:
            case Key.Z when ctrl && shift:
                Redo(); break;
            case Key.OemPlus or Key.Add when ctrl:
                Zoom(ZoomCommand.In); break;
            case Key.OemMinus or Key.Subtract when ctrl:
                Zoom(ZoomCommand.Out); break;
            case Key.D0 or Key.NumPad0 when ctrl:
                Zoom(ZoomCommand.Reset); break;
            case Key.P when !ctrl:
                SetTool(InkTool.Pen); break;
            case Key.H when !ctrl:
                SetTool(InkTool.Highlighter); break;
            case Key.E when !ctrl:
                SetTool(_tool == InkTool.EraseByPoint ? InkTool.EraseByStroke : InkTool.EraseByPoint); break;
            case Key.S when !ctrl:
                SetTool(InkTool.Select); break;
            case Key.R when !ctrl:
                ToggleRuler(); break;
            case Key.L when !ctrl:
                SetTool(InkTool.Laser); break;
            case Key.PageDown when _boardOn:
                NextPage(); break;
            case Key.PageUp when _boardOn:
                PreviousPage(); break;
            default:
                return;
        }
        e.Handled = true;
    }

    #endregion

    public CanvasState GetState() => new(
        _mode, _tool, _shape, _penColor, _penSize, _boardOn,
        _boardOn ? CurrentPage.Paper : PaperType.None,
        _doc.BoardIndex, _doc.BoardPages.Count,
        CurrentPage.History.CanUndo, CurrentPage.History.CanRedo,
        _ruler.Visibility == Visibility.Visible,
        _boardOn ? CurrentPage.Zoom : 1.0,
        _autoScroll,
        CurrentPage.Words.Count > 0);

    private void RaiseState() => CanvasStateChanged?.Invoke(this, GetState());

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hwndSource?.RemoveHook(WndProc);
        StopSmoothScroll();
    }

    public static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color c) return c;
        }
        catch { /* chuỗi màu sai định dạng */ }
        return fallback;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
