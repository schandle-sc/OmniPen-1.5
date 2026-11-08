using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OmniPen.Interop;

/// <summary>
/// P/Invoke cho các tính năng v1.5: chụp màn hình bằng GDI, kính lúp, làm mờ, hook bàn phím/chuột,
/// tìm cửa sổ dưới con trỏ (khung focus).
/// </summary>
internal static class NativeMethods
{
    // ===================== GDI chụp màn hình =====================
    public const int SRCCOPY = 0x00CC0020;
    /// <summary>Bắt buộc để chụp được cửa sổ layered (lớp vẽ của OmniPen, menu, tooltip…).</summary>
    public const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);

    /// <summary>
    /// Chụp một vùng màn hình (pixel vật lý) thành BitmapSource đã Freeze.
    /// Gọi thẳng BitBlt với SRCCOPY | CAPTUREBLT. (Graphics.CopyFromScreen của .NET từ chối giá trị gộp này
    /// vì nó không phải một thành viên hợp lệ của enum CopyPixelOperation – đó là lỗi của bản 1.4.)
    /// </summary>
    public static BitmapSource CaptureScreen(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr old = SelectObject(memDc, bmp);
        try
        {
            BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(memDc, old);
            BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(bmp); // HBITMAP là tài nguyên GDI không được GC thu hồi → phải tự xóa.
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ===================== Loại cửa sổ khỏi ảnh chụp =====================
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // Windows 10 2004+

    /// <summary>
    /// Kính lúp và vùng làm mờ tự chụp màn hình phía dưới chúng mỗi khung hình. Đánh dấu "loại khỏi ảnh chụp"
    /// để BitBlt nhìn xuyên qua chính chúng (nếu không sẽ chụp lại chính mình → hiệu ứng gương vô tận).
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    // ===================== Con trỏ & cửa sổ =====================
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT pt);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Win32Helper.RECT rect, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ===================== Hook bàn phím / chuột cấp thấp =====================
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;

    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
}
