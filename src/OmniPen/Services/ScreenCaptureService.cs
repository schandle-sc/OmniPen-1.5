using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OmniPen.Interop;

namespace OmniPen.Services;

/// <summary>Chụp màn hình (toàn bộ hoặc một vùng) kèm nét vẽ đang hiển thị.</summary>
public static class ScreenCaptureService
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OmniPen");

    /// <summary>Chụp vùng pixel vật lý (dùng GDI BitBlt + CAPTUREBLT để có cả lớp vẽ layered).</summary>
    public static BitmapSource Grab(Int32Rect physical) =>
        NativeMethods.CaptureScreen(physical.X, physical.Y, physical.Width, physical.Height);

    /// <summary>Chụp toàn bộ vùng và lưu. Trả về đường dẫn file.</summary>
    public static string Capture(Int32Rect physical) => Save(Grab(physical));

    /// <summary>Lưu PNG vào Ảnh\OmniPen và chép vào Clipboard.</summary>
    public static string Save(BitmapSource image)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"OmniPen_{DateTime.Now:yyyyMMdd_HHmmss}.png");

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using (var fs = File.Create(path)) enc.Save(fs);

        try { Clipboard.SetImage(image); }
        catch { /* Clipboard đang bị app khác giữ – ảnh vẫn đã lưu ra file */ }
        return path;
    }
}
