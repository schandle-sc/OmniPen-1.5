using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using OmniPen.Ink;

namespace OmniPen.Services;

/// <summary>Cấu hình người dùng, lưu ở %AppData%\OmniPen\settings.json.</summary>
public sealed class AppSettings
{
    public string HotkeyToggleDraw { get; set; } = "Ctrl+Shift+D";
    public string HotkeyEraser { get; set; } = "Ctrl+Shift+E";
    public string HotkeyClear { get; set; } = "Ctrl+Shift+C";
    public string HotkeyBoard { get; set; } = "Ctrl+Shift+B";
    public string HotkeyToolbar { get; set; } = "Ctrl+Shift+H";

    public string PenColor { get; set; } = "#FFE53935";
    public double PenSize { get; set; } = 4;
    public PaperType DefaultPaper { get; set; } = PaperType.OLy;

    /// <summary>Vị trí toolbar theo pixel vật lý (null = mặc định giữa cạnh trên màn hình chính).</summary>
    public int? ToolbarX { get; set; }
    public int? ToolbarY { get; set; }

    public bool TrayTipShown { get; set; }

    // ===== v1.3 =====
    /// <summary>Thanh công cụ dọc (true) hay ngang (false).</summary>
    public bool ToolbarVertical { get; set; }
    /// <summary>Tự cuộn trang bảng khi viết gần mép dưới.</summary>
    public bool AutoScroll { get; set; } = true;
    /// <summary>Hiện nút tròn nhỏ ở góc màn hình khi ẩn thanh công cụ.</summary>
    public bool ShowFloatingButton { get; set; } = true;
    public int? FloatingX { get; set; }
    public int? FloatingY { get; set; }
    /// <summary>Thư mục chứa latex.exe/dvipng.exe nếu không tự tìm thấy.</summary>
    public string? LatexFolder { get; set; }
    public int TableRows { get; set; } = 3;
    public int TableCols { get; set; } = 3;

    // ===== v1.5 =====
    public string HotkeyMagnifier { get; set; } = "Ctrl+Shift+M";
    public string HotkeyCode { get; set; } = "Ctrl+Shift+Q";
    public string HotkeySnip { get; set; } = "Ctrl+Shift+X";
    /// <summary>Hiển thị phím: true = mọi phím; false = chỉ tổ hợp có Ctrl/Alt/Win và phím đặc biệt.</summary>
    public bool ShowAllKeys { get; set; }
    /// <summary>Giữ bút 0,8 giây ở cuối nét để nắn thành hình chuẩn.</summary>
    public bool AutoShape { get; set; } = true;
    public string? PickerNames { get; set; }
}

public static class SettingsService
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniPen");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // File hỏng (sửa tay sai cú pháp…) → dùng mặc định thay vì không mở được app.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // Ghi ra file tạm rồi thay thế: tránh hỏng file nếu mất điện giữa chừng.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Không lưu được cấu hình không phải lỗi nghiêm trọng.
        }
    }
}

/// <summary>Khởi động cùng Windows qua khóa HKCU\...\Run (không cần quyền Admin).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OmniPen";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Chính sách nhóm của trường có thể khóa Registry – bỏ qua.
        }
    }
}
