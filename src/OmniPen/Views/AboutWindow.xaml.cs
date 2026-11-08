using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniPen.Services;

namespace OmniPen.Views;

/// <summary>Giới thiệu sản phẩm: logo, phiên bản, tác giả, ủng hộ, tổng quan tính năng, phím tắt.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow(AppSettings settings)
    {
        InitializeComponent();

        TitleText.Text = AppInfo.Name;
        VersionText.Text = "Phiên bản " + AppInfo.Version;
        DescriptionText.Text = AppInfo.Description;
        TaglineText.Text = AppInfo.Tagline;
        AuthorText.Text = AppInfo.AuthorName;
        SchoolText.Text = AppInfo.School;
        DonateRun.Text = AppInfo.Website;
        CopyrightText.Text = AppInfo.Copyright;

        foreach (var (icon, title, text) in AppInfo.Features)
            FeatureList.Items.Add(FeatureCard(icon, title, text));

        ShortcutText.Text =
            "Toàn cục (dùng được cả khi đang ẩn):\n" +
            $"   {settings.HotkeyToggleDraw}  bật/tắt vẽ   ·   {settings.HotkeyBoard}  bảng   ·   {settings.HotkeyEraser}  tẩy\n" +
            $"   {settings.HotkeyClear}  xóa tất cả   ·   {settings.HotkeyToolbar}  hiện/ẩn thanh công cụ\n" +
            "Khi đang vẽ:\n" +
            "   Esc về chuột · P bút · H dạ quang · E tẩy · S chọn · R thước kẻ · Delete xóa phần đã chọn\n" +
            "   Ctrl+Z hoàn tác · Ctrl+Y làm lại\n" +
            "Trên bảng:\n" +
            "   PageUp/PageDown đổi trang · Lăn chuột cuộn · Ctrl+lăn chuột phóng to/thu nhỏ · Ctrl+0 về 100%\n" +
            "   Giữ nút giữa chuột kéo trang · Giữ Shift khi vẽ hình để vẽ chuẩn";
    }

    private static Border FeatureCard(string icon, string title, string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xFE)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))
            }
        };
        grid.Children.Add(badge);

        var body = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)) });
        body.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
            Margin = new Thickness(0, 3, 0, 0),
            LineHeight = 18
        });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 10, 10),
            Child = grid
        };
    }

    /// <summary>Mở cửa sổ ủng hộ (mã QR lớn + website).</summary>
    private void Donate_Click(object sender, RoutedEventArgs e) => new DonateWindow { Owner = this }.ShowDialog();

    private void Qr_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Donate_Click(sender, e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
