using System.Windows;
using System.Windows.Media;

namespace OmniPen.Ink;

/// <summary>
/// Sinh Brush cho các mẫu giấy. DrawingBrush lát gạch (TileMode.Tile) với Viewport tuyệt đối:
/// chỉ vẽ MỘT ô mẫu, GPU lặp lại cho cả trang → rẻ hơn nhiều so với vẽ hàng nghìn đường thẳng.
/// Brush nằm trong hệ tọa độ trang nên phóng to/thu nhỏ trang thì dòng kẻ cũng co giãn theo.
/// </summary>
public static class PaperBrushes
{
    private static readonly Dictionary<PaperType, Brush> Cache = new();

    public static Brush Get(PaperType type)
    {
        if (Cache.TryGetValue(type, out Brush? b)) return b;
        b = Create(type);
        b.Freeze();
        Cache[type] = b;
        return b;
    }

    /// <summary>Màu nền chính của kiểu giấy (dùng cho SVG, xuất ảnh).</summary>
    public static Color BaseColor(PaperType type) => type switch
    {
        PaperType.Blackboard => Color.FromRgb(0x22, 0x26, 0x28),
        PaperType.Greenboard => Color.FromRgb(0x1F, 0x4D, 0x3A),
        PaperType.White => Color.FromRgb(0xFF, 0xFE, 0xF8),
        _ => Colors.White
    };

    private static Brush Create(PaperType type)
    {
        Color paper = BaseColor(type);
        Color blue = Color.FromRgb(0x9E, 0xC5, 0xE8);
        Color purple = Color.FromRgb(0xB3, 0x9D, 0xDB);
        Color purpleLight = Color.FromRgb(0xDD, 0xD3, 0xF0);

        return type switch
        {
            // Ô 40×40: một dòng kẻ ngang ở đáy ô.
            PaperType.Lined => Tile(40, 40, paper,
                (MakeLine(0, 39.5, 40, 39.5), MakePen(blue, 1))),

            // Ô vuông 30×30: kẻ cạnh trên + cạnh trái → lát lại thành lưới.
            PaperType.Grid => Tile(30, 30, paper,
                (MakeLine(0, 0.5, 30, 0.5), MakePen(blue, 1)),
                (MakeLine(0.5, 0, 0.5, 30), MakePen(blue, 1))),

            // Vở 4 ô ly: ô lớn 48×48, đường đậm ở cạnh ô lớn, 3 dòng mảnh chia 4 ô ly.
            PaperType.OLy => Tile(48, 48, paper,
                (MakeLine(0, 12.5, 48, 12.5), MakePen(purpleLight, 1)),
                (MakeLine(0, 24.5, 48, 24.5), MakePen(purpleLight, 1)),
                (MakeLine(0, 36.5, 48, 36.5), MakePen(purpleLight, 1)),
                (MakeLine(0, 0.75, 48, 0.75), MakePen(purple, 1.5)),
                (MakeLine(0.75, 0, 0.75, 48), MakePen(purple, 1.5))),

            _ => new SolidColorBrush(paper)
        };
    }

    private static Brush Tile(double w, double h, Color background, params (Geometry Geo, Pen Pen)[] parts)
    {
        var rect = new Rect(0, 0, w, h);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(background), null, new RectangleGeometry(rect)));
        foreach (var (geo, pen) in parts)
            group.Children.Add(new GeometryDrawing(null, pen, geo));

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = rect,
            ViewportUnits = BrushMappingMode.Absolute,
            // Viewbox cố định = đúng ô mẫu: nét tràn ra ngoài bị cắt, ô lát khít không co giãn.
            Viewbox = rect,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
    }

    private static Geometry MakeLine(double x1, double y1, double x2, double y2) =>
        new LineGeometry(new Point(x1, y1), new Point(x2, y2));

    private static Pen MakePen(Color c, double thickness) => new(new SolidColorBrush(c), thickness);
}
