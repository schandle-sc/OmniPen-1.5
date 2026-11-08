using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OmniPen.Interop;

namespace OmniPen.Views;

/// <summary>
/// Chụp vùng chọn: chụp toàn desktop TRƯỚC, hiển thị ảnh đóng băng đó toàn màn hình rồi cho người dùng kéo khung
/// (giống Snipping Tool) → nội dung không đổi trong lúc chọn (video, menu…). Kết quả cắt từ ảnh gốc pixel vật lý.
/// </summary>
public partial class SnipWindow : Window
{
    private readonly BitmapSource _shot;
    private readonly Int32Rect _bounds;
    private Point? _start;
    private Rect _sel;

    /// <summary>Ảnh kết quả (đã cắt) khi DialogResult = true.</summary>
    public BitmapSource? Result { get; private set; }

    public SnipWindow(BitmapSource fullScreenShot, Int32Rect physicalBounds)
    {
        InitializeComponent();
        _shot = fullScreenShot;
        _bounds = physicalBounds;
        Frozen.Source = fullScreenShot;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.SetWindowPos(hwnd, Win32Helper.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0);
        };
        Loaded += (_, _) => { UpdateDim(); Activate(); Focus(); };
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(Root); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_start is not { } s) return;
            _sel = new Rect(s, e.GetPosition(Root));
            UpdateDim();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            if (_start is null) return;
            _start = null;
            if (_sel.Width >= 4 && _sel.Height >= 4) Finish(_sel);
        };
        MouseRightButtonUp += (_, _) => DialogResult = false;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
            else if (e.Key == Key.Enter) Finish(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        };
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        Dim.Data = _sel.IsEmpty || _sel.Width < 1
            ? full
            : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_sel));

        bool has = !_sel.IsEmpty && _sel.Width >= 1;
        SelRect.Visibility = SizeLabel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        System.Windows.Controls.Canvas.SetLeft(SelRect, _sel.X);
        System.Windows.Controls.Canvas.SetTop(SelRect, _sel.Y);
        SelRect.Width = _sel.Width;
        SelRect.Height = _sel.Height;

        Int32Rect px = ToPixels(_sel);
        SizeText.Text = $"{px.Width} × {px.Height}";
        System.Windows.Controls.Canvas.SetLeft(SizeLabel, _sel.X);
        System.Windows.Controls.Canvas.SetTop(SizeLabel, Math.Max(0, _sel.Y - 26));
    }

    /// <summary>DIP trong cửa sổ → pixel của ảnh chụp (ảnh có đúng kích thước pixel của desktop ảo).</summary>
    private Int32Rect ToPixels(Rect r)
    {
        double kx = _shot.PixelWidth / Math.Max(1, Root.ActualWidth), ky = _shot.PixelHeight / Math.Max(1, Root.ActualHeight);
        int x = Math.Clamp((int)Math.Round(r.X * kx), 0, _shot.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(r.Y * ky), 0, _shot.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(r.Width * kx), 1, _shot.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(r.Height * ky), 1, _shot.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    private void Finish(Rect r)
    {
        var crop = new CroppedBitmap(_shot, ToPixels(r));
        crop.Freeze();
        Result = crop;
        DialogResult = true;
    }
}
