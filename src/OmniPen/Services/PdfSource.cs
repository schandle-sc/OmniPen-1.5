using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using OmniPen.Ink;
using Windows.Storage;
using Windows.Storage.Streams;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;

namespace OmniPen.Services;

/// <summary>
/// Một file PDF đang mở. Hai thư viện, mỗi cái làm một việc:
///   - Windows.Data.Pdf (có sẵn trong Windows 10/11, không cần cài): RENDER trang thành ảnh nét, nhanh.
///   - PdfPig (NuGet, mã nguồn mở): đọc CHỮ kèm vị trí từng từ → chọn chữ, sao chép, tô sáng, gạch chân.
///
/// Trang được render theo yêu cầu (lazy) chứ không render hết lúc mở: PDF 100 trang × ảnh 1600 px
/// sẽ chiếm hơn 1 GB RAM nếu giữ tất cả.
/// </summary>
public sealed class PdfSource : IDisposable
{
    /// <summary>Đường dẫn file PDF (bản sao trong thư mục tạm nếu mở từ file .omnipen).</summary>
    public string FilePath { get; }
    public string DisplayName { get; }
    public int PageCount => (int)_doc.PageCount;

    private readonly WinPdfDocument _doc;
    private readonly List<IReadOnlyList<PdfWord>> _words;
    private readonly List<Size> _sizes;

    private PdfSource(string path, WinPdfDocument doc, List<Size> sizes, List<IReadOnlyList<PdfWord>> words)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        _doc = doc;
        _sizes = sizes;
        _words = words;
    }

    public static async Task<PdfSource> OpenAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        WinPdfDocument doc = await WinPdfDocument.LoadFromFileAsync(file);

        // Kích thước trang theo DIP (Windows.Data.Pdf đã tính CropBox và góc xoay của trang).
        var sizes = new List<Size>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var p = doc.GetPage(i);
            sizes.Add(new Size(p.Size.Width, p.Size.Height));
        }

        // Trích chữ ở luồng nền: PDF dày có thể mất vài giây.
        List<IReadOnlyList<PdfWord>> words = await Task.Run(() => ExtractWords(path, sizes));
        return new PdfSource(path, doc, sizes, words);
    }

    public Size PageSize(int index) => _sizes[index];

    public IReadOnlyList<PdfWord> Words(int index) => index < _words.Count ? _words[index] : Array.Empty<PdfWord>();

    /// <summary>Render một trang thành ảnh, chiều rộng = widthPx pixel.</summary>
    public async Task<BitmapSource> RenderPageAsync(int index, int widthPx)
    {
        using var page = _doc.GetPage((uint)index);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new WinPdfRenderOptions { DestinationWidth = (uint)Math.Max(16, widthPx) });

        // Chép sang MemoryStream rồi cho WPF giải mã (BitmapCacheOption.OnLoad: đọc hết, không giữ stream).
        stream.Seek(0);
        using Stream src = stream.AsStream();
        var ms = new MemoryStream();
        await src.CopyToAsync(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Đọc từng từ bằng PdfPig và đổi tọa độ PDF (điểm 1/72 inch, gốc ở GÓC DƯỚI-TRÁI)
    /// sang tọa độ trang của OmniPen (DIP 1/96 inch, gốc ở GÓC TRÊN-TRÁI).
    /// Trang bị xoay (Rotate 90/180/270) tạm thời không hỗ trợ chọn chữ.
    /// </summary>
    private static List<IReadOnlyList<PdfWord>> ExtractWords(string path, List<Size> sizes)
    {
        var result = new List<IReadOnlyList<PdfWord>>();
        try
        {
            using PigDocument pdf = PigDocument.Open(path);
            for (int i = 1; i <= pdf.NumberOfPages && i <= sizes.Count; i++)
            {
                var list = new List<PdfWord>();
                try
                {
                    var page = pdf.GetPage(i);
                    if (page.Rotation.Value % 360 == 0 && page.Width > 0 && page.Height > 0)
                    {
                        double sx = sizes[i - 1].Width / page.Width;
                        double sy = sizes[i - 1].Height / page.Height;
                        foreach (var w in page.GetWords())
                        {
                            if (string.IsNullOrWhiteSpace(w.Text)) continue;
                            var bb = w.BoundingBox;
                            var rect = new Rect(
                                bb.Left * sx,
                                (page.Height - bb.Top) * sy,
                                Math.Max(1, (bb.Right - bb.Left) * sx),
                                Math.Max(1, (bb.Top - bb.Bottom) * sy));
                            list.Add(new PdfWord(rect, w.Text));
                        }
                    }
                }
                catch
                {
                    // Trang lỗi font/mã hóa → vẫn hiển thị được, chỉ không chọn chữ được.
                }
                result.Add(list);
            }
        }
        catch
        {
            // PDF có mật khẩu hoặc cấu trúc lạ mà PdfPig không đọc được – vẫn xem/viết được.
        }
        while (result.Count < sizes.Count) result.Add(Array.Empty<PdfWord>());
        return result;
    }

    public void Dispose()
    {
        // WinRT PdfDocument không có Dispose; chỉ cần bỏ tham chiếu. Giữ hàm để sau này dọn file tạm.
    }
}
