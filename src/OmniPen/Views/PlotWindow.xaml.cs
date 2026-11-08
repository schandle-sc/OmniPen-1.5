using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OmniPen.Ink;

namespace OmniPen.Views;

/// <summary>Soạn hệ trục Oxy + tối đa 3 hàm số, xem trước tức thì, rồi vẽ lên trang dưới dạng nét vector.</summary>
public partial class PlotWindow : Window
{
    private static readonly Color[] Colors3 = { Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x16, 0xA3, 0x4A) };
    private static readonly string[] Samples =
    {
        "y = x^2 - 4x + 3", "y = 2x + 1", "y = sin(x)", "y = cos(x)", "y = 1/x", "y = sqrt(x)", "y = |x|", "y = x^3 - 3x", "y = e^x", "y = ln(x)", "y = tan(x)"
    };

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _ready;

    public GraphResult? Result { get; private set; }

    public PlotWindow()
    {
        InitializeComponent();
        foreach (string s in Samples)
        {
            var b = new Button { Content = s.Replace("y = ", ""), Margin = new Thickness(0, 0, 5, 5), Padding = new Thickness(7, 2, 7, 2), FontFamily = new FontFamily("Consolas") };
            string text = s;
            b.Click += (_, _) =>
            {
                TextBox target = new[] { F1, F2, F3 }.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Text)) ?? F1;
                target.Text = text;
            };
            SamplePanel.Children.Add(b);
        }
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };
        Loaded += (_, _) => { _ready = true; Rebuild(); };
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        YMin.IsEnabled = YMax.IsEnabled = AutoY.IsChecked != true;
        _debounce.Stop();
        _debounce.Start();
    }

    private static double Num(TextBox t, string name)
    {
        string s = t.Text.Trim().Replace(',', '.').Replace('−', '-');
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
        throw new FormatException($"Giá trị \"{name}\" không phải là số.");
    }

    private GraphSpec BuildSpec()
    {
        var spec = new GraphSpec
        {
            XMin = Num(XMin, "x từ"),
            XMax = Num(XMax, "x đến"),
            YMin = Num(YMin, "y từ"),
            YMax = Num(YMax, "y đến"),
            AutoY = AutoY.IsChecked == true,
            ShowGrid = GridCheck.IsChecked == true,
            Size = SizeCombo.SelectedIndex switch { 0 => new Size(480, 340), 2 => new Size(860, 580), _ => new Size(640, 440) }
        };
        var boxes = new[] { F1, F2, F3 };
        for (int i = 0; i < boxes.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(boxes[i].Text)) continue;
            try { spec.Functions.Add((MathExpression.Parse(boxes[i].Text), Colors3[i])); }
            catch (FormatException ex) { throw new FormatException($"Hàm {i + 1}: {ex.Message}"); }
        }
        return spec;
    }

    private bool Rebuild()
    {
        try
        {
            GraphSpec spec = BuildSpec();
            Result = GraphBuilder.Build(spec);
            if (spec.AutoY)
            {
                YMin.Text = spec.YMin.ToString(CultureInfo.InvariantCulture);
                YMax.Text = spec.YMax.ToString(CultureInfo.InvariantCulture);
                _debounce.Stop(); // đổi chữ trong ô y gây TextChanged – không dựng lại lần nữa
            }
            Preview.Source = GraphBuilder.RenderPreview(Result);
            ErrorText.Text = "";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            ErrorText.Text = ex.Message;
            Result = null;
            return false;
        }
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        if (Rebuild() && Result is not null) DialogResult = true;
    }
}
