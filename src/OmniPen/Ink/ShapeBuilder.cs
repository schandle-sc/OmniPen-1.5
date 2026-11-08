using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace OmniPen.Ink;

public enum ShapeKind
{
    Line,
    Arrow,
    DoubleArrow,
    Rectangle,
    Ellipse,
    Triangle,
    RightTriangle,
    Cube,
    Table
}

public static class ShapeKindExtensions
{
    public static string DisplayName(this ShapeKind k) => k switch
    {
        ShapeKind.Line => "Đường thẳng",
        ShapeKind.Arrow => "Mũi tên một chiều",
        ShapeKind.DoubleArrow => "Mũi tên hai chiều",
        ShapeKind.Rectangle => "Hình chữ nhật (giữ Shift: hình vuông)",
        ShapeKind.Ellipse => "Hình elip (giữ Shift: hình tròn)",
        ShapeKind.Triangle => "Tam giác cân",
        ShapeKind.RightTriangle => "Tam giác vuông",
        ShapeKind.Cube => "Hình hộp (khối)",
        ShapeKind.Table => "Bảng",
        _ => k.ToString()
    };

    /// <summary>Ký hiệu Unicode (font Segoe UI Symbol) cho nút Hình.</summary>
    public static string Glyph(this ShapeKind k) => k switch
    {
        ShapeKind.Line => "\u2571",          // ╱
        ShapeKind.Arrow => "\u2192",         // →
        ShapeKind.DoubleArrow => "\u2194",   // ↔
        ShapeKind.Rectangle => "\u25AD",     // ▭
        ShapeKind.Ellipse => "\u25EF",       // ◯
        ShapeKind.Triangle => "\u25B3",      // △
        ShapeKind.RightTriangle => "\u25FA", // ◺
        ShapeKind.Cube => "\u2752",          // ❒
        ShapeKind.Table => "\u25A6",         // ▦
        _ => "?"
    };
}

