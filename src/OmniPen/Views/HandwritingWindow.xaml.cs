using System.Windows;
using System.Windows.Media;

namespace OmniPen.Views;

/// <summary>Xem/sửa kết quả nhận dạng chữ viết tay, chọn kiểu chữ, rồi thay thế hoặc chèn thêm.</summary>
public partial class HandwritingWindow : Window
{
    public string ResultTextValue => ResultBox.Text.Trim();
    public bool UseHandwritingFont => HandFont.IsChecked == true;
    public bool KeepStrokes { get; private set; }

    public HandwritingWindow(string text, string recognizer, bool isVietnamese)
    {
        InitializeComponent();
        ResultBox.Text = text;
        InfoText.Text = isVietnamese
            ? $"Đã nhận dạng bằng: {recognizer}."
            : $"Đang dùng bộ nhận dạng: {recognizer}. Để nhận dạng tiếng Việt có dấu, cài gói Chữ viết tay: " +
              "Cài đặt Windows → Thời gian & ngôn ngữ → Ngôn ngữ → Tiếng Việt → Tùy chọn → Chữ viết tay → Tải xuống.";
        Loaded += (_, _) => { ResultBox.Focus(); ResultBox.SelectAll(); Font_Changed(this, new RoutedEventArgs()); };
    }

    private void Font_Changed(object sender, RoutedEventArgs e)
    {
        if (ResultBox is null) return;
        ResultBox.FontFamily = new FontFamily(HandFont.IsChecked == true ? "Segoe Print, Ink Free, Comic Sans MS" : "Segoe UI");
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (ResultTextValue.Length == 0) return;
        KeepStrokes = false;
        DialogResult = true;
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (ResultTextValue.Length == 0) return;
        KeepStrokes = true;
        DialogResult = true;
    }
}
