using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OmniPen.Ink;
using OmniPen.Services;

namespace OmniPen.Views;

/// <summary>Trang, chế độ Bảng, phóng to/thu nhỏ, di chuyển, tự cuộn, PDF, chèn ảnh, lưu/mở.</summary>
public partial class CanvasWindow
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 6.0;
    private const int PdfCacheSize = 6;

    private readonly InkDocument _doc;
    private PaperPage? _shownPage;
    private InkHistory? _attachedHistory;
    private bool _boardOn;
    private bool _autoScroll;

    // Cuộn mượt
    private double _scrollRemaining;
    private bool _scrollAnimating;

    // Cache ảnh nền PDF: chỉ giữ vài trang gần nhất
    private readonly LinkedList<PaperPage> _pdfCache = new();
    private readonly Dictionary<PaperPage, int> _pdfRenderedWidth = new();
    private readonly DispatcherTimer _pdfRerender = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public bool IsBoardOn => _boardOn;
    public InkDocument Document => _doc;

    private PaperPage CurrentPage => _boardOn ? _doc.CurrentBoardPage : _doc.Annotation;

    #region Bật/tắt bảng, chuyển trang

    public void SetBoard(bool on)
    {
        if (_boardOn == on) return;
        EndGestureNow();
        _boardOn = on;
        if (on) _mode = InteractionMode.Draw; // Mở bảng là để viết.
        if (!on && _tool is InkTool.Pan or InkTool.TextSelect) _tool = InkTool.Pen;
        ShowCurrentPage();
    }

    public void ToggleBoard() => SetBoard(!_boardOn);

    public void NextPage()
    {
        if (!_boardOn) return;
        EndGestureNow();
        if (_doc.BoardIndex == _doc.BoardPages.Count - 1) _doc.AddPageAfterCurrent(); // Trang cuối → tạo trang mới.
        else _doc.GoTo(_doc.BoardIndex + 1);
        ShowCurrentPage();
    }

    public void PreviousPage()
    {
        if (!_boardOn) return;
        EndGestureNow();
        if (_doc.GoTo(_doc.BoardIndex - 1)) ShowCurrentPage();
    }

    public void AddPage()
    {
        if (!_boardOn) return;
        EndGestureNow();
        _doc.AddPageAfterCurrent();
        ShowCurrentPage();
    }

    public void DeletePage()
    {
        if (!_boardOn) return;
        EndGestureNow();
        _doc.DeleteCurrentPage();
        ShowCurrentPage();
    }

    public void SetPaper(PaperType paper)
    {
        if (!_boardOn) SetBoard(true);
        if (CurrentPage.IsPdf) return; // Nền PDF không đổi được.
        _doc.CurrentBoardPage.Paper = InkDocument.Normalize(paper);
        ShowCurrentPage();
    }

    public void SetAutoScroll(bool on)
    {
        _autoScroll = on;
        RaiseState();
    }

    /// <summary>
    /// Hiển thị trang hiện tại: chỉ THAY InkCanvas trong khung (mỗi trang một InkCanvas)
    /// → nhanh, không sinh sự kiện StrokesChanged, không làm bẩn lịch sử.
    /// </summary>
    private void ShowCurrentPage()
    {
        StopSmoothScroll();
        CancelToolInteraction();

        if (_attachedHistory is not null) _attachedHistory.StateChanged -= OnHistoryStateChanged;
        if (_shownPage is not null && _shownPage != CurrentPage)
        {
            // Trang cũ: bỏ vùng chọn, tắt nhận input.
            _shownPage.Ink.Select(new System.Windows.Ink.StrokeCollection());
            _shownPage.Ink.EditingMode = InkCanvasEditingMode.None;
        }

        PaperPage page = CurrentPage;
        _shownPage = page;
        WireSelectionUndo(page);
        InkHost.Child = page.Ink; // Border.Child tự gỡ InkCanvas cũ ra khỏi cây hiển thị.
        _attachedHistory = page.History;
        _attachedHistory.StateChanged += OnHistoryStateChanged;

        if (_boardOn)
        {
            EnsurePageInitialized(page);
            Surface.HorizontalAlignment = HorizontalAlignment.Left;
            Surface.VerticalAlignment = VerticalAlignment.Top;
            Surface.Width = page.Width;
            Surface.Height = page.Height;
            DeskBackground.Visibility = Visibility.Visible;
            PaperBackground.Visibility = Visibility.Visible;

            if (page.IsPdf)
            {
                PaperBackground.Background = page.BackgroundImage is { } img
                    ? new ImageBrush(img) { Stretch = Stretch.Fill }
                    : Brushes.White;
                _ = RenderPdfBackgroundAsync(page);
            }
            else
            {
                PaperBackground.Background = PaperBrushes.Get(page.Paper);
            }
        }
        else
        {
            Surface.HorizontalAlignment = HorizontalAlignment.Stretch;
            Surface.VerticalAlignment = VerticalAlignment.Stretch;
            Surface.Width = double.NaN;
            Surface.Height = double.NaN;
            DeskBackground.Visibility = Visibility.Collapsed;
            PaperBackground.Visibility = Visibility.Collapsed;
        }

        if (_tool == InkTool.TextSelect && page.Words.Count == 0) _tool = InkTool.Pen;

        ApplyView();
        AdjustPenColorForPaper();
        ApplyToolAttributes();
        UpdateInputState();
        RaiseState();
    }

    /// <summary>Trang mới: khổ = màn hình hiển thị bảng; góc nhìn mặc định = vừa chiều rộng.</summary>
    private void EnsurePageInitialized(PaperPage page)
    {
        Rect vp = BoardViewport();
        if (page.Width <= 0 || page.Height <= 0)
        {
            page.Width = vp.Width;
            page.Height = vp.Height;
        }
        if (!page.ViewInitialized)
        {
            page.View = FitWidthView(page, vp);
            page.ViewInitialized = true;
        }
    }

    private static Matrix FitWidthView(PaperPage page, Rect vp)
    {
        double zoom = page.IsPdf
            ? Math.Clamp(vp.Width * 0.92 / page.Width, MinZoom, 3)
            : Math.Clamp(vp.Width / page.Width, MinZoom, MaxZoom);
        double x = vp.X + (vp.Width - page.Width * zoom) / 2;
        double y = vp.Y + (page.IsPdf ? 16 : 0);
        return new Matrix(zoom, 0, 0, zoom, x, y);
    }

    private static Matrix FitPageView(PaperPage page, Rect vp)
    {
        double zoom = Math.Clamp(Math.Min(vp.Width / page.Width, vp.Height / page.Height) * 0.95, MinZoom, MaxZoom);
        double x = vp.X + (vp.Width - page.Width * zoom) / 2;
        double y = vp.Y + (vp.Height - page.Height * zoom) / 2;
        return new Matrix(zoom, 0, 0, zoom, x, y);
    }

    /// <summary>Bảng tối + bút tối ⇒ đổi sang trắng; giấy sáng + bút trắng ⇒ đổi sang đen.</summary>
    private void AdjustPenColorForPaper()
    {
        if (!_boardOn) return;
        double lum = (0.299 * _penColor.R + 0.587 * _penColor.G + 0.114 * _penColor.B) / 255.0;
        PaperType paper = CurrentPage.Paper;

        if (paper.IsDark() && lum < 0.3) _penColor = Colors.White;
        else if (!paper.IsDark() && lum > 0.92) _penColor = Colors.Black;
    }

    private void OnHistoryStateChanged(object? sender, EventArgs e) => RaiseState();

    #endregion

    #region Phóng to / thu nhỏ / di chuyển

    /// <summary>Áp góc nhìn của trang cho khung hiển thị (chỉ khi bật bảng; chú thích màn hình luôn 1:1).</summary>
    private void ApplyView()
    {
        Surface.RenderTransform = _boardOn ? new MatrixTransform(CurrentPage.View) : Transform.Identity;
    }

    public void Zoom(ZoomCommand cmd)
    {
        if (!_boardOn) return;
        PaperPage page = CurrentPage;
        Rect vp = BoardViewport();
        Point center = new(vp.X + vp.Width / 2, vp.Y + vp.Height / 2);

        switch (cmd)
        {
            case ZoomCommand.In: ZoomAt(1.25, center); return;
            case ZoomCommand.Out: ZoomAt(1 / 1.25, center); return;
            case ZoomCommand.Reset: ZoomAt(1.0 / page.Zoom, center); return;
            case ZoomCommand.FitWidth: page.View = FitWidthView(page, vp); break;
            case ZoomCommand.FitPage: page.View = FitPageView(page, vp); break;
        }
        AfterViewChanged(zoomChanged: true);
    }

    /// <summary>Phóng quanh một điểm trên màn hình (điểm đó đứng yên – như Ctrl+cuộn chuột trong trình duyệt).</summary>
    private void ZoomAt(double factor, Point screenPoint)
    {
        PaperPage page = CurrentPage;
        double z = page.Zoom;
        double nz = Math.Clamp(z * factor, MinZoom, MaxZoom);
        if (Math.Abs(nz - z) < 1e-6) return;

        Matrix m = page.View;
        m.ScaleAt(nz / z, nz / z, screenPoint.X, screenPoint.Y); // ScaleAt "nối thêm" phép co giãn trong hệ màn hình.
        page.View = m;
        AfterViewChanged(zoomChanged: true);
    }

    /// <summary>Dịch trang theo pixel màn hình.</summary>
    private void PanBy(double dx, double dy)
    {
        if (!_boardOn) return;
        PaperPage page = CurrentPage;
        Matrix m = page.View;
        m.Translate(dx, dy);
        page.View = m;
        AfterViewChanged(zoomChanged: false);
    }

    private void AfterViewChanged(bool zoomChanged)
    {
        ExtendBlankPageToViewport();
        ApplyView();
        if (zoomChanged)
        {
            RaiseState();
            if (CurrentPage.IsPdf) { _pdfRerender.Stop(); _pdfRerender.Start(); } // render lại nét hơn sau khi dừng zoom
        }
    }

    /// <summary>
    /// "Mở rộng trang giấy": trang trắng tự dài thêm khi người dùng cuộn/viết xuống dưới đáy,
    /// như một cuộn giấy vô tận. Trang PDF giữ nguyên khổ.
    /// </summary>
    private void ExtendBlankPageToViewport()
    {
        PaperPage page = CurrentPage;
        if (!_boardOn || page.IsPdf) return;
        Rect vp = BoardViewport();
        double visibleBottom = (vp.Bottom - page.View.OffsetY) / page.Zoom;
        if (visibleBottom > page.Height)
        {
            page.Height = visibleBottom + vp.Height / page.Zoom * 0.5;
            Surface.Height = page.Height;
        }
    }

    /// <summary>
    /// Tự cuộn: nét vừa viết nằm dưới 72% chiều cao màn hình ⇒ trang trôi lên để dòng viết về khoảng 40%,
    /// người dùng viết tiếp mà không phải dừng tay kéo trang.
    /// </summary>
    private void AutoScrollAfterStroke(Rect strokeBounds)
    {
        if (!_boardOn || !_autoScroll || strokeBounds.IsEmpty) return;
        PaperPage page = CurrentPage;
        Rect vp = BoardViewport();

        double bottom = page.View.Transform(new Point(0, strokeBounds.Bottom)).Y;
        if (bottom <= vp.Top + vp.Height * 0.72) return;

        double dy = vp.Top + vp.Height * 0.40 - bottom; // âm = trang đi lên
        if (page.IsPdf)
        {
            // Không cuộn quá đáy trang PDF.
            double pageBottom = page.View.Transform(new Point(0, page.Height)).Y;
            dy = Math.Max(dy, Math.Min(0, vp.Bottom - 40 - pageBottom));
            if (dy > -1) return;
        }
        SmoothScroll(dy);
    }

    private void SmoothScroll(double dy)
    {
        _scrollRemaining += dy;
        if (_scrollAnimating) return;
        _scrollAnimating = true;
        CompositionTarget.Rendering += OnSmoothScrollFrame;
    }

    private void OnSmoothScrollFrame(object? sender, EventArgs e)
    {
        // Mỗi khung hình đi 18% quãng còn lại → chậm dần đều (ease-out), ~0.4 giây.
        double step = Math.Abs(_scrollRemaining) < 0.75 ? _scrollRemaining : _scrollRemaining * 0.18;
        _scrollRemaining -= step;
        PanBy(0, step);
        if (_scrollRemaining == 0) StopSmoothScroll();
    }

    private void StopSmoothScroll()
    {
        if (!_scrollAnimating) { _scrollRemaining = 0; return; }
        CompositionTarget.Rendering -= OnSmoothScrollFrame;
        _scrollAnimating = false;
        _scrollRemaining = 0;
    }

    /// <summary>Vùng trang đang nhìn thấy (hệ tọa độ trang).</summary>
    private Rect VisiblePageRect()
    {
        Rect vp = BoardViewport();
        if (!_boardOn) return vp;
        Matrix inv = CurrentPage.View;
        if (!inv.HasInverse) return vp;
        inv.Invert();
        return Rect.Transform(vp, inv);
    }

    #endregion

    #region PDF

    /// <summary>Mở PDF: mỗi trang PDF thành một trang bảng (nền là trang PDF), bật chế độ Bảng.</summary>
    public async Task OpenPdfAsync(string path)
    {
        // Chép sang thư mục tạm: file gốc có thể bị di chuyển/xóa trong lúc dạy, và để lưu kèm vào .omnipen.
        string dir = Path.Combine(Path.GetTempPath(), "OmniPen", "pdf");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, $"{Guid.NewGuid():N}_{Path.GetFileName(path)}");
        File.Copy(path, copy, overwrite: true);

        PdfSource src = await PdfSource.OpenAsync(copy);
        var pages = new List<PaperPage>();
        for (int i = 0; i < src.PageCount; i++)
        {
            Size size = src.PageSize(i);
            pages.Add(new PaperPage(PaperType.Pdf)
            {
                Pdf = src,
                PdfPageIndex = i,
                Width = size.Width,
                Height = size.Height,
                Words = src.Words(i)
            });
        }

        EndGestureNow();
        _pdfCache.Clear();
        _pdfRenderedWidth.Clear();
        _doc.ReplaceBoardPages(pages);
        _boardOn = true;
        _mode = InteractionMode.Draw;
        ShowCurrentPage();
    }

    /// <summary>Render nền PDF theo mức zoom hiện tại (nét khi phóng to), có giới hạn để không tốn RAM.</summary>
    private async Task RenderPdfBackgroundAsync(PaperPage page)
    {
        if (page.Pdf is null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int wanted = (int)Math.Min(5000, page.Width * Math.Max(1.5, page.Zoom) * dpi);
        if (_pdfRenderedWidth.TryGetValue(page, out int have) && have >= wanted * 0.9 && page.BackgroundImage is not null)
            return;

        try
        {
            var img = await page.Pdf.RenderPageAsync(page.PdfPageIndex, wanted);
            page.BackgroundImage = img;
            _pdfRenderedWidth[page] = wanted;
            TouchPdfCache(page);
            if (_boardOn && CurrentPage == page)
                PaperBackground.Background = new ImageBrush(img) { Stretch = Stretch.Fill };
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private void TouchPdfCache(PaperPage page)
    {
        _pdfCache.Remove(page);
        _pdfCache.AddFirst(page);
        while (_pdfCache.Count > PdfCacheSize && _pdfCache.Last is { } last)
        {
            last.Value.BackgroundImage = null;
            _pdfRenderedWidth.Remove(last.Value);
            _pdfCache.RemoveLast();
        }
    }

    #endregion

    #region Chèn ảnh

    /// <summary>
    /// Chèn ảnh (từ máy, camera, công thức LaTeX) vào giữa vùng đang nhìn, chọn sẵn để kéo/đổi cỡ ngay.
    /// Chèn được Undo (bước tùy biến trong InkHistory vì ảnh không phải nét mực).
    /// </summary>
    public void InsertImage(byte[] bytes, string kind = "image", string? latex = null, Size? preferredSize = null)
    {
        EndGestureNow();
        var bmp = PageElements.Decode(bytes);
        Rect view = VisiblePageRect();
        Size size = preferredSize ?? PageElements.FitSize(bmp, new Size(view.Width * 0.6, view.Height * 0.6));

        var rect = new Rect(
            view.X + (view.Width - size.Width) / 2,
            view.Y + (view.Height - size.Height) / 2,
            size.Width, size.Height);
        Image img = PageElements.Create(bytes, rect, kind, latex);

        PaperPage page = CurrentPage;
        InkCanvas ink = page.Ink;
        ink.Children.Add(img);
        page.History.PushCustom(() => ink.Children.Remove(img), () => ink.Children.Add(img));

        _mode = InteractionMode.Draw;
        _tool = InkTool.Select;
        ApplyToolAttributes();
        UpdateInputState();
        // Chọn sau khi ảnh đã được đo/bố trí, nếu không khung chọn có thể hiện sai kích thước.
        Dispatcher.InvokeAsync(() => ink.Select(new UIElement[] { img }), DispatcherPriority.Loaded);
        RaiseState();
    }

    #endregion

    #region Vùng chọn: xóa, hoàn tác di chuyển/đổi cỡ

    private readonly HashSet<InkCanvas> _wiredInks = new();
    private Dictionary<System.Windows.Ink.Stroke, System.Windows.Input.StylusPointCollection>? _selStrokesBefore;
    private Dictionary<UIElement, Rect>? _selElementsBefore;

    /// <summary>
    /// InkCanvas biến đổi nét được chọn TẠI CHỖ (đổi StylusPoints) nên StrokesChanged không phát ra
    /// → tự chụp trạng thái trước/sau khi kéo/đổi cỡ và ghi một bước Undo tùy biến.
    /// </summary>
    private void WireSelectionUndo(PaperPage page)
    {
        InkCanvas ink = page.Ink;
        if (!_wiredInks.Add(ink)) return;

        void Before(object? s, InkCanvasSelectionEditingEventArgs e)
        {
            _selStrokesBefore = ink.GetSelectedStrokes().ToDictionary(st => st, st => st.StylusPoints.Clone());
            _selElementsBefore = ink.GetSelectedElements().ToDictionary(el => el, el => ExportService.ElementRect((FrameworkElement)el));
        }
        void After(object? s, EventArgs e)
        {
            if (_selStrokesBefore is null || _selElementsBefore is null) return;
            var strokesBefore = _selStrokesBefore;
            var elementsBefore = _selElementsBefore;
            var strokesAfter = strokesBefore.Keys.ToDictionary(st => st, st => st.StylusPoints.Clone());
            var elementsAfter = elementsBefore.Keys.ToDictionary(el => el, el => ExportService.ElementRect((FrameworkElement)el));
            _selStrokesBefore = null;
            _selElementsBefore = null;

            void Apply(Dictionary<System.Windows.Ink.Stroke, System.Windows.Input.StylusPointCollection> st, Dictionary<UIElement, Rect> el)
            {
                ink.Select(new System.Windows.Ink.StrokeCollection()); // bỏ khung chọn cũ (sai vị trí sau khi đảo)
                foreach (var (stroke, pts) in st) stroke.StylusPoints = pts.Clone();
                foreach (var (element, r) in el)
                {
                    InkCanvas.SetLeft(element, r.X);
                    InkCanvas.SetTop(element, r.Y);
                    if (element is FrameworkElement fe) { fe.Width = r.Width; fe.Height = r.Height; }
                }
            }
            page.History.PushCustom(() => Apply(strokesBefore, elementsBefore), () => Apply(strokesAfter, elementsAfter));
        }

        ink.SelectionMoving += Before;
        ink.SelectionResizing += Before;
        ink.SelectionMoved += After;
        ink.SelectionResized += After;
    }

    /// <summary>Xóa nét và ảnh đang chọn (phím Delete). Nét xóa ghi Undo tự động, ảnh ghi Undo tùy biến.</summary>
    private bool DeleteSelection()
    {
        PaperPage page = CurrentPage;
        InkCanvas ink = page.Ink;
        var strokes = ink.GetSelectedStrokes();
        var elements = ink.GetSelectedElements().ToList();
        if (strokes.Count == 0 && elements.Count == 0) return false;

        ink.Select(new System.Windows.Ink.StrokeCollection());
        if (strokes.Count > 0)
        {
            page.History.BeginGroup();
            ink.Strokes.Remove(strokes);
            page.History.EndGroup();
        }
        if (elements.Count > 0)
        {
            foreach (UIElement el in elements) ink.Children.Remove(el);
            page.History.PushCustom(
                () => { foreach (UIElement el in elements) ink.Children.Add(el); },
                () => { foreach (UIElement el in elements) ink.Children.Remove(el); });
        }
        return true;
    }

    #endregion

    #region Lưu / mở / xuất

    public void SaveDocument(string path)
    {
        EndGestureNow();
        InkFileService.Save(path, _doc);
    }

    public async Task LoadDocumentAsync(string path)
    {
        var (annotation, pages) = await InkFileService.LoadAsync(path);
        EndGestureNow();
        _pdfCache.Clear();
        _pdfRenderedWidth.Clear();
        _doc.ReplaceAnnotation(annotation);
        _doc.ReplaceBoardPages(pages);
        ShowCurrentPage();
    }

    /// <summary>Khổ trang để xuất file (DIP).</summary>
    public Size PageSize(PaperPage page)
    {
        if (page == _doc.Annotation) return new Size(RootGrid.ActualWidth, RootGrid.ActualHeight);
        if (page.Width > 0 && page.Height > 0) return new Size(page.Width, page.Height);
        Rect vp = BoardViewport();
        return new Size(vp.Width, vp.Height);
    }

    /// <summary>Trang đang hiển thị (để xuất PNG/SVG).</summary>
    public PaperPage ExportCurrentPage => CurrentPage;

    /// <summary>Các trang để xuất PDF: đang bật bảng → mọi trang bảng; đang chú thích màn hình → trang chú thích.</summary>
    public IReadOnlyList<(PaperPage Page, Size Size)> PagesForPdf()
    {
        var list = new List<(PaperPage Page, Size Size)>();
        if (_boardOn)
            foreach (PaperPage p in _doc.BoardPages) list.Add((p, PageSize(p)));
        else
            list.Add((_doc.Annotation, PageSize(_doc.Annotation)));
        return list;
    }

    #endregion
}
