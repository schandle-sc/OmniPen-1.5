using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OmniPen.Ink;
using OmniPen.Interop;
using Shapes = System.Windows.Shapes;
using WinForms = System.Windows.Forms;

namespace OmniPen.Views;

public enum ToolbarCommand
{
    SetMode, SetTool, SetShape, SetTable, SetColor, SetSize,
    Undo, Redo, Clear,
    ToggleRuler, ToggleBoard, PreviousPage, NextPage, AddPage, DeletePage, SetPaper,
    Zoom, SetAutoScroll,
    InsertImage, InsertCamera, InsertClipboard, InsertLatex,
    Screenshot, Open, OpenPdf, Save,
    ExportPdf, ExportPng, ExportPngTransparent, ExportSvg,
    OpenCaptures, SetVertical, Settings, About, Donate, Hide, Exit,
    // v1.5
    OpenPlot, CaptureFull, CaptureRegion, ToggleKeys, ToggleMagnifier, ToggleFocus, ClearBlur,
    RevealTapes, Handwriting, PasteCode, Countdown, NamePicker, SetAutoShape
}

public sealed class ToolbarCommandEventArgs(ToolbarCommand command, object? argument = null) : EventArgs
{
    public ToolbarCommand Command { get; } = command;
    public object? Argument { get; } = argument;
}

/// <summary>
/// Thanh công cụ nổi. KHÔNG tham chiếu CanvasWindow: chỉ phát lệnh qua <see cref="CommandRequested"/>
/// và hiển thị lại theo <see cref="Render"/>(CanvasState). App là nơi nối hai phía.
/// </summary>
public partial class ToolbarWindow : Window
{
    public event EventHandler<ToolbarCommandEventArgs>? CommandRequested;

    private static readonly Brush ActiveBrush = Frozen(Color.FromArgb(0x66, 0x4F, 0xC3, 0xF7));
    private IntPtr _hwnd;
    private CanvasState? _state;
    private (int X, int Y)? _initialPosition;
    private bool _rendering;
    private bool _vertical;

    public ToolbarWindow()
    {
        InitializeComponent();
        BuildPaperMenu();
        BuildShapeMenu();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PlaceInitially();
    }

    /// <summary>Vị trí đã lưu (pixel vật lý). Gọi trước Show().</summary>
    public void SetInitialPosition(int? x, int? y)
    {
        if (x is int px && y is int py) _initialPosition = (px, py);
    }

    #region Win32 & vị trí

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        // WS_EX_NOACTIVATE: click toolbar KHÔNG lấy focus khỏi PowerPoint/trình duyệt đang trình chiếu.
        // WS_EX_TOOLWINDOW: ẩn khỏi Alt+Tab.
        Win32Helper.AddExStyle(_hwnd, Win32Helper.WS_EX_NOACTIVATE | Win32Helper.WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    /// <summary>"Hàng rào" PRD: toolbar KHÔNG BAO GIỜ có WS_EX_TRANSPARENT (sửa STYLESTRUCT trong WM_STYLECHANGING).</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.WM_STYLECHANGING && wParam.ToInt32() == Win32Helper.GWL_EXSTYLE)
        {
            var ss = Marshal.PtrToStructure<Win32Helper.STYLESTRUCT>(lParam);
            if ((ss.styleNew & Win32Helper.WS_EX_TRANSPARENT) != 0)
            {
                ss.styleNew &= ~Win32Helper.WS_EX_TRANSPARENT;
                Marshal.StructureToPtr(ss, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private void PlaceInitially()
    {
        UpdateMaxSize();
        if (_initialPosition is { } p && IsOnAnyScreen(p.X, p.Y))
        {
            MoveToPhysical(p.X, p.Y);
            return;
        }
        PlaceDefault();
    }

    /// <summary>Ngang: giữa cạnh trên. Dọc: giữa cạnh trái. (màn hình chính)</summary>
    private void PlaceDefault()
    {
        var wa = (WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0]).WorkingArea;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        UpdateLayout();
        int w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (_vertical) MoveToPhysical(wa.Left + 4, wa.Top + Math.Max(0, (wa.Height - h) / 2));
        else MoveToPhysical(wa.Left + Math.Max(0, (wa.Width - w) / 2), wa.Top + 4);
    }

    /// <summary>WrapPanel tự xuống dòng khi chạm giới hạn này → toolbar không bao giờ tràn khỏi màn hình.</summary>
    private void UpdateMaxSize()
    {
        var screen = _hwnd != IntPtr.Zero ? WinForms.Screen.FromHandle(_hwnd) : WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double wDip = screen.WorkingArea.Width / dpi.DpiScaleX;
        double hDip = screen.WorkingArea.Height / dpi.DpiScaleY;
        MainPanel.MaxWidth = _vertical ? double.PositiveInfinity : wDip * 0.92;
        MainPanel.MaxHeight = _vertical ? hDip * 0.9 : double.PositiveInfinity;
        PagePanel.MaxWidth = _vertical ? double.PositiveInfinity : wDip * 0.92;
        PagePanel.MaxHeight = _vertical ? hDip * 0.9 : double.PositiveInfinity;
    }

    private static bool IsOnAnyScreen(int x, int y) =>
        WinForms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + 40, y + 20));

    private void MoveToPhysical(int x, int y) =>
        Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);

