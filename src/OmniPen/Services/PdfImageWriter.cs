using System.Globalization;
using System.IO;
using System.Text;

namespace OmniPen.Services;

/// <summary>
/// Trình ghi PDF tối giản: mỗi trang là MỘT ảnh JPEG phủ kín trang.
/// Không cần thư viện ngoài – JPEG được nhúng nguyên (bộ lọc DCTDecode), PDF reader tự giải mã.
/// Lớp này cố ý không phụ thuộc WPF để kiểm thử độc lập.
/// </summary>
public sealed class PdfImageWriter
{
    private sealed record PageImage(byte[] Jpeg, int PixelWidth, int PixelHeight, double WidthPt, double HeightPt);

    private readonly List<PageImage> _pages = new();

    /// <param name="jpeg">Dữ liệu JPEG (RGB, 8 bit).</param>
    /// <param name="widthPt">Khổ trang theo điểm PDF (1/72 inch).</param>
    public void AddPage(byte[] jpeg, int pixelWidth, int pixelHeight, double widthPt, double heightPt) =>
        _pages.Add(new PageImage(jpeg, pixelWidth, pixelHeight, widthPt, heightPt));

    public int PageCount => _pages.Count;

    public void Save(Stream output)
    {
        if (_pages.Count == 0) throw new InvalidOperationException("Không có trang nào để xuất.");

        var offsets = new List<long>();
        var w = new PdfOut(output);
        w.Raw("%PDF-1.4\n%âãÏÓ\n");

        // Bố cục object: 1 = Catalog, 2 = Pages, rồi mỗi trang 3 object: Page, Contents, Image.
        int pageObj(int i) => 3 + i * 3;
        int contentObj(int i) => 4 + i * 3;
        int imageObj(int i) => 5 + i * 3;
        int objectCount = 2 + _pages.Count * 3;

        void Begin(int id) { offsets.Add(w.Position); w.Raw($"{id} 0 obj\n"); }
        void End() => w.Raw("endobj\n");

        Begin(1);
        w.Raw("<< /Type /Catalog /Pages 2 0 R >>\n");
        End();

        Begin(2);
        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{pageObj(i)} 0 R"));
        w.Raw($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>\n");
        End();

        for (int i = 0; i < _pages.Count; i++)
        {
            PageImage p = _pages[i];
            string W = Num(p.WidthPt), H = Num(p.HeightPt);

            Begin(pageObj(i));
            w.Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] " +
                  $"/Resources << /XObject << /Im{i} {imageObj(i)} 0 R >> >> /Contents {contentObj(i)} 0 R >>\n");
            End();

            // Ma trận [W 0 0 H 0 0]: kéo ảnh đơn vị 1×1 phủ kín trang.
            byte[] content = Encoding.ASCII.GetBytes($"q {W} 0 0 {H} 0 0 cm /Im{i} Do Q\n");
            Begin(contentObj(i));
            w.Raw($"<< /Length {content.Length} >>\nstream\n");
            w.Bytes(content);
            w.Raw("endstream\n");
            End();

            Begin(imageObj(i));
            w.Raw($"<< /Type /XObject /Subtype /Image /Width {p.PixelWidth} /Height {p.PixelHeight} " +
                  $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            w.Bytes(p.Jpeg);
            w.Raw("\nendstream\n");
            End();
        }

        long xref = w.Position;
        w.Raw($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets)
            w.Raw($"{off:0000000000} 00000 n \n");
        w.Raw($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R " +
              $"/Info << /Producer (OmniPen) >> >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Ghi byte và đếm vị trí (bảng xref cần offset chính xác của từng object).</summary>
    private sealed class PdfOut(Stream s)
    {
        public long Position { get; private set; }

        public void Raw(string text) => Bytes(Encoding.Latin1.GetBytes(text));

        public void Bytes(byte[] data)
        {
            s.Write(data, 0, data.Length);
            Position += data.Length;
        }
    }
}