/// <summary>
/// Dựng hình học cho công cụ Hình / Compa dưới dạng danh sách đường gấp khúc (polyline).
/// Mỗi polyline sẽ thành MỘT nét mực (Stroke) → tẩy, hoàn tác, chọn, xuất file hoạt động như nét tay.
/// </summary>
public static class ShapeBuilder
{
    public static List<List<Point>> Build(ShapeKind kind, Point a, Point b, bool constrain,
        double penWidth, int tableRows = 3, int tableCols = 3)
    {
        if (constrain) b = Constrain(kind, a, b);
        double x1 = Math.Min(a.X, b.X), y1 = Math.Min(a.Y, b.Y);
        double x2 = Math.Max(a.X, b.X), y2 = Math.Max(a.Y, b.Y);

        var result = new List<List<Point>>();
        switch (kind)
        {
            case ShapeKind.Line:
                result.Add(new() { a, b });
                break;

            case ShapeKind.Arrow:
                result.Add(new() { a, b });
                result.Add(ArrowHead(a, b, penWidth));
                break;

            case ShapeKind.DoubleArrow:
                result.Add(new() { a, b });
                result.Add(ArrowHead(a, b, penWidth));
                result.Add(ArrowHead(b, a, penWidth));
                break;

            case ShapeKind.Rectangle:
                result.Add(new() { new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2), new(x1, y1) });
                break;

            case ShapeKind.Ellipse:
                result.Add(Ellipse(new Point((x1 + x2) / 2, (y1 + y2) / 2), (x2 - x1) / 2, (y2 - y1) / 2));
                break;

            case ShapeKind.Triangle:
                result.Add(new() { new((x1 + x2) / 2, y1), new(x2, y2), new(x1, y2), new((x1 + x2) / 2, y1) });
                break;

            case ShapeKind.RightTriangle:
                result.Add(new() { new(x1, y1), new(x1, y2), new(x2, y2), new(x1, y1) });
                // Ký hiệu góc vuông.
                double m = Math.Min(14, Math.Min(x2 - x1, y2 - y1) / 4);
                result.Add(new() { new(x1, y2 - m), new(x1 + m, y2 - m), new(x1 + m, y2) });
                break;

            case ShapeKind.Cube:
                result.AddRange(Cube(x1, y1, x2, y2, penWidth));
                break;

            case ShapeKind.Table:
                tableRows = Math.Clamp(tableRows, 1, 30);
                tableCols = Math.Clamp(tableCols, 1, 30);
                for (int r = 0; r <= tableRows; r++)
                {
                    double y = y1 + (y2 - y1) * r / tableRows;
                    result.Add(new() { new(x1, y), new(x2, y) });
                }
                for (int c = 0; c <= tableCols; c++)
                {
                    double x = x1 + (x2 - x1) * c / tableCols;
                    result.Add(new() { new(x, y1), new(x, y2) });
                }
                break;
        }
        return result;
    }

    /// <summary>
    /// Giữ Shift: đường thẳng bắt góc bội 15°, hình chữ nhật/elip/hộp/bảng thành vuông/tròn.
    /// </summary>
    private static Point Constrain(ShapeKind kind, Point a, Point b)
    {
        Vector v = b - a;
        if (kind is ShapeKind.Line or ShapeKind.Arrow or ShapeKind.DoubleArrow)
        {
            double len = v.Length;
            double angle = Math.Atan2(v.Y, v.X);
            double step = Math.PI / 12; // 15°
            angle = Math.Round(angle / step) * step;
            return a + new Vector(Math.Cos(angle) * len, Math.Sin(angle) * len);
        }
        double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
        return new Point(a.X + Math.Sign(v.X == 0 ? 1 : v.X) * side, a.Y + Math.Sign(v.Y == 0 ? 1 : v.Y) * side);
    }

    private static List<Point> ArrowHead(Point from, Point tip, double penWidth)
    {
        Vector dir = tip - from;
        if (dir.Length < 0.01) return new() { tip };
        dir.Normalize();
        double len = Math.Max(14, penWidth * 4.5);
        double ang = 25 * Math.PI / 180;

        Vector left = Rotate(-dir, ang) * len;
        Vector right = Rotate(-dir, -ang) * len;
        return new() { tip + left, tip, tip + right };
    }

    private static Vector Rotate(Vector v, double rad) =>
        new(v.X * Math.Cos(rad) - v.Y * Math.Sin(rad), v.X * Math.Sin(rad) + v.Y * Math.Cos(rad));

    public static List<Point> Ellipse(Point c, double rx, double ry, int segments = 96)
    {
        var pts = new List<Point>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double t = 2 * Math.PI * i / segments;
            pts.Add(new Point(c.X + rx * Math.Cos(t), c.Y + ry * Math.Sin(t)));
        }
        return pts;
    }

    /// <summary>Cung tròn tâm c, bán kính r, từ góc start quét một góc sweep (radian, có dấu).</summary>
    public static List<Point> Arc(Point c, double r, double start, double sweep)
    {
        int segments = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (2 * Math.PI / 180))); // mỗi 2°
        var pts = new List<Point>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double t = start + sweep * i / segments;
            pts.Add(new Point(c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t)));
        }
        return pts;
    }

    /// <summary>
    /// Hình hộp phối cảnh: mặt trước = khung kéo, mặt sau lệch lên-phải.
    /// 3 cạnh khuất vẽ nét đứt (như hình học không gian trong SGK).
    /// </summary>
    private static IEnumerable<List<Point>> Cube(double x1, double y1, double x2, double y2, double penWidth)
    {
        double w = x2 - x1, h = y2 - y1;
        double d = Math.Min(w, h) * 0.35;
        // Mặt trước thu nhỏ để cả khối nằm trong khung kéo.
        double fx1 = x1, fy1 = y1 + d, fx2 = x2 - d, fy2 = y2;
        Vector o = new(d, -d);

        Point A = new(fx1, fy2), B = new(fx2, fy2), C = new(fx2, fy1), D = new(fx1, fy1); // mặt trước
        Point A2 = A + o, B2 = B + o, C2 = C + o, D2 = D + o;                         // mặt sau

        yield return new() { A, B, C, D, A };   // mặt trước
        yield return new() { D, D2, C2, C };    // cạnh trên
        yield return new() { C2, B2, B };       // cạnh phải

        double dash = Math.Max(6, penWidth * 2.5), gap = Math.Max(5, penWidth * 2);
        foreach (var seg in Dashed(A, A2, dash, gap)) yield return seg;   // cạnh khuất
        foreach (var seg in Dashed(A2, B2, dash, gap)) yield return seg;
        foreach (var seg in Dashed(A2, D2, dash, gap)) yield return seg;
    }

    private static IEnumerable<List<Point>> Dashed(Point p, Point q, double dash, double gap)
    {
        Vector v = q - p;
        double len = v.Length;
        if (len < 0.01) yield break;
        v.Normalize();
        for (double s = 0; s < len; s += dash + gap)
        {
            double e = Math.Min(len, s + dash);
            yield return new() { p + v * s, p + v * e };
        }
    }

    /// <summary>Chuyển polyline thành nét mực. Hình học: không làm mượt, không theo lực nhấn.</summary>
    public static Stroke ToStroke(IReadOnlyList<Point> pts, DrawingAttributes baseAttributes)
    {
        var da = baseAttributes.Clone();
        da.FitToCurve = false;
        da.IgnorePressure = true;
        da.StylusTip = StylusTip.Ellipse;
        IEnumerable<Point> list = pts.Count == 1 ? new[] { pts[0], pts[0] } : pts;
        return new Stroke(new StylusPointCollection(list), da);
    }

    /// <summary>Hình học xem trước (vẽ trong lớp preview khi đang kéo).</summary>
    public static Geometry ToGeometry(IEnumerable<IReadOnlyList<Point>> polylines)
    {
        var g = new StreamGeometry();
        using (StreamGeometryContext ctx = g.Open())
        {
            foreach (var pl in polylines)
            {
                if (pl.Count == 0) continue;
                ctx.BeginFigure(pl[0], false, false);
                ctx.PolyLineTo(pl.Skip(1).ToList(), true, true);
            }
        }
        g.Freeze();
        return g;
    }
}