    public (int X, int Y)? GetPhysicalPosition() =>
        _hwnd != IntPtr.Zero && Win32Helper.GetWindowRect(_hwnd, out var r) ? (r.Left, r.Top) : null;

    /// <summary>Tâm toolbar (pixel vật lý) – để canvas biết hiển thị bảng trên màn hình nào.</summary>
    public System.Drawing.Point? GetPhysicalCenter() =>
        _hwnd != IntPtr.Zero && IsVisible && Win32Helper.GetWindowRect(_hwnd, out var r)
            ? new System.Drawing.Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)
            : null;

    #endregion

    #region Ngang / dọc

    /// <summary>Xoay thanh công cụ: các nút xếp theo cột, thanh trang nằm bên phải.</summary>
    public void SetVertical(bool vertical, bool reposition = true)
    {
        _vertical = vertical;
        MenuOrientation.IsChecked = vertical;

        var orient = vertical ? Orientation.Vertical : Orientation.Horizontal;
        MainPanel.Orientation = orient;
        PagePanel.Orientation = orient;
        OuterPanel.Orientation = vertical ? Orientation.Horizontal : Orientation.Vertical;
        PageBar.Margin = vertical ? new Thickness(6, 0, 0, 0) : new Thickness(0, 6, 0, 0);
        PageBar.HorizontalAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center;

        DragHandle.Width = vertical ? 40 : 16;
        DragHandle.Height = vertical ? 16 : 40;
        GripRotate.Angle = vertical ? 0 : 90;

        ColorGrid.Columns = vertical ? 2 : 4;
        SizePanel.Orientation = orient;
        SizeSlider.Orientation = orient;
        SizeSlider.Width = vertical ? double.NaN : 96;
        SizeSlider.Height = vertical ? 96 : double.NaN;
        SizeSlider.IsDirectionReversed = vertical; // dọc: kéo lên = dày hơn
        SizePanel.Margin = vertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0);

        foreach (var sep in FindSeparators(this))
        {
            sep.Width = vertical ? 28 : 1;
            sep.Height = vertical ? 1 : 28;
            sep.Margin = vertical ? new Thickness(0, 6, 0, 6) : new Thickness(6, 0, 6, 0);
        }

        if (IsLoaded)
        {
            UpdateMaxSize();
            if (reposition) Dispatcher.InvokeAsync(PlaceDefault, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static IEnumerable<Shapes.Rectangle> FindSeparators(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Shapes.Rectangle r && Equals(r.Tag, "sep")) yield return r;
            if (child is DependencyObject d)
                foreach (var x in FindSeparators(d)) yield return x;
        }
    }

    #endregion

    #region Render trạng thái

    /// <summary>Vẽ lại toàn bộ trạng thái nút theo canvas – một chiều, toolbar không tự giữ trạng thái riêng.</summary>
    public void Render(CanvasState s)
    {
        _state = s;
        _rendering = true;
        try
        {
            var toolButtons = new[] { BtnMouse, BtnSelect, BtnPen, BtnHighlighter, BtnLaser, BtnTape, BtnEraser, BtnShape, BtnCompass, BtnTextSelect, BtnBlur, BtnPan };
            foreach (var b in toolButtons) b.Background = Brushes.Transparent;

            Button active = s.Mode == InteractionMode.Mouse ? BtnMouse : s.Tool switch
            {
                InkTool.Select => BtnSelect,
                InkTool.Highlighter => BtnHighlighter,
                InkTool.EraseByPoint or InkTool.EraseByStroke => BtnEraser,
                InkTool.Shape => BtnShape,
                InkTool.Compass => BtnCompass,
                InkTool.TextSelect => BtnTextSelect,
                InkTool.Pan => BtnPan,
                InkTool.Laser => BtnLaser,
                InkTool.Tape => BtnTape,
                InkTool.Blur => BtnBlur,
                _ => BtnPen
            };
            active.Background = ActiveBrush;

            BtnEraser.Content = s.Tool == InkTool.EraseByStroke ? "" : "";
            BtnEraser.ToolTip = s.Tool == InkTool.EraseByStroke
                ? "Đang: tẩy cả nét – bấm để chuyển sang tẩy một phần (E)"
                : "Tẩy một phần – bấm lần nữa để tẩy cả nét (E)";
            BtnShape.Content = s.Shape.Glyph();
            BtnRuler.Background = s.RulerVisible ? ActiveBrush : Brushes.Transparent;
            BtnTextSelect.IsEnabled = s.HasPdfText;

            CurrentColorDot.Fill = new SolidColorBrush(s.PenColor);
            SizeSlider.Value = s.PenSize;
            SizeText.Text = s.PenSize.ToString("0");
            SizePreview.Width = SizePreview.Height = Math.Clamp(s.PenSize, 2, 20);
            SizePreview.Fill = new SolidColorBrush(s.PenColor);

            BtnUndo.IsEnabled = s.CanUndo;
            BtnRedo.IsEnabled = s.CanRedo;

            BtnBoard.Background = s.BoardOn ? ActiveBrush : Brushes.Transparent;
            PageBar.Visibility = s.BoardOn ? Visibility.Visible : Visibility.Collapsed;
            PageText.Text = $"{s.PageIndex + 1} / {s.PageCount}";
            BtnPrevPage.IsEnabled = s.PageIndex > 0;
            BtnZoomText.Content = $"{s.Zoom * 100:0}%";
            BtnAutoScroll.Background = s.AutoScroll ? ActiveBrush : Brushes.Transparent;
            PaperText.Text = s.Paper.DisplayName() + (s.Paper == PaperType.Pdf ? "" : " ▾");
            BtnPaper.IsEnabled = s.Paper != PaperType.Pdf;

            foreach (MenuItem item in PaperMenu.Items.OfType<MenuItem>())
                item.IsChecked = item.Tag is PaperType t && t == s.Paper;
        }
        finally
        {
            _rendering = false;
        }
    }

    #endregion

    #region Handlers

    private void Raise(ToolbarCommand cmd, object? arg = null) =>
        CommandRequested?.Invoke(this, new ToolbarCommandEventArgs(cmd, arg));

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void BtnMouse_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.SetMode, InteractionMode.Mouse);
    private void BtnSelect_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Select);
    private void BtnPen_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Pen);
    private void BtnHighlighter_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Highlighter);
    private void BtnCompass_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Compass);
    private void BtnTextSelect_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.TextSelect);
    private void BtnPan_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Pan);
    private void BtnRuler_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ToggleRuler);
    private void BtnLaser_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Laser);
    private void BtnTape_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Tape);
    private void BtnBlur_Click(object sender, RoutedEventArgs e) => SelectTool(InkTool.Blur);
    private void BtnPlot_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.OpenPlot);
    private void BtnCapture_Click(object sender, RoutedEventArgs e) => OpenMenu(CaptureMenu, BtnCapture);
    private void BtnPresent_Click(object sender, RoutedEventArgs e) => OpenMenu(PresentMenu, BtnPresent);
    private void MenuCaptureFull_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.CaptureFull);
    private void MenuCaptureRegion_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.CaptureRegion);
    private void MenuKeys_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ToggleKeys, MenuKeys.IsChecked);
    private void MenuMagnifier_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ToggleMagnifier);
    private void MenuFocus_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ToggleFocus, MenuFocus.IsChecked);
    private void MenuClearBlur_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ClearBlur);
    private void MenuRevealTapes_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.RevealTapes);
    private void MenuHandwriting_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Handwriting);
    private void MenuPasteCode_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.PasteCode);
    private void MenuAutoShape_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.SetAutoShape, MenuAutoShape.IsChecked);
    private void MenuCountdown_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Countdown);
    private void MenuNamePicker_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.NamePicker);

    /// <summary>Đồng bộ dấu tích các tính năng trình chiếu (bật/tắt từ phím tắt hoặc khay hệ thống).</summary>
    public void SetPresenterState(bool keys, bool magnifier, bool focus, bool autoShape)
    {
        MenuKeys.IsChecked = keys;
        MenuMagnifier.IsChecked = magnifier;
        MenuFocus.IsChecked = focus;
        MenuAutoShape.IsChecked = autoShape;
    }

    private void BtnEraser_Click(object sender, RoutedEventArgs e)
    {
        bool drawing = _state?.Mode == InteractionMode.Draw;
        InkTool next = drawing && _state?.Tool == InkTool.EraseByPoint ? InkTool.EraseByStroke : InkTool.EraseByPoint;
        SelectTool(next);
    }

    /// <summary>Chọn công cụ ⇒ tự vào chế độ Vẽ.</summary>
    private void SelectTool(InkTool tool)
    {
        Raise(ToolbarCommand.SetTool, tool);
        Raise(ToolbarCommand.SetMode, InteractionMode.Draw);
    }

    private void BtnShape_Click(object sender, RoutedEventArgs e) => OpenMenu(ShapeMenu, BtnShape);

    private void BuildShapeMenu()
    {
        foreach (ShapeKind k in Enum.GetValues<ShapeKind>())
        {
            if (k == ShapeKind.Table) continue;
            var item = new MenuItem { Header = k.DisplayName(), Icon = Glyph(k.Glyph()) };
            ShapeKind kind = k;
            item.Click += (_, _) =>
            {
                Raise(ToolbarCommand.SetShape, kind);
                Raise(ToolbarCommand.SetMode, InteractionMode.Draw);
            };
            ShapeMenu.Items.Add(item);
        }

        var table = new MenuItem { Header = ShapeKind.Table.DisplayName(), Icon = Glyph(ShapeKind.Table.Glyph()) };
        foreach (var (r, c) in new[] { (2, 2), (2, 3), (3, 3), (3, 4), (4, 4), (4, 5), (5, 5), (6, 4), (8, 3) })
        {
            var sub = new MenuItem { Header = $"{r} hàng × {c} cột" };
            int rows = r, cols = c;
            sub.Click += (_, _) =>
            {
                Raise(ToolbarCommand.SetTable, (rows, cols));
                Raise(ToolbarCommand.SetMode, InteractionMode.Draw);
            };
            table.Items.Add(sub);
        }
        ShapeMenu.Items.Add(new Separator());
        ShapeMenu.Items.Add(table);
    }

    private static TextBlock Glyph(string g) =>
        new() { Text = g, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 14, TextAlignment = TextAlignment.Center };

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Background: SolidColorBrush brush }) ApplyColor(brush.Color);
    }

    private void ApplyColor(Color c)
    {
        Raise(ToolbarCommand.SetColor, c);
        // Chọn màu khi đang cầm tẩy / chuột / chọn ⇒ cầm bút.
        if (_state is null || _state.Mode == InteractionMode.Mouse ||
            _state.Tool is InkTool.EraseByPoint or InkTool.EraseByStroke or InkTool.Select or InkTool.Pan or InkTool.TextSelect)
            SelectTool(InkTool.Pen);
    }

    private void BtnPalette_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.ColorDialog { FullOpen = true, AnyColor = true };
        if (_state is { } s)
            dlg.Color = System.Drawing.Color.FromArgb(s.PenColor.A, s.PenColor.R, s.PenColor.G, s.PenColor.B);

        // Owner = toolbar ⇒ hộp thoại nằm trên toolbar, toolbar nằm trên canvas → không bị lớp vẽ che.
        if (dlg.ShowDialog(new Win32Owner(_hwnd)) == WinForms.DialogResult.OK)
            ApplyColor(Color.FromArgb(255, dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _rendering) return;
        SizeText.Text = e.NewValue.ToString("0");
        Raise(ToolbarCommand.SetSize, Math.Round(e.NewValue));
    }

    private void SizeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SizeSlider.Value = Math.Clamp(SizeSlider.Value + (e.Delta > 0 ? 1 : -1), SizeSlider.Minimum, SizeSlider.Maximum);
        e.Handled = true;
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Undo);
    private void BtnRedo_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Redo);
    private void BtnClear_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Clear);
    private void BtnBoard_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ToggleBoard);
    private void BtnHide_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Hide);

    private void BtnPrevPage_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.PreviousPage);
    private void BtnNextPage_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.NextPage);
    private void BtnAddPage_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.AddPage);

    private void BtnDeletePage_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "Xóa trang bảng hiện tại?", "OmniPen",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r == MessageBoxResult.Yes) Raise(ToolbarCommand.DeletePage);
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Zoom, ZoomCommand.Out);
    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Zoom, ZoomCommand.In);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Zoom, ZoomCommand.Reset);
    private void BtnFitWidth_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Zoom, ZoomCommand.FitWidth);
    private void BtnFitPage_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Zoom, ZoomCommand.FitPage);
    private void BtnAutoScroll_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.SetAutoScroll, !(_state?.AutoScroll ?? true));

    private void BtnInsert_Click(object sender, RoutedEventArgs e) => OpenMenu(InsertMenu, BtnInsert);
    private void BtnMore_Click(object sender, RoutedEventArgs e) => OpenMenu(MoreMenu, BtnMore);
    private void BtnPaper_Click(object sender, RoutedEventArgs e) => OpenMenu(PaperMenu, BtnPaper);

    private void OpenMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = _vertical ? PlacementMode.Right : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BuildPaperMenu()
    {
        foreach (PaperType t in PaperTypeExtensions.Selectable())
        {
            var item = new MenuItem { Header = t.DisplayName(), Tag = t };
            PaperType paper = t;
            item.Click += (_, _) => Raise(ToolbarCommand.SetPaper, paper);
            PaperMenu.Items.Add(item);
        }
    }

    private void MenuInsertImage_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.InsertImage);
    private void MenuInsertCamera_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.InsertCamera);
    private void MenuInsertClipboard_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.InsertClipboard);
    private void MenuInsertLatex_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.InsertLatex);

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Open);
    private void MenuOpenPdf_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.OpenPdf);
    private void MenuSave_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Save);
    private void MenuExportPdf_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ExportPdf);
    private void MenuExportPng_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ExportPng);
    private void MenuExportPngTransparent_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ExportPngTransparent);
    private void MenuExportSvg_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.ExportSvg);
    private void MenuScreenshot_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Screenshot);
    private void MenuOpenCaptures_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.OpenCaptures);
    private void MenuOrientation_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.SetVertical, MenuOrientation.IsChecked);
    private void MenuSettings_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Settings);
    private void MenuAbout_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.About);
    private void MenuExit_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Exit);
    private void MenuDonate_Click(object sender, RoutedEventArgs e) => Raise(ToolbarCommand.Donate);

    #endregion

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Bọc HWND thành IWin32Window để làm owner cho hộp thoại WinForms (ColorDialog).</summary>
public sealed class Win32Owner(IntPtr handle) : WinForms.IWin32Window
{
    public IntPtr Handle { get; } = handle;
}
