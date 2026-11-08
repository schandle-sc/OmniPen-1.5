using System.Windows;

namespace OmniPen.Ink;

/// <summary>
/// Nhận dạng nét vẽ tay thành hình chuẩn ("búng" hình): đoạn thẳng, đường tròn, elip, tam giác, tứ giác,
/// đa giác tới 6 cạnh, đường gấp khúc mở. Trả về các polyline thay thế, hoặc null nếu không chắc chắn.
///
/// Thuật toán:
///   1) Đoạn thẳng: độ dài dây cung / độ dài nét ≥ 0.93 → nối đầu-cuối (bắt góc 0°/45°/90° nếu lệch &lt; 6°).
///   2) Nét kín (đầu gần cuối): đơn giản hóa bằng Ramer–Douglas–Peucker → số đỉnh.
///      3–6 đỉnh và sai số nhỏ → đa giác (tứ giác gần vuông góc → hình chữ nhật thẳng hàng).
///      Ngược lại thử tròn (độ lệch bán kính &lt; 14%) rồi elip (theo khung bao).
///   3) Nét hở 2–4 đỉnh → đường gấp khúc thẳng.
/// </summary>
public static class ShapeRecognizer
{
    public static List<List<Point>>? Recognize(IReadOnlyList<Point> raw)
    {
        if (raw.Count < 4) return null;
        var pts = Dedup(raw);
        if (pts.Count < 3) return null;

        double length = PathLength(pts);
        Rect box = Bounds(pts);
        double diag = Math.Sqrt(box.Width * box.Width + box.Height * box.Height);
        if (length < 20 || diag < 12) return null;

        Point a = pts[0], b = pts[^1];
        double chord = (b - a).Length;

        // 1) Đoạn thẳng
        if (chord / length >= 0.93)
            return new() { SnapLine(a, b) };

        bool closed = chord < Math.Max(18, diag * 0.22);
        double eps = Math.Max(4, diag * 0.06);
        var simple = Rdp(pts, eps);

        if (closed)
        {
            // Bỏ đỉnh cuối trùng đỉnh đầu.
            if (simple.Count > 2 && (simple[^1] - simple[0]).Length < Math.Max(18, diag * 0.22)) simple.RemoveAt(simple.Count - 1);
            simple = MergeClose(simple, diag * 0.12);
            int n = simple.Count;

            double circleScore = CircleDeviation(pts, out Point c, out double r);
            Point cc = c; double rr = r;
            double circleAbsErr = pts.Average(p => Math.Abs((p - cc).Length - rr));
            double polyErr = n is >= 3 and <= 6 ? PolygonError(pts, simple) : double.MaxValue;

            // Đa giác khi: ít đỉnh, bám sát các cạnh, và khớp TỐT HƠN HẲN đường tròn
            // (ngũ/lục giác đều rất "tròn" nên chỉ so độ tròn là không đủ).
            if (polyErr < diag * 0.07 && polyErr < circleAbsErr * 0.6)
            {
                if (n == 4 && IsRectangular(simple)) return new() { Rectangle(simple) };
                var poly = new List<Point>(simple) { simple[0] };
                return new() { poly };
            }

            if (circleScore < 0.14)
                return new() { ShapeBuilder.Ellipse(c, r, r) };

            if (EllipseDeviation(pts, box) < 0.16)
                return new() { ShapeBuilder.Ellipse(new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2) };

            return null;
        }

        // 3) Gấp khúc mở
        // Nét hở: chỉ nắn khi các đoạn rất thẳng (góc gãy rõ) – tránh biến chữ viết / đường cong thành gấp khúc.
        if (simple.Count is >= 3 and <= 4 && PolygonError(pts, simple, closed: false) < diag * 0.022)
            return new() { new List<Point>(simple) };

        return null;
    }

    // ---------------------------------------------------------------- hình chuẩn

    private static List<Point> SnapLine(Point a, Point b)
    {
        Vector v = b - a;
        double ang = Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        double snapped = Math.Round(ang / 45) * 45;
        if (Math.Abs(ang - snapped) < 6)
        {
            double len = v.Length, rad = snapped * Math.PI / 180;
            b = a + new Vector(Math.Cos(rad) * len, Math.Sin(rad) * len);
        }
        return new() { a, b };
    }

    private static bool IsRectangular(List<Point> q)
    {
        for (int i = 0; i < 4; i++)
        {
            Vector u = q[(i + 1) % 4] - q[i], w = q[(i + 3) % 4] - q[i];
            double cos = Math.Abs(Vector.Multiply(u, w) / (u.Length * w.Length));
            if (cos > 0.26) return false; // lệch vuông góc > ~15°
        }
        return true;
    }

