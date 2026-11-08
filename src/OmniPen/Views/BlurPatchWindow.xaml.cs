using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OmniPen.Interop;

namespace OmniPen.Views;

/// <summary>
/// Một vùng làm mờ bảo mật: cửa sổ nhỏ nằm đè lên vùng màn hình, liên tục (5 lần/giây) chụp nội dung bên dưới,
/// thu nhỏ 8 lần rồi làm nhòe → mật khẩu, API key, email… không đọc được trên máy chiếu / luồng stream,
/// trong khi ứng dụng bên dưới vẫn chạy và vẫn thao tác được (cửa sổ xuyên chuột).
/// WDA_EXCLUDEFROMCAPTURE: BitBlt nhìn xuyên qua chính vùng mờ để lấy nội dung thật phía dưới.
/// </summary>
public partial class BlurPatchWindow : Window
{
    private IntPtr _hwnd;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Vùng phủ (pixel vật lý).</summary>
    public Int32Rect Region { get; }

    public BlurPatchWindow(Int32Rect region)
    {
        InitializeComponent();
        Region = region;
        _timer.Tick += (_, _) => Refresh();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32Helper.AddExStyle(_hwnd, Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_NOACTIVATE);
            Win32Helper.SetClickThrough(_hwnd, true);
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            Win32Helper.SetWindowPos(_hwnd, Win32Helper.HWND_TOPMOST, region.X, region.Y, region.Width, region.Height,
                Win32Helper.SWP_NOACTIVATE);
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public bool Contains(Point screen) =>
        screen.X >= Region.X && screen.X < Region.X + Region.Width && screen.Y >= Region.Y && screen.Y < Region.Y + Region.Height;

    private void Refresh()
    {
        try
        {
            BitmapSource shot = NativeMethods.CaptureScreen(Region.X, Region.Y, Region.Width, Region.Height);
            // Thu nhỏ 1/8 trước khi làm mờ: vừa nhẹ máy, vừa phá hẳn nét chữ (không "khử mờ" được).
            var small = new TransformedBitmap(shot, new ScaleTransform(0.125, 0.125));
            small.Freeze();
            BlurImage.Source = small;
        }
        catch
        {
            // Màn hình khóa / UAC: giữ khung cũ.
        }
    }
}
