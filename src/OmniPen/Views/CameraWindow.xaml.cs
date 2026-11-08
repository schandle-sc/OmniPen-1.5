using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
// Alias thay vì "using Windows.Graphics.Imaging": namespace đó cũng có BitmapFrame/BitmapEncoder trùng tên với WPF.
using BitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using BitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using SoftwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap;

namespace OmniPen.Views;

/// <summary>
/// Chụp ảnh từ webcam / camera tài liệu bằng Windows.Media.Capture (có sẵn trong Windows 10/11).
/// Xem trước trực tiếp qua MediaFrameReader: mỗi khung hình (SoftwareBitmap BGRA8) được chép vào
/// WriteableBitmap của WPF. Khung đến trên luồng nền → bỏ qua khung mới khi UI chưa vẽ xong khung trước.
/// </summary>
public partial class CameraWindow : Window
{
    private List<MediaFrameSourceGroup> _groups = new();
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _preview;
    private byte[]? _lastFrame;
    private int _lastW, _lastH;
    private int _busy;
    private volatile bool _frozen;

    /// <summary>Ảnh đã chụp (PNG) khi DialogResult = true.</summary>
    public byte[]? CapturedPng { get; private set; }

    public CameraWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadCamerasAsync();
        Closed += async (_, _) => await StopAsync();
    }

    private async Task LoadCamerasAsync()
    {
        try
        {
            var all = await MediaFrameSourceGroup.FindAllAsync();
            _groups = all.Where(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color)).ToList();
            if (_groups.Count == 0)
            {
                StatusText.Text = "Không tìm thấy camera nào trên máy.";
                BtnCapture.IsEnabled = false;
                return;
            }
            CameraCombo.ItemsSource = _groups.Select(g => g.DisplayName).ToList();
            CameraCombo.SelectedIndex = 0; // → SelectionChanged → bật camera
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không liệt kê được camera.\n" + ex.Message;
        }
    }

    private async void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraCombo.SelectedIndex < 0 || CameraCombo.SelectedIndex >= _groups.Count) return;
        await StartAsync(_groups[CameraCombo.SelectedIndex]);
    }

    private async Task StartAsync(MediaFrameSourceGroup group)
    {
        await StopAsync();
        StatusText.Text = "Đang mở camera…";
        StatusText.Visibility = Visibility.Visible;
        Retake();

        try
        {
            MediaFrameSourceInfo info =
                group.SourceInfos.FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color && i.MediaStreamType == MediaStreamType.VideoPreview)
                ?? group.SourceInfos.First(i => i.SourceKind == MediaFrameSourceKind.Color);

            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu, // khung nằm ở RAM → đọc được pixel
                StreamingCaptureMode = StreamingCaptureMode.Video
            });

            MediaFrameSource source = _capture.FrameSources[info.Id];
            _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            _reader.FrameArrived += OnFrameArrived;
            MediaFrameReaderStartStatus status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
                StatusText.Text = $"Không bật được camera ({status}). Camera có thể đang được ứng dụng khác dùng.";
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "Windows đang chặn quyền dùng camera.\n" +
                              "Mở Cài đặt → Quyền riêng tư & bảo mật → Camera, bật \"Cho phép ứng dụng desktop truy cập camera\".";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không bật được camera.\n" + ex.Message;
        }
    }

    private async Task StopAsync()
    {
        MediaFrameReader? reader = _reader;
        MediaCapture? capture = _capture;
        _reader = null;
        _capture = null;
        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync();
                reader.Dispose();
            }
        }
        catch { /* camera đã bị rút */ }
        capture?.Dispose();
    }

    /// <summary>Luồng nền: lấy khung mới nhất, chép pixel rồi đẩy sang UI thread.</summary>
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_frozen || Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
            SoftwareBitmap? sb = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (sb is null) { _busy = 0; return; }

            SoftwareBitmap bgra = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? sb
                : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var bytes = new byte[w * h * 4];
            bgra.CopyToBuffer(bytes.AsBuffer()); // AsBuffer bọc trực tiếp mảng → dữ liệu ghi thẳng vào bytes
            if (!ReferenceEquals(bgra, sb)) bgra.Dispose();

            Dispatcher.InvokeAsync(() =>
            {
                try { ShowFrame(bytes, w, h); }
                finally { _busy = 0; }
            });
        }
        catch
        {
            _busy = 0;
        }
    }

    private void ShowFrame(byte[] bytes, int w, int h)
    {
        if (_frozen) return;
        // Bgr32: bỏ qua kênh alpha (nhiều camera để alpha = 0 → ảnh sẽ trong suốt nếu dùng Bgra32).
        if (_preview is null || _preview.PixelWidth != w || _preview.PixelHeight != h)
        {
            _preview = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.Source = _preview;
        }
        _preview.WritePixels(new Int32Rect(0, 0, w, h), bytes, w * 4, 0);
        _lastFrame = bytes;
        _lastW = w;
        _lastH = h;
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFrame is null) return;
        _frozen = true; // dừng cập nhật xem trước: ảnh đang hiện chính là ảnh đã chụp
        BtnCapture.Visibility = Visibility.Collapsed;
        BtnRetake.Visibility = Visibility.Visible;
        BtnInsert.Visibility = Visibility.Visible;
        BtnInsert.IsDefault = true;
    }

    private void BtnRetake_Click(object sender, RoutedEventArgs e) => Retake();

    private void Retake()
    {
        _frozen = false;
        BtnCapture.Visibility = Visibility.Visible;
        BtnRetake.Visibility = Visibility.Collapsed;
        BtnInsert.Visibility = Visibility.Collapsed;
        BtnCapture.IsDefault = true;
    }

    private void BtnInsert_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFrame is null) return;
        BitmapSource bmp = BitmapSource.Create(_lastW, _lastH, 96, 96, PixelFormats.Bgr32, null, _lastFrame, _lastW * 4);
        if (MirrorCheck.IsChecked == true)
            bmp = new TransformedBitmap(bmp, new ScaleTransform(-1, 1));

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        CapturedPng = ms.ToArray();
        DialogResult = true;
    }

    private void Mirror_Changed(object sender, RoutedEventArgs e) =>
        MirrorTransform.ScaleX = MirrorCheck.IsChecked == true ? -1 : 1;
}
