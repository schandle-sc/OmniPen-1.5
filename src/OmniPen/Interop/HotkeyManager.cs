using System.Windows.Input;
using System.Windows.Interop;

namespace OmniPen.Interop;

/// <summary>
/// Một tổ hợp phím tắt, VD "Ctrl+Shift+D".
/// Lưu ý: giá trị enum ModifierKeys của WPF (Alt=1, Control=2, Shift=4, Windows=8)
/// TRÙNG KHỚP với MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN của Win32 → ép kiểu trực tiếp được.
/// </summary>
public sealed record Hotkey(ModifierKeys Modifiers, Key Key)
{
    public uint Win32Modifiers => (uint)Modifiers;
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToText(Key));
        return string.Join("+", parts);
    }

    private static string KeyToText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString()
    };

    /// <summary>Phân tích chuỗi "Ctrl+Shift+D". Trả null nếu không hợp lệ.</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        ModifierKeys mods = ModifierKeys.None;
        Key key = Key.None;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win": case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0]))
                        key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key))
                        return null;
                    break;
            }
        }

        // Bắt buộc có ít nhất 1 phím bổ trợ để không "nuốt" phím gõ thường của toàn hệ thống.
        if (key == Key.None || mods == ModifierKeys.None) return null;
        return new Hotkey(mods, key);
    }
}

/// <summary>
/// Quản lý phím tắt TOÀN CỤC bằng RegisterHotKey.
///
/// Vì sao dùng message-only window riêng (HWND_MESSAGE) thay vì HWND của Canvas/Toolbar?
///   - Hotkey phải hoạt động cả khi toolbar bị ẩn xuống khay hệ thống.
///   - Cửa sổ message-only không bao giờ hiển thị, không ảnh hưởng z-order/focus, sống suốt vòng đời app.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x0100;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("OmniPenHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = Win32Helper.HWND_MESSAGE
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Đăng ký phím tắt. Trả về false nếu tổ hợp đã bị ứng dụng khác chiếm
    /// (RegisterHotKey chỉ cho MỘT process giữ một tổ hợp).
    /// </summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        int id = _nextId++;
        bool ok = Win32Helper.RegisterHotKey(_source.Handle, id,
            hotkey.Win32Modifiers | Win32Helper.MOD_NOREPEAT, hotkey.VirtualKey);
        if (ok) _actions[id] = action;
        return ok;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys)
            Win32Helper.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