    /// <summary>Tứ giác gần vuông: nếu gần nằm ngang/dọc → hình chữ nhật thẳng hàng; không thì giữ góc xoay.</summary>
    private static List<Point> Rectangle(List<Point> q)
    {
        Vector e = q[1] - q[0];
        double ang = Math.Atan2(e.Y, e.X) * 180 / Math.PI;
        double tilt = ang - Math.Round(ang / 90) * 90;
        if (Math.Abs(tilt) < 8)
        {
            Rect r = Bounds(q);
            return new() { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft, r.TopLeft };
        }
        // Xoay: dựng hình chữ nhật từ cạnh đầu và chiều cao trung bình.
        Vector u = e; u.Normalize();
        var nrm = new Vector(-u.Y, u.X);
        double h = (Vector.Multiply(q[3] - q[0], nrm) + Vector.Multiply(q[2] - q[1], nrm)) / 2;
        double w = (Vector.Multiply(q[1] - q[0], u) + Vector.Multiply(q[2] - q[3], u)) / 2;
        Point p0 = q[0], p1 = p0 + u * w, p2 = p1 + nrm * h, p3 = p0 + nrm * h;
        return new() { p0, p1, p2, p3, p0 };
    }

    // ---------------------------------------------------------------- đo đạc

    private static double CircleDeviation(List<Point> pts, out Point c, out double r)
    {
        double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        c = new Point(cx, cy);
        Point center = c;
        var d = pts.Select(p => (p - center).Length).ToList();
        r = d.Average();
        double mean = r;
        double sd = Math.Sqrt(d.Average(v => (v - mean) * (v - mean)));
        return r < 1 ? 1 : sd / r;
    }

    private static double EllipseDeviation(List<Point> pts, Rect box)
    {
        double a = box.Width / 2, b = box.Height / 2;
        if (a < 1 || b < 1) return 1;
        double cx = box.X + a, cy = box.Y + b;
        var d = pts.Select(p => Math.Sqrt(Math.Pow((p.X - cx) / a, 2) + Math.Pow((p.Y - cy) / b, 2))).ToList();
        double mean = d.Average();
        return Math.Sqrt(d.Average(v => (v - mean) * (v - mean))) + Math.Abs(1 - mean) * 0.5;
    }

    /// <summary>Khoảng cách trung bình từ điểm nét gốc tới đa giác đã đơn giản hóa.</summary>
    private static double PolygonError(List<Point> pts, List<Point> poly, bool closed = true)
    {
        int segs = closed ? poly.Count : poly.Count - 1;
        double sum = 0;
        foreach (Point p in pts)
        {
            double best = double.MaxValue;
            for (int i = 0; i < segs; i++)
                best = Math.Min(best, DistToSegment(p, poly[i], poly[(i + 1) % poly.Count]));
            sum += best;
        }
        return sum / pts.Count;
    }

    private static double DistToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Ramer–Douglas–Peucker: giữ các điểm "góc" của nét.</summary>
    private static List<Point> Rdp(List<Point> pts, double eps)
    {
        if (pts.Count < 3) return new List<Point>(pts);
        int idx = -1;
        double max = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double d = DistToSegment(pts[i], pts[0], pts[^1]);
            if (d > max) { max = d; idx = i; }
        }
        // Nét kín: đầu ≈ cuối nên dây cung suy biến → tách tại điểm xa đầu nhất.
        if ((pts[^1] - pts[0]).Length < eps)
        {
            idx = 1;
            max = 0;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                double d = (pts[i] - pts[0]).Length;
                if (d > max) { max = d; idx = i; }
            }
            var l = Rdp(pts.GetRange(0, idx + 1), eps);
            var r = Rdp(pts.GetRange(idx, pts.Count - idx), eps);
            l.RemoveAt(l.Count - 1);
            l.AddRange(r);
            return l;
        }
        if (max <= eps) return new List<Point> { pts[0], pts[^1] };
        var left = Rdp(pts.GetRange(0, idx + 1), eps);
        var right = Rdp(pts.GetRange(idx, pts.Count - idx), eps);
        left.RemoveAt(left.Count - 1);
        left.AddRange(right);
        return left;
    }

    /// <summary>Gộp các đỉnh quá gần nhau (rung tay ở góc tạo 2 đỉnh sát nhau).</summary>
    private static List<Point> MergeClose(List<Point> v, double minDist)
    {
        var res = new List<Point>();
        foreach (Point p in v)
            if (res.Count == 0 || (p - res[^1]).Length >= minDist) res.Add(p);
        if (res.Count > 2 && (res[^1] - res[0]).Length < minDist) res.RemoveAt(res.Count - 1);
        return res;
    }

    private static List<Point> Dedup(IReadOnlyList<Point> raw)
    {
        var res = new List<Point>(raw.Count);
        foreach (Point p in raw)
            if (res.Count == 0 || (p - res[^1]).Length > 0.5) res.Add(p);
        return res;
    }

    private static double PathLength(List<Point> pts)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += (pts[i] - pts[i - 1]).Length;
        return s;
    }

    private static Rect Bounds(IEnumerable<Point> pts)
    {
        var list = pts.ToList();
        double x0 = list.Min(p => p.X), y0 = list.Min(p => p.Y), x1 = list.Max(p => p.X), y1 = list.Max(p => p.Y);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
