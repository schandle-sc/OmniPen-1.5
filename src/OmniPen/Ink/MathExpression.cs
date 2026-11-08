using System.Globalization;

namespace OmniPen.Ink;

/// <summary>
/// Bộ phân tích & tính biểu thức toán một biến x cho Vẽ đồ thị (không phụ thuộc WPF – kiểm thử độc lập được).
/// Hỗ trợ: + − * / ^ (lũy thừa, kết hợp phải), dấu âm, ngoặc (), nhân ngầm (2x, 3(x+1), x sin x, (x+1)(x-1)),
/// hằng pi, e; hàm sin cos tan cot asin acos atan sqrt cbrt abs ln log log2 exp floor ceil sign;
/// tiền tố "y =" hoặc "f(x) =" được bỏ qua; dấu phẩy thập phân kiểu Việt Nam (1,5) được chấp nhận;
/// ký hiệu √ ² ³ π · × ÷ − cũng được.
/// </summary>
public sealed class MathExpression
{
    private readonly Func<double, double> _eval;
    public string Source { get; }

    private MathExpression(string source, Func<double, double> eval)
    {
        Source = source;
        _eval = eval;
    }

    public double Evaluate(double x) => _eval(x);

    /// <exception cref="FormatException">Biểu thức sai cú pháp (thông báo tiếng Việt).</exception>
    public static MathExpression Parse(string text)
    {
        string src = Normalize(text);
        var p = new Parser(src);
        Func<double, double> f = p.ParseExpression();
        p.ExpectEnd();
        return new MathExpression(text.Trim(), f);
    }

    public static bool TryParse(string text, out MathExpression? expr, out string? error)
    {
        try
        {
            expr = Parse(text);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            expr = null;
            error = ex.Message;
            return false;
        }
    }

    private static string Normalize(string text)
    {
        string s = text.Trim().ToLowerInvariant()
            .Replace('−', '-').Replace('·', '*').Replace('×', '*').Replace('÷', '/')
            .Replace("π", "pi").Replace("√", "sqrt").Replace("²", "^2").Replace("³", "^3");

        // Bỏ "y =" / "f(x) =" ở đầu.
        int eq = s.IndexOf('=');
        if (eq >= 0) s = s[(eq + 1)..];

        // Dấu phẩy thập phân (1,5) → dấu chấm; đồ thị một biến không dùng dấu phẩy cho mục đích khác.
        s = s.Replace(',', '.');
        if (string.IsNullOrWhiteSpace(s)) throw new FormatException("Chưa nhập biểu thức.");
        return s;
    }

    private sealed class Parser(string s)
    {
        private int _i;

        private static readonly Dictionary<string, Func<double, double>> Functions = new()
        {
            ["sin"] = Math.Sin, ["cos"] = Math.Cos, ["tan"] = Math.Tan, ["tg"] = Math.Tan,
            ["cot"] = v => 1 / Math.Tan(v), ["cotg"] = v => 1 / Math.Tan(v),
            ["asin"] = Math.Asin, ["acos"] = Math.Acos, ["atan"] = Math.Atan, ["arcsin"] = Math.Asin,
            ["arccos"] = Math.Acos, ["arctan"] = Math.Atan,
            ["sqrt"] = Math.Sqrt, ["cbrt"] = Math.Cbrt, ["abs"] = Math.Abs,
            ["ln"] = Math.Log, ["log"] = Math.Log10, ["lg"] = Math.Log10, ["log2"] = Math.Log2,
            ["exp"] = Math.Exp, ["floor"] = Math.Floor, ["ceil"] = Math.Ceiling, ["sign"] = v => Math.Sign(v)
        };

        private char Peek => _i < s.Length ? s[_i] : '\0';

