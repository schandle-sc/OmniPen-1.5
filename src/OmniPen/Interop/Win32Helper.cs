using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OmniPen.Interop;

/// <summary>
/// Tập hợp P/Invoke tới user32.dll và các hàm tiện ích thao tác Extended Window Style.
///
/// Kiến thức nền:
///   - Mỗi cửa sổ Win32 có 2 bộ cờ: Style (GWL_STYLE) và Extended Style (GWL_EXSTYLE).
///   - Khi WPF Window đặt AllowsTransparency="True", WPF tự gắn WS_EX_LAYERED và vẽ bằng
///     UpdateLayeredWindow (per-pixel alpha). Với cửa sổ layered:
///       * Pixel alpha = 0  → Windows TỰ ĐỘNG cho chuột xuyên qua tại pixel đó.
///       * Pixel alpha > 0  → cửa sổ nhận chuột tại pixel đó.
///   - Thêm WS_EX_TRANSPARENT vào một cửa sổ layered → TOÀN BỘ cửa sổ bị bỏ qua khi hit-test,
///     kể cả chỗ đã có nét vẽ. Đây chính là cơ chế "xuyên chuột" (click-through) ta cần.
/// </summary>
internal static class Win32Helper
{
    #region Constants

    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TRANSPARENT = 0x0000_0020; // Bỏ qua hit-test (dùng kèm WS_EX_LAYERED)
    public const int WS_EX_TOOLWINDOW  = 0x0000_0080; // Ẩn khỏi Alt+Tab và taskbar
    public const int WS_EX_APPWINDOW   = 0x0004_0000;
    public const int WS_EX_LAYERED     = 0x0008_0000;
    public const int WS_EX_NOACTIVATE  = 0x0800_0000; // Click vào không cướp focus của app đang dùng

    public static readonly IntPtr HWND_TOPMOST   = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const uint SWP_NOSIZE         = 0x0001;
    public const uint SWP_NOMOVE         = 0x0002;
    public const uint SWP_NOZORDER       = 0x0004;
    public const uint SWP_NOACTIVATE     = 0x0010;
    public const uint SWP_FRAMECHANGED   = 0x0020;
    public const uint SWP_SHOWWINDOW     = 0x0040;
    public const uint SWP_NOOWNERZORDER  = 0x0200;

    public const int WM_STYLECHANGING = 0x007C;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_HOTKEY        = 0x0312;
    public const int WM_DPICHANGED    = 0x02E0;

    // Modifier cho RegisterHotKey (dùng ở task phím tắt toàn cục)
    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000; // Giữ phím không bắn WM_HOTKEY liên tục

    #endregion

    #region P/Invoke

    // GetWindowLongPtr/SetWindowLongPtr chỉ là export thật trên user32 64-bit.
    // Trên process 32-bit chúng là macro trỏ về GetWindowLong/SetWindowLong → phải rẽ nhánh theo IntPtr.Size,
    // nếu không sẽ gặp EntryPointNotFoundException khi build x86.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>Cha đặc biệt cho "message-only window": không hiển thị, chỉ nhận message (dùng cho hotkey).</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Cấu trúc lParam của WM_STYLECHANGING / WM_STYLECHANGED.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STYLESTRUCT
    {
        public int styleOld;
        public int styleNew;
    }

    #endregion

    #region Extended style helpers

    public static int GetExStyle(IntPtr hwnd)
    {
        return IntPtr.Size == 8
            ? (int)GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hwnd, GWL_EXSTYLE);
    }

    public static void SetExStyle(IntPtr hwnd, int exStyle)
    {
        // SetWindowLong trả về giá trị CŨ; giá trị cũ = 0 là hợp lệ.
        // Vì vậy phải xóa LastError trước, rồi chỉ coi là lỗi khi (kết quả == 0 && LastError != 0).
        SetLastError(0);
        long previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(exStyle)).ToInt64()
            : SetWindowLong32(hwnd, GWL_EXSTYLE, exStyle);

        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error, "SetWindowLong(GWL_EXSTYLE) thất bại.");

        // Windows cache một phần thông tin style. SWP_FRAMECHANGED buộc hệ thống áp dụng style mới ngay,
        // không đổi vị trí/kích thước/z-order và không kích hoạt cửa sổ.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    public static void AddExStyle(IntPtr hwnd, int flags)    => SetExStyle(hwnd, GetExStyle(hwnd) | flags);
    public static void RemoveExStyle(IntPtr hwnd, int flags) => SetExStyle(hwnd, GetExStyle(hwnd) & ~flags);

    /// <summary>
    /// Bật/tắt xuyên chuột cho một cửa sổ layered (WPF AllowsTransparency="True").
    /// Chỉ ghi lại style khi trạng thái thực sự thay đổi để tránh SetWindowPos thừa.
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;

        int current = GetExStyle(hwnd);

        // WS_EX_TRANSPARENT chỉ cho xuyên chuột "đúng nghĩa" khi cửa sổ cũng là layered.
        // WPF đã gắn sẵn WS_EX_LAYERED khi AllowsTransparency=True; thêm lại cho chắc chắn.
        int desired = enabled
            ? current | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : current & ~WS_EX_TRANSPARENT;

        if (desired != current)
            SetExStyle(hwnd, desired);
    }

    public static bool IsClickThrough(IntPtr hwnd) => (GetExStyle(hwnd) & WS_EX_TRANSPARENT) != 0;

    #endregion
}
