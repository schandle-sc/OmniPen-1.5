using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniPen.Ink;
using OmniPen.Services;
using WinForms = System.Windows.Forms;

namespace OmniPen.Views;

/// <summary>Soạn, xem trước và chèn công thức LaTeX (dựng bằng MiKTeX/TeX Live cài trên máy).</summary>
public partial class LatexWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Color _color;
    private string? _bin;
    private bool _busy;

    public byte[]? ResultPng { get; private set; }
    public int ResultDpi { get; private set; }
    public string Formula => FormulaBox.Text.Trim();

    private static readonly (string Label, string Tex)[] Samples =
    {
        ("a/b", @"\frac{a}{b}"),
        ("√x", @"\sqrt{x}"),
        ("xⁿ", @"x^{n}"),
        ("xᵢ", @"x_{i}"),
        ("Σ", @"\sum_{i=1}^{n} i"),
        ("∫", @"\int_{a}^{b} f(x)\,dx"),
        ("lim", @"\lim_{x \to 0} \frac{\sin x}{x} = 1"),
        ("hệ", @"\begin{cases} x + y = 3 \\ 2x - y = 0 \end{cases}"),
        ("ma trận", @"\begin{pmatrix} 1 & 2 \\ 3 & 4 \end{pmatrix}"),
        ("vector", @"\vec{AB} = \overrightarrow{AB}"),
        ("góc", @"\widehat{ABC} = 60^\circ"),
        ("α β π", @"\alpha + \beta = \pi")
    };

    public LatexWindow(AppSettings settings, Color color, string? initialFormula = null)
    {
        InitializeComponent();
        _settings = settings;
        _color = color;
        if (!string.IsNullOrWhiteSpace(initialFormula)) FormulaBox.Text = initialFormula;

        foreach (var (label, tex) in Samples)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 6), ToolTip = tex };
            string t = tex;
            b.Click += (_, _) =>
            {
                int pos = FormulaBox.CaretIndex;
                FormulaBox.Text = FormulaBox.Text.Insert(pos, t);
                FormulaBox.CaretIndex = pos + t.Length;
                FormulaBox.Focus();
            };
            SamplePanel.Children.Add(b);
        }

        Loaded += (_, _) =>
        {
            DetectLatex();
            FormulaBox.Focus();
            FormulaBox.SelectAll();
        };
    }

    private void DetectLatex()
    {
        _bin = LatexService.FindBinFolder(_settings.LatexFolder);
        MissingPanel.Visibility = _bin is null ? Visibility.Visible : Visibility.Collapsed;
        BtnPreview.IsEnabled = BtnInsert.IsEnabled = _bin is not null;
    }

    private async Task<bool> RenderAsync()
    {
        if (_bin is null || _busy || Formula.Length == 0) return false;
        _busy = true;
        StatusText.Text = "";
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var (png, dpi) = await LatexService.RenderAsync(Formula, _color, _bin);
            ResultPng = png;
            ResultDpi = dpi;
            PreviewImage.Source = PageElements.Decode(png);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
        finally
        {
            _busy = false;
            Cursor = null;
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await RenderAsync();

    private async void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (await RenderAsync()) DialogResult = true;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(LatexService.DownloadUrl) { UseShellExecute = true }); }
        catch { /* không mở được trình duyệt */ }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Chọn thư mục chứa latex.exe và dvipng.exe (VD: ...\\MiKTeX\\miktex\\bin\\x64)",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        _settings.LatexFolder = dlg.SelectedPath;
        SettingsService.Save(_settings);
        DetectLatex();
        if (_bin is null)
            StatusText.Text = "Thư mục này không có đủ latex.exe và dvipng.exe.";
    }
}
