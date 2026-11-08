using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OmniPen.Ink;

/// <summary>Thiết lập một hệ trục Oxy + các hàm số cần vẽ.</summary>
public sealed class GraphSpec
{
    public List<(MathExpression Expr, Color Color)> Functions { get; } = new();
    public double XMin { get; set; } = -6;
    public double XMax { get; set; } = 6;
    public double YMin { get; set; } = -4;
    public double YMax { get; set; } = 4;
    public bool AutoY { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public Size Size { get; set; } = new(640, 440);
    public double CurveWidth { get; set; } = 3;
}

/// <summary>Kết quả: các đường gấp khúc vector (thành nét mực) + ảnh nhãn chữ trong suốt phủ lên trên.</summary>
public sealed record GraphResult(
    List<(List<Point> Points, Color Color, double Width)> Lines,
    BitmapSource Labels,
    Size Size);

/// <summary>
/// Dựng hệ trục tọa độ Oxy và đồ thị hàm số. Trục, lưới và đường cong đều là NÉT MỰC vector
/// (phóng to vẫn mịn, tẩy/hoàn tác/xuất SVG như nét tay); chỉ chữ số trên trục là ảnh.
/// Đường cong được lấy mẫu dày (2 điểm/DIP) và tự ngắt tại tiệm cận / điểm không xác định (1/x, tan x, ln x…).
/// </summary>
public static class GraphBuilder
{
    private static readonly Color AxisColor = Color.FromRgb(0x1F, 0x29, 0x37);
    private static readonly Color GridColor = Color.FromRgb(0xCB, 0xD5, 0xE1);

    public static GraphResult Build(GraphSpec spec)
    {
        if (spec.XMax - spec.XMin < 1e-9) throw new ArgumentException("Khoảng x không hợp lệ (x max phải lớn hơn x min).");
        if (spec.AutoY && spec.Functions.Count > 0) AutoRangeY(spec);
        if (spec.YMax - spec.YMin < 1e-9) throw new ArgumentException("Khoảng y không hợp lệ (y max phải lớn hơn y min).");

        double W = spec.Size.Width, H = spec.Size.Height;
        double sx = W / (spec.XMax - spec.XMin), sy = H / (spec.YMax - spec.YMin);
        Point P(double x, double y) => new((x - spec.XMin) * sx, (spec.YMax - y) * sy);

        var lines = new List<(List<Point>, Color, double)>();

        double stepX = NiceStep(spec.XMax - spec.XMin), stepY = NiceStep(spec.YMax - spec.YMin);

        // ---- Lưới
        if (spec.ShowGrid)
        {
            foreach (double gx in Ticks(spec.XMin, spec.XMax, stepX))
                lines.Add((new List<Point> { P(gx, spec.YMin), P(gx, spec.YMax) }, GridColor, 1));
            foreach (double gy in Ticks(spec.YMin, spec.YMax, stepY))
                lines.Add((new List<Point> { P(spec.XMin, gy), P(spec.XMax, gy) }, GridColor, 1));
        }

        // ---- Trục (đặt tại x=0 / y=0 nếu nằm trong khung, không thì ở mép)
        double ax = Math.Clamp(0, spec.XMin, spec.XMax), ay = Math.Clamp(0, spec.YMin, spec.YMax);
        Point xa = P(spec.XMin, ay), xb = P(spec.XMax, ay), ya = P(ax, spec.YMin), yb = P(ax, spec.YMax);
        lines.Add((new List<Point> { xa, xb }, AxisColor, 2));
        lines.Add((new List<Point> { ya, yb }, AxisColor, 2));
        lines.Add((new List<Point> { new(xb.X - 12, xb.Y - 6), xb, new(xb.X - 12, xb.Y + 6) }, AxisColor, 2));
        lines.Add((new List<Point> { new(yb.X - 6, yb.Y + 12), yb, new(yb.X + 6, yb.Y + 12) }, AxisColor, 2));

        // Vạch chia
        foreach (double t in Ticks(spec.XMin, spec.XMax, stepX))
            if (Math.Abs(t - ax) > stepX / 2 && P(t, 0).X < W - 14)
                lines.Add((new List<Point> { new(P(t, ay).X, P(t, ay).Y - 4), new(P(t, ay).X, P(t, ay).Y + 4) }, AxisColor, 1.5));
        foreach (double t in Ticks(spec.YMin, spec.YMax, stepY))
            if (Math.Abs(t - ay) > stepY / 2 && P(0, t).Y > 14)
                lines.Add((new List<Point> { new(P(ax, t).X - 4, P(ax, t).Y), new(P(ax, t).X + 4, P(ax, t).Y) }, AxisColor, 1.5));

        // ---- Đồ thị
        foreach (var (expr, color) in spec.Functions)
            foreach (var seg in SampleCurve(expr, spec, P, W))
                lines.Add((seg, color, spec.CurveWidth));

        BitmapSource labels = RenderLabels(spec, P, stepX, stepY, ax, ay, W, H);
        return new GraphResult(lines, labels, spec.Size);
    }

