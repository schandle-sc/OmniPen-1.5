using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OmniPen.Ink;

/// <summary>Tạo phần tử ảnh (ảnh chèn, ảnh camera, công thức LaTeX) đặt trong InkCanvas của trang.</summary>
public static class PageElements
{
    public static BitmapSource Decode(byte[] bytes)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static Image Create(byte[] bytes, Rect bounds, string kind = "image", string? latex = null)
    {
        var img = new Image
        {
            Source = Decode(bytes),
            Width = bounds.Width,
            Height = bounds.Height,
            Stretch = Stretch.Fill,
            Tag = new ElementData { Bytes = bytes, Kind = kind, Latex = latex },
            ToolTip = kind == "latex" ? latex : null
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        InkCanvas.SetLeft(img, bounds.X);
        InkCanvas.SetTop(img, bounds.Y);
        return img;
    }

    /// <summary>
    /// Băng che chữ (Cloze tape): dải băng tối che kín một từ. Bấm vào để băng "bốc hơi" lộ đáp án.
    /// Là UIElement trong InkCanvas nên di chuyển/lưu/hoàn tác như ảnh chèn.
    /// </summary>
    public static Border CreateTape(Rect bounds)
    {
        var tape = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            CornerRadius = new CornerRadius(Math.Min(6, bounds.Height / 3)),
            Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x2F, 0x36), Color.FromRgb(0x11, 0x13, 0x18), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Tag = new ElementData { Bytes = Array.Empty<byte>(), Kind = "tape" },
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Bấm để mở đáp án",
            Child = new TextBlock
            {
                Text = "?",
                Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                FontWeight = FontWeights.Bold,
                FontSize = Math.Clamp(bounds.Height * 0.55, 10, 40),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        InkCanvas.SetLeft(tape, bounds.X);
        InkCanvas.SetTop(tape, bounds.Y);
        return tape;
    }

    public static bool IsTape(object? o) => o is FrameworkElement { Tag: ElementData { Kind: "tape" } };

    /// <summary>Kích thước hiển thị hợp lý của ảnh: theo DPI gốc, thu nhỏ để vừa trong <paramref name="max"/>.</summary>
    public static Size FitSize(BitmapSource bmp, Size max, double extraScale = 1.0)
    {
        double w = bmp.PixelWidth * 96.0 / (bmp.DpiX > 1 ? bmp.DpiX : 96) * extraScale;
        double h = bmp.PixelHeight * 96.0 / (bmp.DpiY > 1 ? bmp.DpiY : 96) * extraScale;
        double k = Math.Min(1, Math.Min(max.Width / Math.Max(1, w), max.Height / Math.Max(1, h)));
        return new Size(Math.Max(8, w * k), Math.Max(8, h * k));
    }
}