        private void SkipSpaces()
        {
            while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++;
        }

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_i < s.Length) throw new FormatException($"Ký tự không hợp lệ tại vị trí {_i + 1}: '{s[_i]}'.");
        }

        // expr := term (('+'|'-') term)*
        public Func<double, double> ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Peek == '+') { _i++; var l = left; var r = ParseTerm(); left = x => l(x) + r(x); }
                else if (Peek == '-') { _i++; var l = left; var r = ParseTerm(); left = x => l(x) - r(x); }
                else return left;
            }
        }

        // term := unary (('*'|'/'| ngầm định) unary)*
        private Func<double, double> ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipSpaces();
                char c = Peek;
                if (c == '*') { _i++; var l = left; var r = ParseUnary(); left = x => l(x) * r(x); }
                else if (c == '/') { _i++; var l = left; var r = ParseUnary(); left = x => l(x) / r(x); }
                else if (char.IsLetter(c) || c == '(' || char.IsDigit(c) || c == '.')
                {
                    // Nhân ngầm: 2x, x(x+1), 3sin(x), (x+1)(x-1)
                    var l = left; var r = ParseUnary(); left = x => l(x) * r(x);
                }
                else return left;
            }
        }

        // unary := ('-'|'+') unary | power
        private Func<double, double> ParseUnary()
        {
            SkipSpaces();
            if (Peek == '-') { _i++; var u = ParseUnary(); return x => -u(x); }
            if (Peek == '+') { _i++; return ParseUnary(); }
            return ParsePower();
        }

        // power := primary ('^' unary)?   (kết hợp phải; -x^2 = -(x^2) vì dấu âm xử lý ở unary)
        private Func<double, double> ParsePower()
        {
            var b = ParsePrimary();
            SkipSpaces();
            if (Peek == '^')
            {
                _i++;
                var e = ParseUnary();
                return x => Pow(b(x), e(x));
            }
            return b;
        }

        /// <summary>Lũy thừa: cơ số âm với số mũ dạng 1/n lẻ (căn bậc lẻ) vẫn cho kết quả thực.</summary>
        private static double Pow(double b, double e)
        {
            if (b < 0 && Math.Abs(e - Math.Round(e)) > 1e-12)
            {
                double inv = 1 / e;
                if (Math.Abs(inv - Math.Round(inv)) < 1e-9 && ((long)Math.Round(inv)) % 2 != 0)
                    return -Math.Pow(-b, e);
            }
            return Math.Pow(b, e);
        }

        private Func<double, double> ParsePrimary()
        {
            SkipSpaces();
            char c = Peek;

            if (c == '(' || c == '[')
            {
                char close = c == '(' ? ')' : ']';
                _i++;
                var inner = ParseExpression();
                SkipSpaces();
                if (Peek != close) throw new FormatException("Thiếu dấu đóng ngoặc.");
                _i++;
                return inner;
            }

            if (c == '|')
            {
                _i++;
                var inner = ParseExpression();
                SkipSpaces();
                if (Peek != '|') throw new FormatException("Thiếu dấu | đóng trị tuyệt đối.");
                _i++;
                return x => Math.Abs(inner(x));
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
                if (!double.TryParse(s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new FormatException($"Số không hợp lệ: {s[start.._i]}");
                return _ => v;
            }

            if (char.IsLetter(c))
            {
                int start = _i;
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]))) _i++;
                string name = s[start.._i];

                // Tách tên dài nhất là hàm/hằng đã biết, phần còn lại trả về để nhân ngầm (VD "xsin" → x · sin).
                foreach (int len in Enumerable.Range(1, name.Length).Reverse())
                {
                    string head = name[..len];
                    if (Functions.ContainsKey(head) || head is "pi" or "e" or "x")
                    {
                        _i = start + len;
                        return Named(head);
                    }
                }
                throw new FormatException($"Không hiểu \"{name}\". Dùng biến x và các hàm sin, cos, tan, sqrt, ln, log, abs, exp…");
            }

            if (c == '\0') throw new FormatException("Biểu thức kết thúc đột ngột.");
            throw new FormatException($"Ký tự không hợp lệ: '{c}'.");
        }

        private Func<double, double> Named(string name)
        {
            switch (name)
            {
                case "x": return x => x;
                case "pi": return _ => Math.PI;
                case "e": return _ => Math.E;
            }

            Func<double, double> fn = Functions[name];
            SkipSpaces();
            // Cho phép "sin x", "sin 2x" (không ngoặc): lấy một thừa số đơn.
            Func<double, double> arg = Peek == '(' || Peek == '[' || Peek == '|' ? ParsePrimary() : ParseUnaryNoImplicit();
            // sin(x)^2 → (sin x)^2
            SkipSpaces();
            if (Peek == '^')
            {
                _i++;
                var e = ParseUnary();
                return x => Pow(fn(arg(x)), e(x));
            }
            return x => fn(arg(x));
        }

        /// <summary>Đối số hàm không ngoặc: "sin 2x" = sin(2x) – lấy số + biến liền kề, như cách viết trong SGK.</summary>
        private Func<double, double> ParseUnaryNoImplicit()
        {
            SkipSpaces();
            if (Peek == '-') { _i++; var u = ParseUnaryNoImplicit(); return x => -u(x); }
            var first = ParsePower();
            SkipSpaces();
            // "2x", "2pi" → nhân liền
            if (char.IsLetter(Peek) && !StartsWithFunction())
            {
                var second = ParsePower();
                return x => first(x) * second(x);
            }
            return first;
        }

        private bool StartsWithFunction()
        {
            foreach (var name in Functions.Keys)
                if (string.CompareOrdinal(s, _i, name, 0, name.Length) == 0) return true;
            return false;
        }
    }
}
