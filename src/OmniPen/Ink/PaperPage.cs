using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using OmniPen.Services;

namespace OmniPen.Ink;

/// <summary>Một từ trong trang PDF, tọa độ theo hệ tọa độ trang (DIP), dùng cho chọn chữ.</summary>
public sealed record PdfWord(Rect Bounds, string Text);

/// <summary>
/// Dữ liệu gốc của một ảnh/công thức chèn vào trang. Gắn vào <see cref="FrameworkElement.Tag"/> của Image
/// để lưu file và xuất SVG đúng ảnh gốc (không phải ảnh đã co giãn).
/// </summary>
public sealed class ElementData
{
    public required byte[] Bytes { get; init; }
    public string Kind { get; init; } = "image"; // "image" | "latex"
    public string? Latex { get; init; }
}

/// <summary>
/// Một trang. Mỗi trang sở hữu MỘT InkCanvas riêng:
///   - Nét vẽ (Strokes) và ảnh chèn (Children) nằm sẵn trong InkCanvas → chuyển trang chỉ là thay
///     InkCanvas trong khung hiển thị, không Clear/Add, không làm bẩn lịch sử.
///   - Lịch sử Undo gắn cố định vào StrokeCollection của trang ngay khi tạo.
/// Trang bảng có kích thước (Width/Height, DIP) và góc nhìn riêng (View: phóng to + dịch chuyển).
/// </summary>
public sealed class PaperPage
{
    public InkCanvas Ink { get; }
    public StrokeCollection Strokes => Ink.Strokes;
    public InkHistory History { get; }
    public PaperType Paper { get; set; }

    /// <summary>Kích thước trang (DIP). 0 = chưa khởi tạo – sẽ lấy theo màn hình khi hiển thị lần đầu.</summary>
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Ma trận hệ tọa độ trang → màn hình (zoom + pan).</summary>
    public Matrix View { get; set; } = Matrix.Identity;
    public bool ViewInitialized { get; set; }

    // ===== PDF =====
    public PdfSource? Pdf { get; init; }
    public int PdfPageIndex { get; init; }
    public IReadOnlyList<PdfWord> Words { get; set; } = Array.Empty<PdfWord>();
    /// <summary>Ảnh nền đã render (cache, có thể bị giải phóng để tiết kiệm RAM).</summary>
    public ImageSource? BackgroundImage { get; set; }
    public bool IsPdf => Pdf is not null;

    public PaperPage(PaperType paper)
    {
        Paper = paper;
        Ink = new InkCanvas
        {
            Background = Brushes.Transparent,
            EditingMode = InkCanvasEditingMode.None,
            ClipToBounds = false
        };
        History = new InkHistory(Ink.Strokes);
    }

    public IEnumerable<Image> Images => Ink.Children.OfType<Image>();

    /// <summary>Mọi phần tử có dữ liệu gốc (ảnh, công thức, code, văn bản, băng che chữ).</summary>
    public IEnumerable<FrameworkElement> Elements => Ink.Children.OfType<FrameworkElement>().Where(e => e.Tag is ElementData);

    public IEnumerable<FrameworkElement> Tapes => Ink.Children.OfType<FrameworkElement>().Where(PageElements.IsTape);

    public bool HasContent => Strokes.Count > 0 || Ink.Children.Count > 0;

    public double Zoom => View.M11;
}

/// <summary>
/// Tài liệu = 1 trang chú thích (vẽ trên màn hình) + danh sách trang bảng.
/// Hai không gian tách biệt: tắt bảng thì nét chú thích hiện lại nguyên vẹn.
/// </summary>
public sealed class InkDocument
{
    public PaperPage Annotation { get; private set; } = new(PaperType.None);
    public List<PaperPage> BoardPages { get; } = new();
    public int BoardIndex { get; private set; }

    public PaperPage CurrentBoardPage => BoardPages[BoardIndex];

    public InkDocument(PaperType defaultBoardPaper)
    {
        BoardPages.Add(new PaperPage(Normalize(defaultBoardPaper)));
    }

    /// <summary>Thêm trang trắng ngay sau trang hiện tại và chuyển tới trang đó.</summary>
    public void AddPageAfterCurrent()
    {
        PaperPage cur = CurrentBoardPage;
        PaperType paper = cur.IsPdf ? PaperType.White : cur.Paper;
        var page = new PaperPage(paper);
        if (cur.IsPdf)
        {
            // Trang ghi chú chèn giữa tài liệu PDF: cùng khổ với trang PDF cho liền mạch khi xuất.
            page.Width = cur.Width;
            page.Height = cur.Height;
        }
        BoardPages.Insert(BoardIndex + 1, page);
        BoardIndex++;
    }

    /// <summary>Xóa trang hiện tại. Nếu chỉ còn 1 trang thì xóa nội dung của nó (nét xóa được Undo).</summary>
    public void DeleteCurrentPage()
    {
        if (BoardPages.Count == 1)
        {
            CurrentBoardPage.Strokes.Clear();
            CurrentBoardPage.Ink.Children.Clear();
            return;
        }
        BoardPages.RemoveAt(BoardIndex);
        BoardIndex = Math.Min(BoardIndex, BoardPages.Count - 1);
    }

    public bool GoTo(int index)
    {
        if (index < 0 || index >= BoardPages.Count || index == BoardIndex) return false;
        BoardIndex = index;
        return true;
    }

    /// <summary>Thay toàn bộ trang bảng (mở file, mở PDF).</summary>
    public void ReplaceBoardPages(IEnumerable<PaperPage> pages)
    {
        var list = pages.ToList();
        if (list.Count == 0) list.Add(new PaperPage(PaperType.White));

        foreach (PdfSource src in BoardPages.Select(p => p.Pdf).OfType<PdfSource>().Distinct()
                     .Except(list.Select(p => p.Pdf).OfType<PdfSource>()))
            src.Dispose();

        BoardPages.Clear();
        BoardPages.AddRange(list);
        BoardIndex = 0;
    }

    /// <summary>Thay cả trang chú thích (dùng khi mở file .omnipen).</summary>
    public void ReplaceAnnotation(PaperPage annotation) => Annotation = annotation;

    public bool HasAnyContent => Annotation.HasContent || BoardPages.Any(p => p.HasContent || p.IsPdf);

    public static PaperType Normalize(PaperType t) => t is PaperType.None or PaperType.Pdf ? PaperType.White : t;
}
