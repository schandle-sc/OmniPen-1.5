using System.Windows;

namespace OmniPen.Views;

/// <summary>Cửa sổ ủng hộ tác giả: mã QR chuyển khoản (VietQR/MoMo) và website.</summary>
public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();
        AuthorText.Text = AppInfo.Author;
        SiteRun.Text = AppInfo.Website;
    }

    private void Site_Click(object sender, RoutedEventArgs e) => AppInfo.OpenDonatePage();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
