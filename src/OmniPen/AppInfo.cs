using System.Diagnostics;
using System.Reflection;

namespace OmniPen;

/// <summary>Thông tin sản phẩm, tác giả, phiên bản (phiên bản đọc từ metadata assembly khai báo trong OmniPen.csproj).</summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    public static string Name => "OmniPen";

    /// <summary>VD "1.5".</summary>
    public static string Version =>
        Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Asm.GetName().Version?.ToString(2)
        ?? "1.5";

    public static string AuthorName => "Hồ Văn Thuyên";
    public static string School => "Trường THPT Hoàng Hoa Thám";
    public static string Author => $"{AuthorName} - {School}";

    public static string Website => "www.giaoducso.org";
    public static string DonateUrl => "https://www.giaoducso.org";

    public static string Description => "Viết bảng điện tử & chú thích màn hình";

    public static string Tagline =>
        "Phần mềm viết bảng điện tử miễn phí dành cho giáo viên: viết, vẽ, chú thích trực tiếp lên màn hình, " +
        "biến máy tính thành bảng đen – bảng trắng nhiều trang, ghi chú lên PDF và xuất bài giảng chỉ với vài cú nhấp.";

    public static string Copyright =>
        Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? $"© {Author}";

    /// <summary>Tổng quan tính năng (hiện trong cửa sổ Giới thiệu).</summary>
    public static IReadOnlyList<(string Icon, string Title, string Text)> Features { get; } = new[]
    {
        ("✏", "Viết trên mọi ứng dụng",
            "Bút có lực nhấn, bút dạ quang, tẩy một phần / cả nét. Viết đè lên PowerPoint, Word, trình duyệt, video… rồi bấm Chuột để thao tác tiếp."),
        ("▦", "Bảng nhiều trang",
            "Giấy trắng, kẻ ngang, ô vuông, ô ly vở 4 ô ly, bảng trắng, bảng đen, bảng xanh. Thêm/xóa/chuyển trang thoải mái."),
        ("⇣", "Tự cuộn – trang không giới hạn",
            "Viết tới cuối màn hình, trang tự trôi lên. Phóng to/thu nhỏ (Ctrl + lăn chuột), kéo trang bằng bàn tay hoặc nút giữa chuột."),
        ("📄", "Ghi chú lên PDF",
            "Mở PDF để viết trực tiếp; chọn chữ để sao chép, tô sáng, gạch chân, gạch ngang."),
        ("△", "Hình học & dụng cụ",
            "Đường thẳng, mũi tên, hình chữ nhật, tròn, tam giác, hình hộp, bảng N×M; thước kẻ nắn thẳng nét và compa điện tử."),
        ("🖼", "Chèn ảnh & công thức",
            "Ảnh từ máy, Clipboard hoặc chụp từ camera; công thức toán LaTeX đẹp như sách giáo khoa."),
        ("💾", "Lưu & xuất linh hoạt",
            "Lưu bài giảng .omnipen để dạy lại; xuất PDF, PNG (có thể nền trong suốt), SVG vector; chụp màn hình kèm nét vẽ."),
        ("📈", "Hệ trục Oxy & vẽ đồ thị",
            "Gõ y = sin(x) hay y = x^2 - 4x + 3 là có ngay đồ thị vector chuẩn xác trên hệ trục, tối đa 3 hàm cùng lúc."),
        ("◼", "Băng che chữ (điền từ)",
            "Quẹt bút băng lên từ cần che như trò chơi điền vào chỗ trống; bấm nhẹ để băng bốc hơi, lộ đáp án."),
        ("✦", "Bút laser & tự nắn hình",
            "Nét laser phát sáng tự mờ sau 3 giây. Vẽ tay hình tròn, tam giác… rồi giữ bút 1 giây để nắn thành hình chuẩn."),
        ("🔍", "Trình chiếu chuyên nghiệp",
            "Kính lúp động 2×/3×, khung focus làm tối mọi thứ trừ cửa sổ đang dùng, hiển thị phím tắt và hiệu ứng click chuột."),
        ("🛡", "Làm mờ bảo mật",
            "Kéo khung để làm mờ trực tiếp mật khẩu, API key, email học sinh trên màn hình chiếu mà không dừng bài giảng."),
        ("</>", "Code nổi & chữ viết tay → văn bản",
            "Dán đoạn code từ Clipboard thành khối tô màu cú pháp (Dracula). Khoanh chữ viết tay để chuyển thành chữ đánh máy hoặc chữ tay đẹp."),
        ("⏱", "Tiện ích lớp học",
            "Đồng hồ đếm ngược cho bài kiểm tra 15 phút và vòng quay gọi tên ngẫu nhiên ngay trên màn hình."),
        ("⌨", "Gọn nhẹ, tiện dụng",
            "Phím tắt toàn cục, thanh công cụ ngang/dọc, chụp toàn màn hình hoặc vùng chọn, thu nhỏ xuống khay, hỗ trợ nhiều màn hình và máy chiếu.")
    };

    /// <summary>Mở trang ủng hộ tác giả bằng trình duyệt mặc định.</summary>
    public static void OpenDonatePage()
    {
        try { Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true }); }
        catch { /* máy không có trình duyệt mặc định */ }
    }
}
