namespace OmniPen.Ink;

/// <summary>Kiểu nền trang.</summary>
public enum PaperType
{
    /// <summary>Không có giấy – chế độ chú thích trên màn hình.</summary>
    None,
    White,
    Lined,
    Grid,
    /// <summary>Giấy ô ly kiểu vở học sinh Việt Nam (ô lớn chia 4 dòng kẻ nhỏ).</summary>
    OLy,
    Whiteboard,
    Blackboard,
    Greenboard,
    /// <summary>Nền là một trang PDF (không đổi được kiểu giấy).</summary>
    Pdf
}

public static class PaperTypeExtensions
{
    public static string DisplayName(this PaperType t) => t switch
    {
        PaperType.White => "Giấy trắng",
        PaperType.Lined => "Giấy kẻ ngang",
        PaperType.Grid => "Giấy ô vuông",
        PaperType.OLy => "Giấy ô ly (vở 4 ô ly)",
        PaperType.Whiteboard => "Bảng trắng",
        PaperType.Blackboard => "Bảng đen",
        PaperType.Greenboard => "Bảng xanh",
        PaperType.Pdf => "Trang PDF",
        _ => "Không"
    };

    /// <summary>Nền tối ⇒ bút mặc định nên là màu sáng.</summary>
    public static bool IsDark(this PaperType t) => t is PaperType.Blackboard or PaperType.Greenboard;

    /// <summary>Các kiểu người dùng được chọn trong menu "Giấy".</summary>
    public static IEnumerable<PaperType> Selectable() =>
        Enum.GetValues<PaperType>().Where(t => t is not (PaperType.None or PaperType.Pdf));
}
