using System.Windows.Ink;
using WinInk = Windows.UI.Input.Inking;
using WinPoint = Windows.Foundation.Point;

namespace OmniPen.Services;

/// <summary>
/// Nhận dạng chữ viết tay bằng bộ nhận dạng có sẵn của Windows (Windows.UI.Input.Inking).
/// Tiếng Việt cần gói "Chữ viết tay" của ngôn ngữ Tiếng Việt: Cài đặt → Thời gian &amp; ngôn ngữ → Ngôn ngữ →
/// Tiếng Việt → Tùy chọn → Chữ viết tay. Không có thì dùng bộ nhận dạng mặc định (thường là tiếng Anh).
/// </summary>
public static class HandwritingService
{
    public sealed record Result(string Text, string RecognizerName, bool IsVietnamese);

    public static async Task<Result> RecognizeAsync(StrokeCollection strokes)
    {
        if (strokes.Count == 0) throw new InvalidOperationException("Không có nét viết nào để nhận dạng.");

        // Chuyển nét WPF → nét WinRT (cùng đơn vị DIP, giữ lực nhấn).
        var builder = new WinInk.InkStrokeBuilder();
        var container = new WinInk.InkStrokeContainer();
        foreach (Stroke s in strokes)
        {
            var points = s.StylusPoints.Select(p => new WinInk.InkPoint(new WinPoint(p.X, p.Y), Math.Clamp(p.PressureFactor, 0.01f, 1f)));
            WinInk.InkStroke ws = builder.CreateStrokeFromInkPoints(points, System.Numerics.Matrix3x2.Identity);
            container.AddStroke(ws);
        }

        var rc = new WinInk.InkRecognizerContainer();
        WinInk.InkRecognizer? vi = rc.GetRecognizers()
            .FirstOrDefault(r => r.Name.Contains("Vietnam", StringComparison.OrdinalIgnoreCase)
                              || r.Name.Contains("Việt", StringComparison.OrdinalIgnoreCase));
        if (vi is not null) rc.SetDefaultRecognizer(vi);
        string name = vi?.Name ?? rc.GetRecognizers().FirstOrDefault()?.Name ?? "mặc định";

        IReadOnlyList<WinInk.InkRecognitionResult> results = await rc.RecognizeAsync(container, WinInk.InkRecognitionTarget.All);

        // Mỗi kết quả là một "từ"; sắp theo dòng (trên → dưới) rồi trái → phải.
        var words = results
            .Select(r => (Box: r.BoundingRect, Text: r.GetTextCandidates().FirstOrDefault() ?? ""))
            .Where(w => w.Text.Length > 0)
            .ToList();

        var lines = new List<List<(Windows.Foundation.Rect Box, string Text)>>();
        foreach (var w in words.OrderBy(w => w.Box.Y + w.Box.Height / 2))
        {
            double cy = w.Box.Y + w.Box.Height / 2;
            var line = lines.FirstOrDefault(l => Math.Abs(l[0].Box.Y + l[0].Box.Height / 2 - cy) < Math.Max(l[0].Box.Height, w.Box.Height) * 0.6);
            if (line is null) lines.Add(new() { w });
            else line.Add(w);
        }
        string text = string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(w => w.Box.X).Select(w => w.Text))));

        return new Result(text, name, vi is not null);
    }
}
