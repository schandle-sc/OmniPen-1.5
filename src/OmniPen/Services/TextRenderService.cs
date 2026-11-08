using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OmniPen.Services;

/// <summary>Dựng văn bản / khối code thành ảnh PNG nét (2×) để chèn lên trang.</summary>
public static class TextRenderService
{
    private const double Scale = 2;

    /// <summary>Văn bản nhận dạng từ chữ viết tay: kiểu chữ tay (Segoe Print / Ink Free) hoặc đánh máy (Segoe UI).</summary>
    public static (byte[] Png, Size Size) RenderText(string text, bool handwritingFont, Color color, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(handwritingFont ? "Segoe Print, Ink Free, Comic Sans MS" : "Segoe UI"),
            FontSize = Math.Clamp(fontSize, 12, 120),
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(2)
        };
        return Render(tb);
    }

    // ------------------------------------------------------------------ Code

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C / C++ / C# / Java / JS / TS / Go / Rust / PHP
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","class","const","continue","def",
        "default","delegate","do","double","else","enum","event","explicit","export","extends","extern","false","final",
        "finally","float","fn","for","foreach","from","func","function","get","goto","if","implements","import","in","include",
        "int","interface","internal","is","let","lock","long","match","mod","namespace","new","null","nullptr","object",
        "operator","out","override","package","private","protected","pub","public","readonly","ref","return","sealed","set",
        "short","sizeof","static","string","struct","super","switch","this","throw","true","try","typedef","typeof","uint",
        "unsigned","use","using","var","virtual","void","volatile","while","yield","echo","require","impl","mut","template",
        "typename","std","cout","cin","endl","printf","scanf","println","System","console",
        // Python / Pascal
        "and","or","not","elif","lambda","pass","raise","with","None","True","False","global","nonlocal","del","assert",
        "print","range","len","self","begin","end","then","procedure","program","integer","real","writeln","readln","until","repeat","of"
    };

    // Thứ tự nhóm quan trọng: chú thích → chuỗi → số → từ.
    private static readonly Regex Token = new(
        @"(?<c>//.*|#(?!include|define|pragma).*|/\*[\s\S]*?\*/|--.*)|(?<s>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`[^`]*`)|(?<n>\b\d+(?:\.\d+)?[fFdDmMlLuU]?\b|0x[0-9a-fA-F]+)|(?<w>[A-Za-z_#][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // Bảng màu Dracula
    private static readonly Brush Bg = Frozen(0x28, 0x2A, 0x36), Fg = Frozen(0xF8, 0xF8, 0xF2), Comment = Frozen(0x62, 0x72, 0xA4),
        Str = Frozen(0xF1, 0xFA, 0x8C), Kw = Frozen(0xFF, 0x79, 0xC6), Num = Frozen(0xBD, 0x93, 0xF9),
        Fn = Frozen(0x50, 0xFA, 0x7B), Type = Frozen(0x8B, 0xE9, 0xFD), LineNo = Frozen(0x58, 0x5B, 0x70);

    /// <summary>Khối code nổi kiểu Dracula: thanh tiêu đề, số dòng, tô màu cú pháp (C/C++/C#/Java/JS/Python/Pascal…).</summary>
    public static (byte[] Png, Size Size) RenderCode(string code)
    {
        var lines = code.Replace("\r\n", "\n").Replace("\t", "    ").TrimEnd('\n').Split('\n')
            .Take(80).Select(l => l.Length > 140 ? l[..140] + "…" : l).ToList();

        // Bỏ thụt lề chung (code copy từ giữa một hàm).
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        lines = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();

        var font = new FontFamily("Cascadia Code, Cascadia Mono, Consolas, Courier New");
        var body = new StackPanel();
        int width = lines.Count.ToString(CultureInfo.InvariantCulture).Length;

        for (int i = 0; i < lines.Count; i++)
        {
            var tb = new TextBlock { FontFamily = font, FontSize = 15, Foreground = Fg, LineHeight = 22 };
            tb.Inlines.Add(new Run((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width) + "  ") { Foreground = LineNo });
            AddHighlighted(tb.Inlines, lines[i]);
            body.Children.Add(tb);
        }

        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var c in new[] { Color.FromRgb(0xFF, 0x5F, 0x57), Color.FromRgb(0xFE, 0xBC, 0x2E), Color.FromRgb(0x28, 0xC8, 0x40) })
            dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 7, 0) });

        var root = new Border
        {
            Background = Bg,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 20, 14),
            BorderBrush = Frozen(0x44, 0x47, 0x5A),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { dots, body } }
        };
        return Render(root);
    }

    private static void AddHighlighted(InlineCollection inlines, string line)
    {
        int pos = 0;
        foreach (Match m in Token.Matches(line))
        {
            if (m.Index > pos) inlines.Add(new Run(line[pos..m.Index]));
            Brush brush = Fg;
            if (m.Groups["c"].Success) brush = Comment;
            else if (m.Groups["s"].Success) brush = Str;
            else if (m.Groups["n"].Success) brush = Num;
            else if (m.Groups["w"].Success)
            {
                string w = m.Value;
                int after = m.Index + m.Length;
                while (after < line.Length && line[after] == ' ') after++;
                if (Keywords.Contains(w) || w.StartsWith('#')) brush = Kw;
                else if (after < line.Length && line[after] == '(') brush = Fn;
                else if (char.IsUpper(w[0])) brush = Type;
            }
            inlines.Add(new Run(m.Value) { Foreground = brush });
            pos = m.Index + m.Length;
        }
        if (pos < line.Length) inlines.Add(new Run(line[pos..]));
    }

    private static (byte[] Png, Size Size) Render(FrameworkElement el)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = el.DesiredSize;
        el.Arrange(new Rect(size));
        el.UpdateLayout();

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * Scale)), Math.Max(1, (int)Math.Ceiling(size.Height * Scale)),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        rtb.Render(el);
        rtb.Freeze();
        return (ExportService.ToPng(rtb), size);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