    /// <summary>Lấy mẫu đường cong, cắt theo khung y, ngắt tại chỗ không xác định hoặc nhảy vọt (tiệm cận đứng).</summary>
    private static IEnumerable<List<Point>> SampleCurve(MathExpression expr, GraphSpec spec, Func<double, double, Point> P, double W)
    {
        int n = Math.Max(200, (int)(W * 2));
        double range = spec.YMax - spec.YMin;
        double lo = spec.YMin, hi = spec.YMax;
        var cur = new List<Point>();
        double? prevX = null, prevY = null;

        for (int i = 0; i <= n; i++)
        {
            double x = spec.XMin + (spec.XMax - spec.XMin) * i / n;
            double y;
            try { y = expr.Evaluate(x); } catch { y = double.NaN; }

            bool valid = double.IsFinite(y);
            bool jump = valid && prevY is double py && Math.Abs(y - py) > range * 2.5
                        && Math.Sign(y - (lo + hi) / 2) != Math.Sign(py - (lo + hi) / 2);

            if (!valid || jump)
            {
                if (cur.Count > 1) yield return cur;
                cur = new List<Point>();
                prevX = valid ? x : null;
                prevY = valid ? y : null;
                if (valid && y >= lo && y <= hi) cur.Add(P(x, y));
                continue;
            }

            bool inside = y >= lo && y <= hi;
            if (prevY is double pY && prevX is double pX)
            {
                bool prevInside = pY >= lo && pY <= hi;
                if (inside && !prevInside) cur.Add(P(Lerp(pX, x, pY, y, pY > hi ? hi : lo), pY > hi ? hi : lo));
                if (!inside && prevInside)
                {
                    double bound = y > hi ? hi : lo;
                    cur.Add(P(Lerp(pX, x, pY, y, bound), bound));
                    if (cur.Count > 1) yield return cur;
                    cur = new List<Point>();
                }
            }
            if (inside) cur.Add(P(x, y));
            prevX = x;
            prevY = y;
        }
        if (cur.Count > 1) yield return cur;
    }

    private static double Lerp(double x0, double x1, double y0, double y1, double yTarget) =>
        Math.Abs(y1 - y0) < 1e-12 ? x0 : x0 + (x1 - x0) * (yTarget - y0) / (y1 - y0);

    /// <summary>Tự chọn khoảng y vừa đẹp: bỏ 5% giá trị cực đoan hai đầu (tiệm cận) rồi nới thêm 15%.</summary>
    private static void AutoRangeY(GraphSpec spec)
    {
        var ys = new List<double>();
        foreach (var (expr, _) in spec.Functions)
            for (int i = 0; i <= 400; i++)
            {
                double x = spec.XMin + (spec.XMax - spec.XMin) * i / 400;
                double y;
                try { y = expr.Evaluate(x); } catch { continue; }
                if (double.IsFinite(y)) ys.Add(y);
            }
        if (ys.Count < 2) return;
        ys.Sort();
        double lo = ys[(int)(ys.Count * 0.05)], hi = ys[(int)(ys.Count * 0.95) - 1];
        lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); // luôn thấy trục Ox
        if (hi - lo < 1e-6) { lo -= 1; hi += 1; }
        double pad = (hi - lo) * 0.15;
        spec.YMin = Math.Floor(lo - pad);
        spec.YMax = Math.Ceiling(hi + pad);
    }

