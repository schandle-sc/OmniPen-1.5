using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using OmniPen.Interop;

namespace OmniPen.Services;

/// <summary>
/// Hook bàn phím & chuột cấp thấp (WH_KEYBOARD_LL / WH_MOUSE_LL) cho tính năng "Hiển thị phím & chuột".
/// - CHỈ bật khi người dùng bật tính năng; không ghi log, không lưu gì – chỉ phát sự kiện để hiển thị.
/// - Hook LL chạy trên luồng đã cài nó (UI thread) qua vòng lặp message; callback phải trả về thật nhanh
///   (Windows tự gỡ hook chậm) → chỉ đọc dữ liệu rồi đẩy việc vẽ sang Dispatcher.
/// - Luôn gọi CallNextHookEx: không bao giờ chặn phím/chuột của ứng dụng khác.
/// </summary>
public sealed class InputHookService : IDisposable
{
    public event EventHandler<string>? KeyCombo;
    /// <summary>Điểm (pixel vật lý) và nút chuột vừa bấm.</summary>
    public event EventHandler<(Point Screen, MouseButton Button)>? MouseClick;

    /// <summary>true: hiện cả phím gõ thường (chữ, số); false: chỉ tổ hợp có Ctrl/Alt/Win và phím đặc biệt.</summary>
    public bool ShowAllKeys { get; set; }

    private IntPtr _kbHook, _mouseHook;
    // Giữ tham chiếu delegate: nếu GC thu hồi, Windows gọi vào con trỏ chết → crash.
    private readonly NativeMethods.LowLevelProc _kbProc, _mouseProc;

    public bool KeyboardEnabled => _kbHook != IntPtr.Zero;
    public bool MouseEnabled => _mouseHook != IntPtr.Zero;

    public InputHookService()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Enable(bool keyboard, bool mouse)
    {
        IntPtr mod = NativeMethods.GetModuleHandle(null);
        if (keyboard && _kbHook == IntPtr.Zero)
            _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbProc, mod, 0);
        if (!keyboard && _kbHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
        }

        if (mouse && _mouseHook == IntPtr.Zero)
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, mod, 0);
        if (!mouse && _mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                string? combo = Describe((int)data.vkCode);
                if (combo is not null)
                    Application.Current?.Dispatcher.BeginInvoke(() => KeyCombo?.Invoke(this, combo));
            }
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            MouseButton? btn = msg switch
            {
                NativeMethods.WM_LBUTTONDOWN => MouseButton.Left,
                NativeMethods.WM_RBUTTONDOWN => MouseButton.Right,
                NativeMethods.WM_MBUTTONDOWN => MouseButton.Middle,
                _ => null
            };
            if (btn is MouseButton b)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(data.pt.X, data.pt.Y);
                Application.Current?.Dispatcher.BeginInvoke(() => MouseClick?.Invoke(this, (pt, b)));
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Tạo chuỗi "Ctrl + Shift + S". Trả null nếu là phím bổ trợ đơn lẻ hoặc phím gõ thường (khi không hiện tất cả).</summary>
    private string? Describe(int vk)
    {
        // Bản thân phím bổ trợ: không hiện (sẽ hiện khi bấm kèm phím khác).
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C) return null;

        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);
        Key key = KeyInterop.KeyFromVirtualKey(vk);
        string name = KeyName(key);

        // Phím đặc biệt luôn hiện (kể cả khi bấm đơn lẻ). Space chỉ hiện khi đi kèm phím bổ trợ.
        bool special = key is (>= Key.F1 and <= Key.F24) or Key.Enter or Key.Escape or Key.Tab or Key.Back or Key.Delete
            or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PrintScreen;

        if (!(ctrl || alt || win || special || ShowAllKeys)) return null;

        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift && (ctrl || alt || win || special || name.Length > 1)) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(name);
        return string.Join(" + ", parts);
    }

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Space => "Space",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.PrintScreen => "PrtSc",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.Oem1 => ";",
        Key.Oem3 => "`",
        Key.Oem4 => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem7 => "'",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        _ => k.ToString()
    };

    public void Dispose() => Enable(false, false);
}
