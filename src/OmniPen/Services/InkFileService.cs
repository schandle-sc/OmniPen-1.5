using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using OmniPen.Ink;

namespace OmniPen.Services;

/// <summary>
/// Định dạng .omnipen (phiên bản 2) = file ZIP gồm:
///   manifest.json          : danh sách trang, kiểu giấy, khổ trang, ảnh chèn, liên kết PDF
///   annotation.isf         : nét chú thích màn hình
///   page_001.isf, ...      : nét từng trang bảng (ISF – định dạng gốc của Windows Ink)
///   el_001_01.png, ...     : ảnh/công thức đã chèn (lưu dữ liệu gốc)
///   pdf_0.pdf, ...         : bản sao file PDF nền (để mở lại ở máy khác vẫn đủ)
/// Đọc được cả file phiên bản 1 (OmniPen 1.2).
/// </summary>
public static class InkFileService
{
    public const string Extension = ".omnipen";
    public const string Filter = "Tài liệu OmniPen (*.omnipen)|*.omnipen";

    private sealed class Manifest
    {
        public int Version { get; set; }
        public string? App { get; set; }
        public List<ElementEntry> AnnotationElements { get; set; } = new();
        public List<PageEntry> Pages { get; set; } = new();
        public List<string> PdfFiles { get; set; } = new();
    }

    private sealed class PageEntry
    {
        public string File { get; set; } = "";
        public PaperType Paper { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int PdfIndex { get; set; } = -1;
        public int PdfPage { get; set; }
        public List<ElementEntry> Elements { get; set; } = new();
    }

    private sealed class ElementEntry
    {
        public string File { get; set; } = "";
        public string Kind { get; set; } = "image";
        public string? Latex { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Save(string path, InkDocument doc)
    {
        string tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            var manifest = new Manifest { Version = 2, App = "OmniPen " + AppInfo.Version };

            WriteStrokes(zip, "annotation.isf", doc.Annotation.Strokes);
            manifest.AnnotationElements = WriteElements(zip, doc.Annotation, "el_000");

            var pdfs = doc.BoardPages.Select(p => p.Pdf).OfType<PdfSource>().Distinct().ToList();
            for (int i = 0; i < pdfs.Count; i++)
            {
                string name = $"pdf_{i}.pdf";
                zip.CreateEntryFromFile(pdfs[i].FilePath, name, CompressionLevel.Fastest);
                manifest.PdfFiles.Add(name);
            }

            for (int i = 0; i < doc.BoardPages.Count; i++)
            {
                PaperPage page = doc.BoardPages[i];
                string name = $"page_{i + 1:000}.isf";
                WriteStrokes(zip, name, page.Strokes);
                manifest.Pages.Add(new PageEntry
                {
                    File = name,
                    Paper = page.Paper,
                    Width = page.Width,
                    Height = page.Height,
                    PdfIndex = page.Pdf is null ? -1 : pdfs.IndexOf(page.Pdf),
                    PdfPage = page.PdfPageIndex,
                    Elements = WriteElements(zip, page, $"el_{i + 1:000}")
                });
            }

            using var s = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(s, manifest, Json);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Đọc file. PDF nhúng được giải nén ra thư mục tạm rồi mở lại.</summary>
    public static async Task<(PaperPage Annotation, List<PaperPage> Pages)> LoadAsync(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        ZipArchiveEntry entry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("File không phải tài liệu OmniPen hợp lệ.");
        Manifest manifest;
        using (var s = entry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(s) ?? throw new InvalidDataException("manifest.json bị hỏng.");

        var annotation = new PaperPage(PaperType.None);
        annotation.History.ReplaceAllWithoutHistory(ReadStrokes(zip, "annotation.isf"));
        AddElements(zip, annotation, manifest.AnnotationElements);

        var pdfs = new List<PdfSource>();
        foreach (string name in manifest.PdfFiles)
        {
            string dir = Path.Combine(Path.GetTempPath(), "OmniPen", "pdf");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{Guid.NewGuid():N}.pdf");
            zip.GetEntry(name)?.ExtractToFile(file, overwrite: true);
            pdfs.Add(await PdfSource.OpenAsync(file));
        }

        var pages = new List<PaperPage>();
        foreach (PageEntry pe in manifest.Pages)
        {
            PaperPage page;
            if (pe.PdfIndex >= 0 && pe.PdfIndex < pdfs.Count && pe.PdfPage < pdfs[pe.PdfIndex].PageCount)
            {
                PdfSource src = pdfs[pe.PdfIndex];
                Size size = src.PageSize(pe.PdfPage);
                page = new PaperPage(PaperType.Pdf) { Pdf = src, PdfPageIndex = pe.PdfPage, Width = size.Width, Height = size.Height };
                page.Words = src.Words(pe.PdfPage);
            }
            else
            {
                page = new PaperPage(InkDocument.Normalize(pe.Paper)) { Width = pe.Width, Height = pe.Height };
            }
            page.History.ReplaceAllWithoutHistory(ReadStrokes(zip, pe.File));
            AddElements(zip, page, pe.Elements);
            pages.Add(page);
        }
        return (annotation, pages);
    }

    private static List<ElementEntry> WriteElements(ZipArchive zip, PaperPage page, string prefix)
    {
        var list = new List<ElementEntry>();
        int n = 0;
        foreach (FrameworkElement el in page.Elements)
        {
            if (el.Tag is not ElementData data) continue;
            Rect r = ExportService.ElementRect(el);
            string name = "";
            if (data.Bytes.Length > 0)
            {
                name = $"{prefix}_{++n:00}.bin";
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                s.Write(data.Bytes, 0, data.Bytes.Length);
            }
            list.Add(new ElementEntry { File = name, Kind = data.Kind, Latex = data.Latex, X = r.X, Y = r.Y, W = r.Width, H = r.Height });
        }
        return list;
    }

    private static void AddElements(ZipArchive zip, PaperPage page, IEnumerable<ElementEntry> entries)
    {
        foreach (ElementEntry e in entries)
        {
            if (e.Kind == "tape")
            {
                page.Ink.Children.Add(PageElements.CreateTape(new Rect(e.X, e.Y, e.W, e.H)));
                continue;
            }
            ZipArchiveEntry? ze = string.IsNullOrEmpty(e.File) ? null : zip.GetEntry(e.File);
            if (ze is null) continue;
            using var src = ze.Open();
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            try
            {
                page.Ink.Children.Add(PageElements.Create(ms.ToArray(), new Rect(e.X, e.Y, e.W, e.H), e.Kind, e.Latex));
            }
            catch { /* ảnh hỏng – bỏ qua, không làm hỏng cả tài liệu */ }
        }
    }

    private static void WriteStrokes(ZipArchive zip, string name, StrokeCollection strokes)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        strokes.Save(s, compress: true);
    }

    private static StrokeCollection ReadStrokes(ZipArchive zip, string name)
    {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        if (entry is null) return new StrokeCollection();

        // Constructor StrokeCollection(Stream) cần stream seek được → copy ra MemoryStream trước.
        using var src = entry.Open();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        ms.Position = 0;
        return ms.Length == 0 ? new StrokeCollection() : new StrokeCollection(ms);
    }
}