    /// <summary>Bước chia "đẹp" 1-2-5 × 10ⁿ cho khoảng 8–20 vạch.</summary>
    public static double NiceStep(double range)
    {
        double raw = range / 12;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        double nice = n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10;
        return nice * mag;
    }

    private static IEnumerable<double> Ticks(double min, double max, double step)
    {
        for (double t = Math.Ceiling(min / step) * step; t <= max + step * 1e-6; t += step)
            yield return Math.Abs(t) < step * 1e-6 ? 0 : t;
    }

    private static BitmapSource RenderLabels(GraphSpec spec, Func<double, double, Point> P,
        double stepX, double stepY, double ax, double ay, double W, double H)
    {
        const double scale = 2;
        var dv = new DrawingVisual();
        var tf = new Typeface("Segoe UI");
        var tfItalic = new Typeface(new FontFamily("Cambria Math, Times New Roman"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        var brush = new SolidColorBrush(AxisColor);
        var ci = CultureInfo.GetCultureInfo("vi-VN");

        FormattedText Text(string s, double size, Brush b, Typeface? t = null) =>
            new(s, ci, FlowDirection.LeftToRight, t ?? tf, size, b, 1.0);

        using (DrawingContext dc = dv.RenderOpen())
        {
            foreach (double t in Ticks(spec.XMin, spec.XMax, stepX))
            {
                if (Math.Abs(t - ax) <= stepX / 2) continue;
                Point p = P(t, ay);
                if (p.X > W - 14) continue;
                var ft = Text(Format(t), 12, brush);
                dc.DrawText(ft, new Point(p.X - ft.Width / 2, Math.Min(H - ft.Height, p.Y + 6)));
            }
            foreach (double t in Ticks(spec.YMin, spec.YMax, stepY))
            {
                if (Math.Abs(t - ay) <= stepY / 2) continue;
                Point p = P(ax, t);
                if (p.Y < 14) continue;
                var ft = Text(Format(t), 12, brush);
                dc.DrawText(ft, new Point(Math.Max(0, p.X - ft.Width - 7), p.Y - ft.Height / 2));
            }

            Point o = P(ax, ay);
            if (ax == 0 && ay == 0)
            {
                var ftO = Text("O", 14, brush, tfItalic);
                dc.DrawText(ftO, new Point(o.X - ftO.Width - 4, o.Y + 2));
            }
            var ftx = Text("x", 18, brush, tfItalic);
            dc.DrawText(ftx, new Point(W - ftx.Width - 2, o.Y + 6));
            var fty = Text("y", 18, brush, tfItalic);
            dc.DrawText(fty, new Point(P(ax, 0).X + 10, 0));

            // Chú thích hàm số (góc trên-trái).
            double ly = 4;
            foreach (var (expr, color) in spec.Functions)
            {
                string label = expr.Source.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase) ? expr.Source : "y = " + expr.Source;
                var ft = Text(label, 15, new SolidColorBrush(color), tfItalic);
                var bg = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
                dc.DrawRoundedRectangle(bg, null, new Rect(4, ly - 1, ft.Width + 12, ft.Height + 2), 4, 4);
                dc.DrawText(ft, new Point(10, ly));
                ly += ft.Height + 4;
            }
        }

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(W * scale), (int)Math.Ceiling(H * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static string Format(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v).ToString(CultureInfo.InvariantCulture)
                                            : v.ToString("0.##", CultureInfo.GetCultureInfo("vi-VN"));

    /// <summary>Ảnh xem trước (nền trắng) cho hộp thoại Vẽ đồ thị.</summary>
    public static BitmapSource RenderPreview(GraphResult g)
    {
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(g.Size));
            foreach (var (pts, color, width) in g.Lines)
            {
                var pen = new Pen(new SolidColorBrush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(pts[0], false, false);
                    ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
                }
                dc.DrawGeometry(null, pen, geo);
            }
            dc.DrawImage(g.Labels, new Rect(g.Size));
        }
        var rtb = new RenderTargetBitmap((int)g.Size.Width, (int)g.Size.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
