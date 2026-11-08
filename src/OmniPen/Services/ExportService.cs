using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OmniPen.Ink;

namespace OmniPen.Services;

/// <summary>Xuất trang ra PDF / PNG / SVG.</summary>
public static class ExportService
{
    public const string PdfFilter = "Tài liệu PDF (*.pdf)|*.pdf";
    public const string PngFilter = "Ảnh PNG (*.png)|*.png";
    public const string SvgFilter = "Ảnh vector SVG (*.svg)|*.svg";

    /// <summary>Giới hạn số pixel một ảnh xuất (~48 MP) để trang dài không làm hết RAM.</summary>
    private const double MaxPixels = 48_000_000;

    /// <summary>
    /// Vẽ một trang (nền + ảnh chèn + nét mực) thành bitmap.
    /// Dùng DrawingVisual dựng riêng, không chụp cửa sổ → xuất được cả trang đang không hiển thị
    /// và không bị ảnh hưởng bởi mức zoom hiện tại.
    /// </summary>
    public static async Task<BitmapSource> RenderPageAsync(PaperPage page, Size size, double scale, bool background)
    {
        scale = Math.Min(scale, Math.Sqrt(MaxPixels / Math.Max(1, size.Width * size.Height)));
        var rect = new Rect(size);

        ImageSource? pdfBackground = null;
        if (background && page.Pdf is { } pdf)
            pdfBackground = await pdf.RenderPageAsync(page.PdfPageIndex, (int)Math.Ceiling(size.Width * scale));

        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            if (background)
            {
                if (pdfBackground is not null) dc.DrawImage(pdfBackground, rect);
                else dc.DrawRectangle(page.Paper == PaperType.None ? Brushes.White : PaperBrushes.Get(page.Paper), null, rect);
            }
            foreach (Image img in page.Images)
                if (img.Source is not null) dc.DrawImage(img.Source, ElementRect(img));
            foreach (FrameworkElement tape in page.Tapes)
            {
                Rect r = ElementRect(tape);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23)), null, r, Math.Min(6, r.Height / 3), Math.Min(6, r.Height / 3));
            }
            page.Strokes.Draw(dc);
        }

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(size.Height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static Rect ElementRect(FrameworkElement el)
    {
        double left = InkCanvas.GetLeft(el), top = InkCanvas.GetTop(el);
        double w = double.IsNaN(el.Width) ? el.ActualWidth : el.Width;
        double h = double.IsNaN(el.Height) ? el.ActualHeight : el.Height;
        return new Rect(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top, Math.Max(0, w), Math.Max(0, h));
    }

    // ------------------------------------------------------------------ PNG

    public static async Task ExportPngAsync(string path, PaperPage page, Size size, bool transparent)
    {
        BitmapSource bmp = await RenderPageAsync(page, size, 2.0, background: !transparent);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ------------------------------------------------------------------ PDF

    /// <param name="pages">Các trang cần xuất kèm kích thước (DIP).</param>
    /// <param name="progress">Báo tiến độ (số trang đã xong).</param>
    public static async Task ExportPdfAsync(string path, IReadOnlyList<(PaperPage Page, Size Size)> pages, IProgress<int>? progress = null)
    {
        var writer = new PdfImageWriter();
        for (int i = 0; i < pages.Count; i++)
        {
            var (page, size) = pages[i];
            // ~192 DPI: đủ nét khi in A4, file không quá nặng.
            BitmapSource bmp = await RenderPageAsync(page, size, 2.0, background: true);

            // JPEG không có kênh alpha: trải nền trắng rồi chuyển sang RGB 24 bit.
            var flat = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(flat));
            using var ms = new MemoryStream();
            enc.Save(ms);

            writer.AddPage(ms.ToArray(), flat.PixelWidth, flat.PixelHeight, size.Width * 72 / 96, size.Height * 72 / 96);
            progress?.Report(i + 1);
        }

        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) writer.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------------ SVG

    /// <summary>
    /// SVG thật sự là vector: mỗi nét mực được xuất bằng đường viền chính xác (stroke.GetGeometry)
    /// nên giữ nguyên độ dày thay đổi theo lực nhấn bút. Ảnh chèn và nền PDF nhúng dạng base64.
    /// </summary>
    public static async Task ExportSvgAsync(string path, PaperPage page, Size size, bool background)
    {
        var ci = CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("0.##", ci);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(size.Width)}\" height=\"{N(size.Height)}\" " +
                      $"viewBox=\"0 0 {N(size.Width)} {N(size.Height)}\">");
        sb.AppendLine("  <!-- Tạo bởi OmniPen " + AppInfo.Version + " -->");

        if (background)
        {
            if (page.Pdf is { } pdf)
            {
                BitmapSource bg = await pdf.RenderPageAsync(page.PdfPageIndex, (int)Math.Ceiling(size.Width * 2));
                sb.AppendLine($"  <image x=\"0\" y=\"0\" width=\"{N(size.Width)}\" height=\"{N(size.Height)}\" " +
                              $"preserveAspectRatio=\"none\" href=\"data:image/png;base64,{ToPngBase64(bg)}\"/>");
            }
            else if (page.Paper is PaperType.Lined or PaperType.Grid or PaperType.OLy)
            {
                // Giấy có dòng kẻ: vẽ nền giấy thành ảnh (chỉ nền, không nét) rồi nhúng.
                var empty = new PaperPage(page.Paper);
                BitmapSource bg = await RenderPageAsync(empty, size, 1.0, background: true);
                sb.AppendLine($"  <image x=\"0\" y=\"0\" width=\"{N(size.Width)}\" height=\"{N(size.Height)}\" " +
                              $"preserveAspectRatio=\"none\" href=\"data:image/png;base64,{ToPngBase64(bg)}\"/>");
            }
            else
            {
                Color c = page.Paper == PaperType.None ? Colors.White : PaperBrushes.BaseColor(page.Paper);
                sb.AppendLine($"  <rect width=\"100%\" height=\"100%\" fill=\"{Hex(c)}\"/>");
            }
        }

        foreach (Image img in page.Images)
        {
            Rect r = ElementRect(img);
            string href = img.Tag is ElementData data
                ? $"data:{Mime(data.Bytes)};base64,{Convert.ToBase64String(data.Bytes)}"
                : img.Source is BitmapSource bs ? $"data:image/png;base64,{ToPngBase64(bs)}" : "";
            if (href.Length == 0) continue;
            sb.AppendLine($"  <image x=\"{N(r.X)}\" y=\"{N(r.Y)}\" width=\"{N(r.Width)}\" height=\"{N(r.Height)}\" " +
                          $"preserveAspectRatio=\"none\" href=\"{href}\"/>");
        }

        foreach (FrameworkElement tape in page.Tapes)
        {
            Rect r = ElementRect(tape);
            sb.AppendLine($"  <rect x=\"{N(r.X)}\" y=\"{N(r.Y)}\" width=\"{N(r.Width)}\" height=\"{N(r.Height)}\" rx=\"{N(Math.Min(6, r.Height / 3))}\" fill=\"#1A1D23\"/>");
        }

        // Dạ quang trước (nằm dưới), bút thường sau – đúng thứ tự WPF hiển thị.
        foreach (Stroke s in page.Strokes.OrderBy(s => s.DrawingAttributes.IsHighlighter ? 0 : 1))
        {
            string d = ToSvgPath(s.GetGeometry());
            if (d.Length == 0) continue;
            Color c = s.DrawingAttributes.Color;
            double opacity = (c.A / 255.0) * (s.DrawingAttributes.IsHighlighter ? 0.5 : 1.0);
            sb.Append($"  <path fill=\"{Hex(c)}\"");
            if (opacity < 0.999) sb.Append($" fill-opacity=\"{N(opacity)}\"");
            sb.AppendLine($" fill-rule=\"nonzero\" d=\"{d}\"/>");
        }

        sb.AppendLine("</svg>");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static readonly Regex FillRulePrefix = new(@"^\s*F[01]\s*", RegexOptions.Compiled);

    /// <summary>Geometry WPF → chuỗi lệnh path SVG (cú pháp gần như trùng nhau, chỉ bỏ tiền tố F0/F1).</summary>
    private static string ToSvgPath(Geometry geometry)
    {
        PathGeometry pg = PathGeometry.CreateFromGeometry(geometry);
        string text = pg.ToString(CultureInfo.InvariantCulture);
        return FillRulePrefix.Replace(text, "");
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Mime(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
        : bytes.Length > 3 && bytes[0] == 0x47 && bytes[1] == 0x49 ? "image/gif"
        : bytes.Length > 2 && bytes[0] == 0x42 && bytes[1] == 0x4D ? "image/bmp"
        : "image/png";

    public static byte[] ToPng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static string ToPngBase64(BitmapSource bmp) => Convert.ToBase64String(ToPng(bmp));
}
