using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OmniPen.Ink;
using OmniPen.Interop;
using OmniPen.Services;
using OmniPen.Views;

namespace OmniPen;

/// <summary>
/// Composition root: tạo cửa sổ, khay hệ thống, nút nổi, phím tắt và nối lệnh Toolbar/Tray/Hotkey → Canvas.
/// Không chứa logic vẽ.
/// </summary>
public partial class App : Application
{
    // "Local\" = phạm vi phiên đăng nhập (máy dùng chung nhiều tài khoản giáo viên vẫn chạy độc lập được).
    private const string MutexName = @"Local\OmniPen_SingleInstance_7C1E4B";
    private const string ActivateEventName = @"Local\OmniPen_Activate_7C1E4B";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;

    private AppSettings _settings = new();
    private CanvasWindow? _canvas;
    private ToolbarWindow? _toolbar;
    private FloatingButtonWindow? _floating;
    private TrayService? _tray;
    private HotkeyManager? _hotkeys;
    private string? _currentFile;

    // v1.5 – trình chiếu & trợ giảng
    private EffectsWindow? _effects;
    private InputHookService? _inputHooks;
    private MagnifierWindow? _magnifier;
    private readonly List<BlurPatchWindow> _blurPatches = new();
    private CountdownWindow? _countdown;
    private NamePickerWindow? _namePicker;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ===== 1) Chỉ cho chạy MỘT bản =====
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { /* bản kia đang đóng */ }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.InvokeAsync(ShowToolbar), null, Timeout.Infinite, false);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        _settings = SettingsService.Load();
        bool startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        // ===== 2) Khay hệ thống =====
        _tray = new TrayService();
        WireTray(_tray);

        // ===== 3) Canvas PHẢI Show trước (WPF không cho gán Owner là cửa sổ chưa từng hiển thị) =====
        _canvas = new CanvasWindow(_settings) { AutoShape = _settings.AutoShape };
        _canvas.Show();
        _canvas.BlurRegionRequested += (_, r) => AddBlurPatch(r);
        _canvas.BlurRemoveRequested += (_, p) => RemoveBlurPatchAt(p);

        // ===== 4) Toolbar & nút nổi: Owner = canvas → luôn nằm trên lớp vẽ (quy tắc owned window) =====
        _toolbar = new ToolbarWindow { Owner = _canvas };
        _toolbar.SetInitialPosition(_settings.ToolbarX, _settings.ToolbarY);
        _toolbar.SetVertical(_settings.ToolbarVertical, reposition: false);
        _toolbar.CommandRequested += OnToolbarCommand;

        _floating = new FloatingButtonWindow { Owner = _canvas };
        _floating.SetInitialPosition(_settings.FloatingX, _settings.FloatingY);
        _floating.OpenRequested += (_, _) => ShowToolbar();

        ToolbarWindow toolbar = _toolbar;
        TrayService tray = _tray;
        _canvas.AnchorProvider = () => toolbar.GetPhysicalCenter();
        _canvas.CanvasStateChanged += (_, s) =>
        {
            toolbar.Render(s);
            tray.UpdateState(s.Mode == InteractionMode.Draw, s.BoardOn);
        };

        // ===== 5) Phím tắt toàn cục =====
        _hotkeys = new HotkeyManager();
        RegisterHotkeys(showErrors: true);

        if (startHidden)
        {
            if (_settings.ShowFloatingButton) _floating.Show();
            _tray.ShowTip("OmniPen đang chạy", $"Nhấn {_settings.HotkeyToggleDraw} để vẽ, {_settings.HotkeyToolbar} để hiện thanh công cụ.");
        }
        else
        {
            _toolbar.Show();
        }

        _toolbar.Render(_canvas.GetState());
        UpdatePresenterState();

        // Mở file truyền qua dòng lệnh (bấm đúp file .omnipen / .pdf).
        string? file = e.Args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        if (file is not null) _ = OpenFileFromArgsAsync(file);
    }

    private async Task OpenFileFromArgsAsync(string file)
    {
        if (_canvas is null) return;
        try
        {
            if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) await _canvas.OpenPdfAsync(file);
            else
            {
                await _canvas.LoadDocumentAsync(file);
                _currentFile = file;
            }
        }
        catch (Exception ex) { ShowError("Không mở được file", ex); }
    }

    #region Wiring

    private void WireTray(TrayService tray)
    {
        tray.ShowToolbarRequested += (_, _) => ShowToolbar();
        tray.ToggleDrawRequested += (_, _) => _canvas?.ToggleMode();
        tray.ToggleBoardRequested += (_, _) => _canvas?.ToggleBoard();
        tray.ClearRequested += (_, _) => _canvas?.ClearAll();
        tray.ScreenshotRequested += (_, _) => TakeScreenshot();
        tray.SettingsRequested += (_, _) => OpenSettings();
        tray.AboutRequested += (_, _) => OpenAbout();
        tray.DonateRequested += (_, _) => OpenDonate();
        tray.ExitRequested += (_, _) => RequestExit();
    }

    private void OnToolbarCommand(object? sender, ToolbarCommandEventArgs e)
    {
        if (_canvas is not { } c) return;

        switch (e.Command)
        {
            case ToolbarCommand.SetMode when e.Argument is InteractionMode m: c.SetMode(m); break;
            case ToolbarCommand.SetTool when e.Argument is InkTool t: c.SetTool(t); break;
            case ToolbarCommand.SetShape when e.Argument is ShapeKind k: c.SetShape(k); break;
            case ToolbarCommand.SetTable when e.Argument is ValueTuple<int, int> size:
                c.SetTableSize(size.Item1, size.Item2);
                _settings.TableRows = size.Item1;
                _settings.TableCols = size.Item2;
                break;
            case ToolbarCommand.SetColor when e.Argument is Color col: c.SetPenColor(col); break;
            case ToolbarCommand.SetSize when e.Argument is double d: c.SetPenSize(d); break;
            case ToolbarCommand.Undo: c.Undo(); break;
            case ToolbarCommand.Redo: c.Redo(); break;
            case ToolbarCommand.Clear: c.ClearAll(); break;
            case ToolbarCommand.ToggleRuler: c.ToggleRuler(); break;
            case ToolbarCommand.ToggleBoard: c.ToggleBoard(); break;
            case ToolbarCommand.PreviousPage: c.PreviousPage(); break;
            case ToolbarCommand.NextPage: c.NextPage(); break;
            case ToolbarCommand.AddPage: c.AddPage(); break;
            case ToolbarCommand.DeletePage: c.DeletePage(); break;
            case ToolbarCommand.SetPaper when e.Argument is PaperType p: c.SetPaper(p); break;
            case ToolbarCommand.Zoom when e.Argument is ZoomCommand z: c.Zoom(z); break;
            case ToolbarCommand.SetAutoScroll when e.Argument is bool on:
                c.SetAutoScroll(on);
                _settings.AutoScroll = on;
                break;
            case ToolbarCommand.InsertImage: InsertImageFromFile(); break;
            case ToolbarCommand.InsertCamera: InsertFromCamera(); break;
            case ToolbarCommand.InsertClipboard: InsertFromClipboard(); break;
            case ToolbarCommand.InsertLatex: InsertLatex(); break;
            case ToolbarCommand.Screenshot: TakeScreenshot(); break;
            case ToolbarCommand.Open: OpenDocument(); break;
            case ToolbarCommand.OpenPdf: OpenPdf(); break;
            case ToolbarCommand.Save: SaveDocument(); break;
            case ToolbarCommand.ExportPdf: Export(ExportKind.Pdf); break;
            case ToolbarCommand.ExportPng: Export(ExportKind.Png); break;
            case ToolbarCommand.ExportPngTransparent: Export(ExportKind.PngTransparent); break;
            case ToolbarCommand.ExportSvg: Export(ExportKind.Svg); break;
            case ToolbarCommand.OpenCaptures: OpenFolder(ScreenCaptureService.Folder); break;
            case ToolbarCommand.SetVertical when e.Argument is bool v:
                _settings.ToolbarVertical = v;
                _toolbar?.SetVertical(v);
                SettingsService.Save(_settings);
                break;
            case ToolbarCommand.Settings: OpenSettings(); break;
            case ToolbarCommand.About: OpenAbout(); break;
            case ToolbarCommand.Donate: OpenDonate(); break;
            case ToolbarCommand.OpenPlot: OpenPlot(); break;
            case ToolbarCommand.CaptureFull: TakeScreenshot(); break;
            case ToolbarCommand.CaptureRegion: TakeRegionScreenshot(); break;
            case ToolbarCommand.ToggleKeys when e.Argument is bool k: SetKeyVisualizer(k); break;
            case ToolbarCommand.ToggleMagnifier: ToggleMagnifier(); break;
            case ToolbarCommand.ToggleFocus when e.Argument is bool f: SetFocusHighlight(f); break;
            case ToolbarCommand.ClearBlur: ClearBlurPatches(); break;
            case ToolbarCommand.RevealTapes: c.RevealAllTapes(); break;
            case ToolbarCommand.Handwriting: ConvertHandwriting(); break;
            case ToolbarCommand.PasteCode: PasteCode(); break;
            case ToolbarCommand.Countdown: ShowCountdown(); break;
            case ToolbarCommand.NamePicker: ShowNamePicker(); break;
            case ToolbarCommand.SetAutoShape when e.Argument is bool a:
                c.AutoShape = a;
                _settings.AutoShape = a;
                SettingsService.Save(_settings);
                break;
            case ToolbarCommand.Hide: HideToolbar(); break;
            case ToolbarCommand.Exit: RequestExit(); break;
        }
    }

    private void RegisterHotkeys(bool showErrors)
    {
        if (_hotkeys is not { } hotkeys || _canvas is not { } canvas) return;
        hotkeys.UnregisterAll();

        var failed = new List<string>();
        void Reg(string text, Action action)
        {
            if (Hotkey.Parse(text) is not { } hk) return;
            if (!hotkeys.Register(hk, action)) failed.Add(hk.ToString());
        }

        Reg(_settings.HotkeyToggleDraw, () => canvas.ToggleMode());
        Reg(_settings.HotkeyEraser, () =>
        {
            canvas.SetTool(InkTool.EraseByPoint);
            canvas.SetMode(InteractionMode.Draw);
        });
        Reg(_settings.HotkeyClear, () => canvas.ClearAll());
        Reg(_settings.HotkeyBoard, () => canvas.ToggleBoard());
        Reg(_settings.HotkeyToolbar, () =>
        {
            if (_toolbar?.IsVisible == true) HideToolbar();
            else ShowToolbar();
        });
        Reg(_settings.HotkeyMagnifier, ToggleMagnifier);
        Reg(_settings.HotkeyCode, PasteCode);
        Reg(_settings.HotkeySnip, TakeRegionScreenshot);

        if (failed.Count > 0 && showErrors)
            _tray?.ShowTip("Không đăng ký được phím tắt",
                $"{string.Join(", ", failed)} đang bị ứng dụng khác dùng. Hãy đổi trong Cài đặt.");
    }

    #endregion

    #region Toolbar, nút nổi & hộp thoại

    private void ShowToolbar()
    {
        if (_toolbar is null) return;
        _floating?.Hide();
        _toolbar.Show();
        _canvas?.ReassertTopmost();
    }

    private void HideToolbar()
    {
        if (_toolbar is null || _canvas is null) return;
        _toolbar.Hide();
        if (_settings.ShowFloatingButton) _floating?.Show();

        // Ẩn toolbar khi đang vẽ trên màn hình → trả chuột cho người dùng để không bị "kẹt".
        if (!_canvas.IsBoardOn) _canvas.SetMode(InteractionMode.Mouse);

        if (!_settings.TrayTipShown)
        {
            _tray?.ShowTip("OmniPen vẫn đang chạy",
                $"Bấm nút tròn ở góc màn hình, biểu tượng dưới khay, hoặc nhấn {_settings.HotkeyToolbar} để mở lại.");
            _settings.TrayTipShown = true;
            SettingsService.Save(_settings);
        }
    }

    /// <summary>Hộp thoại luôn có Owner = toolbar (nằm trên lớp vẽ) → hiện toolbar trước khi mở.</summary>
    private Window DialogOwner()
    {
        ShowToolbar();
        return _toolbar!;
    }

    private void OpenSettings()
    {
        Window owner = DialogOwner();
        // Gỡ phím tắt tạm thời: nếu không, RegisterHotKey "nuốt" tổ hợp phím trước khi tới ô nhập.
        _hotkeys?.UnregisterAll();
        try
        {
            var dlg = new SettingsWindow(_settings) { Owner = owner };
            if (dlg.ShowDialog() == true)
            {
                StartupService.SetEnabled(dlg.StartWithWindows);
                SettingsService.Save(_settings);
                _toolbar?.SetVertical(_settings.ToolbarVertical);
                _canvas?.SetAutoScroll(_settings.AutoScroll);
                if (_canvas is not null) _canvas.AutoShape = _settings.AutoShape;
                if (_inputHooks is not null) _inputHooks.ShowAllKeys = _settings.ShowAllKeys;
                UpdatePresenterState();
            }
        }
        finally
        {
            RegisterHotkeys(showErrors: true);
        }
    }

    private void OpenAbout()
    {
        Window owner = DialogOwner();
        new AboutWindow(_settings) { Owner = owner }.ShowDialog();
    }

    private async void TakeScreenshot()
    {
        if (_canvas is null || _toolbar is null) return;

        bool toolbarWasVisible = _toolbar.IsVisible;
        bool floatingWasVisible = _floating?.IsVisible == true;
        _toolbar.Hide();
        _floating?.Hide();
        await Task.Delay(250); // đợi DWM vẽ lại màn hình không còn toolbar

        try
        {
            string path = ScreenCaptureService.Capture(_canvas.PhysicalBounds);
            _tray?.ShowTip("Đã chụp màn hình", $"{Path.GetFileName(path)}\nĐã lưu vào Ảnh\\OmniPen và chép vào Clipboard.");
        }
        catch (Exception ex)
        {
            ShowError("Không chụp được màn hình", ex);
        }
        finally
        {
            if (toolbarWasVisible) ShowToolbar();
            else if (floatingWasVisible) _floating?.Show();
        }
    }

    #endregion

    #region Trợ giảng & trình chiếu (v1.5)

    private EffectsWindow Effects()
    {
        if (_effects is null)
        {
            _effects = new EffectsWindow();
            _effects.Show();
        }
        return _effects;
    }

    private void UpdatePresenterState() =>
        _toolbar?.SetPresenterState(
            _inputHooks?.KeyboardEnabled == true,
            _magnifier?.IsVisible == true,
            _effects?.FocusEnabled == true,
            _canvas?.AutoShape == true);

    /// <summary>Hiển thị phím tắt + gợn sóng click chuột (hook bàn phím/chuột cấp thấp).</summary>
    private void SetKeyVisualizer(bool on)
    {
        if (_inputHooks is null)
        {
            _inputHooks = new InputHookService();
            _inputHooks.KeyCombo += (_, combo) => Effects().ShowKeys(combo);
            _inputHooks.MouseClick += (_, m) => Effects().ShowRipple(m.Screen, m.Button);
        }
        _inputHooks.ShowAllKeys = _settings.ShowAllKeys;
        _inputHooks.Enable(on, on);
        if (on) Effects();
        UpdatePresenterState();
    }

    private void ToggleMagnifier()
    {
        _magnifier ??= new MagnifierWindow();
        _magnifier.Cycle();
        UpdatePresenterState();
    }

    private void SetFocusHighlight(bool on)
    {
        Effects().SetFocusEnabled(on);
        UpdatePresenterState();
    }

    private void AddBlurPatch(Int32Rect physical)
    {
        var patch = new BlurPatchWindow(physical);
        patch.Show();
        _blurPatches.Add(patch);
        _canvas?.ReassertTopmost(); // lớp vẽ nằm trên vùng mờ → vẫn viết đè lên được
    }

    private void RemoveBlurPatchAt(Point screen)
    {
        var hit = _blurPatches.LastOrDefault(p => p.Contains(screen));
        if (hit is null) return;
        hit.Close();
        _blurPatches.Remove(hit);
    }

    private void ClearBlurPatches()
    {
        foreach (var p in _blurPatches) p.Close();
        _blurPatches.Clear();
    }

    private void ShowCountdown()
    {
        if (_countdown is null || !_countdown.IsLoaded)
        {
            _countdown = new CountdownWindow { Owner = _canvas };
            _countdown.Closed += (_, _) => _countdown = null;
        }
        _countdown.Show();
    }

    private void ShowNamePicker()
    {
        if (_namePicker is null || !_namePicker.IsLoaded)
        {
            _namePicker = new NamePickerWindow(_settings) { Owner = _toolbar?.IsVisible == true ? _toolbar : _canvas };
            _namePicker.Closed += (_, _) => _namePicker = null;
        }
        _namePicker.Show();
        _namePicker.Activate();
    }

    private void OpenDonate()
    {
        Window owner = DialogOwner();
        new DonateWindow { Owner = owner }.ShowDialog();
    }

    private void OpenPlot()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();
        var dlg = new PlotWindow { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.Result is { } g)
        {
            try { _canvas.InsertGraph(g); }
            catch (Exception ex) { ShowError("Không vẽ được đồ thị", ex); }
        }
    }

    /// <summary>Dán code từ Clipboard thành khối nổi tô màu cú pháp (Dracula) trên lớp vẽ.</summary>
    private void PasteCode()
    {
        if (_canvas is null) return;
        string? code = null;
        try { if (Clipboard.ContainsText()) code = Clipboard.GetText(); }
        catch { /* Clipboard đang bận */ }
        if (string.IsNullOrWhiteSpace(code))
        {
            _tray?.ShowTip("Clipboard chưa có code", "Hãy chọn đoạn code trong IDE rồi Ctrl+C trước.");
            return;
        }
        try
        {
            var (png, size) = TextRenderService.RenderCode(code);
            _canvas.InsertImage(png, "code", code, size);
        }
        catch (Exception ex) { ShowError("Không dán được code", ex); }
    }

    private async void ConvertHandwriting()
    {
        if (_canvas is null) return;
        System.Windows.Ink.StrokeCollection strokes = _canvas.SelectedOrAllStrokes();
        if (strokes.Count == 0)
        {
            _tray?.ShowTip("Chưa có chữ viết tay", "Viết chữ lên màn hình, dùng công cụ Chọn khoanh vùng rồi chọn lại chức năng này.");
            return;
        }
        try
        {
            HandwritingService.Result r;
            using (BusyCursor()) r = await HandwritingService.RecognizeAsync(strokes);
            Window owner = DialogOwner();
            var dlg = new HandwritingWindow(r.Text, r.RecognizerName, r.IsVietnamese) { Owner = owner };
            if (dlg.ShowDialog() != true) return;

            // Cỡ chữ ≈ chiều cao một dòng viết tay.
            Rect b = strokes.GetBounds();
            int lines = Math.Max(1, dlg.ResultTextValue.Split('\n').Length);
            double fontSize = Math.Clamp(b.Height / lines * 0.62, 14, 96);
            var (png, size) = TextRenderService.RenderText(dlg.ResultTextValue, dlg.UseHandwritingFont, _canvas.GetState().PenColor, fontSize);

            if (dlg.KeepStrokes) _canvas.InsertImage(png, "text", dlg.ResultTextValue, size);
            else _canvas.ReplaceStrokesWithImage(strokes, png, size, "text", dlg.ResultTextValue);
        }
        catch (Exception ex)
        {
            ShowError("Không nhận dạng được chữ viết tay", ex);
        }
    }

    /// <summary>Chụp vùng chọn: ẩn thanh công cụ, chụp đóng băng toàn desktop, cho kéo khung, lưu phần đã chọn.</summary>
    private async void TakeRegionScreenshot()
    {
        if (_canvas is null || _toolbar is null) return;
        bool toolbarWasVisible = _toolbar.IsVisible;
        bool floatingWasVisible = _floating?.IsVisible == true;
        _toolbar.Hide();
        _floating?.Hide();
        await Task.Delay(250);

        try
        {
            var shot = ScreenCaptureService.Grab(_canvas.PhysicalBounds);
            var snip = new SnipWindow(shot, _canvas.PhysicalBounds) { Owner = _canvas };
            if (snip.ShowDialog() == true && snip.Result is { } img)
            {
                string path = ScreenCaptureService.Save(img);
                _tray?.ShowTip("Đã chụp vùng chọn", $"{Path.GetFileName(path)}\nĐã lưu vào Ảnh\\OmniPen và chép vào Clipboard.");
            }
        }
        catch (Exception ex)
        {
            ShowError("Không chụp được màn hình", ex);
        }
        finally
        {
            if (toolbarWasVisible) ShowToolbar();
            else if (floatingWasVisible) _floating?.Show();
        }
    }

    #endregion

    #region Chèn ảnh / camera / LaTeX

    private void InsertImageFromFile()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Chọn ảnh để chèn",
            Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            _canvas.InsertImage(File.ReadAllBytes(dlg.FileName));
        }
        catch (Exception ex)
        {
            ShowError("Không chèn được ảnh", ex);
        }
    }

    private void InsertFromClipboard()
    {
        if (_canvas is null) return;
        try
        {
            BitmapSource? img = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
            if (img is null)
            {
                _tray?.ShowTip("Clipboard không có ảnh", "Hãy sao chép (Ctrl+C) một ảnh trước.");
                return;
            }
            _canvas.InsertImage(ExportService.ToPng(img));
        }
        catch (Exception ex)
        {
            ShowError("Không dán được ảnh", ex);
        }
    }

    private void InsertFromCamera()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();
        var dlg = new CameraWindow { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.CapturedPng is { } png)
        {
            try { _canvas.InsertImage(png); }
            catch (Exception ex) { ShowError("Không chèn được ảnh", ex); }
        }
    }

    private void InsertLatex()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();
        var dlg = new LatexWindow(_settings, _canvas.GetState().PenColor) { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.ResultPng is not { } png) return;

        try
        {
            // dvipng dựng ở 600 DPI; phóng 1.6 lần để chữ công thức to tương đương chữ viết tay trên bảng.
            BitmapSource bmp = PageElements.Decode(png);
            var size = new Size(bmp.PixelWidth * 96.0 / dlg.ResultDpi * 1.6, bmp.PixelHeight * 96.0 / dlg.ResultDpi * 1.6);
            _canvas.InsertImage(png, "latex", dlg.Formula, size);
        }
        catch (Exception ex)
        {
            ShowError("Không chèn được công thức", ex);
        }
    }

    #endregion

    #region Mở / lưu / xuất

    private static string DocumentsFolder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OmniPen");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void SaveDocument()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Lưu tài liệu OmniPen",
            Filter = InkFileService.Filter,
            DefaultExt = InkFileService.Extension,
            InitialDirectory = (_currentFile is null ? null : Path.GetDirectoryName(_currentFile)) ?? DocumentsFolder,
            FileName = _currentFile is null ? $"BaiGiang_{DateTime.Now:yyyyMMdd_HHmm}" : Path.GetFileName(_currentFile)
        };
        if (dlg.ShowDialog(owner) != true) return;

        try
        {
            using (BusyCursor())
                _canvas.SaveDocument(dlg.FileName);
            _currentFile = dlg.FileName;
            _tray?.ShowTip("Đã lưu", Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            ShowError("Không lưu được tài liệu", ex);
        }
    }

    private bool ConfirmReplace(Window owner) =>
        _canvas?.Document.HasAnyContent != true ||
        MessageBox.Show(owner, "Nội dung hiện tại sẽ bị thay thế. Bạn đã lưu chưa?\n\nTiếp tục mở?", "OmniPen",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void OpenDocument()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();
        if (!ConfirmReplace(owner)) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Mở tài liệu OmniPen",
            Filter = InkFileService.Filter,
            InitialDirectory = DocumentsFolder
        };
        if (dlg.ShowDialog(owner) != true) return;

        try
        {
            using (BusyCursor())
                await _canvas.LoadDocumentAsync(dlg.FileName);
            _currentFile = dlg.FileName;
        }
        catch (Exception ex)
        {
            ShowError("Không mở được tài liệu", ex);
        }
    }

    private async void OpenPdf()
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();
        if (!ConfirmReplace(owner)) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Mở PDF để ghi chú",
            Filter = "Tài liệu PDF (*.pdf)|*.pdf",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(owner) != true) return;

        try
        {
            using (BusyCursor())
                await _canvas.OpenPdfAsync(dlg.FileName);
            _currentFile = null;
        }
        catch (Exception ex)
        {
            ShowError("Không mở được PDF (file có thể có mật khẩu hoặc bị hỏng)", ex);
        }
    }

    private enum ExportKind { Pdf, Png, PngTransparent, Svg }

    private async void Export(ExportKind kind)
    {
        if (_canvas is null) return;
        Window owner = DialogOwner();

        string ext = kind switch { ExportKind.Pdf => ".pdf", ExportKind.Svg => ".svg", _ => ".png" };
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Xuất file",
            Filter = kind switch { ExportKind.Pdf => ExportService.PdfFilter, ExportKind.Svg => ExportService.SvgFilter, _ => ExportService.PngFilter },
            DefaultExt = ext,
            InitialDirectory = DocumentsFolder,
            FileName = (_currentFile is null ? $"OmniPen_{DateTime.Now:yyyyMMdd_HHmm}" : Path.GetFileNameWithoutExtension(_currentFile)) +
                       (kind == ExportKind.PngTransparent ? "_trongsuot" : "")
        };
        if (dlg.ShowDialog(owner) != true) return;

        try
        {
            using (BusyCursor())
            {
                PaperPage page = _canvas.ExportCurrentPage;
                Size size = _canvas.PageSize(page);
                switch (kind)
                {
                    case ExportKind.Pdf:
                        await ExportService.ExportPdfAsync(dlg.FileName, _canvas.PagesForPdf());
                        break;
                    case ExportKind.Png:
                        await ExportService.ExportPngAsync(dlg.FileName, page, size, transparent: false);
                        break;
                    case ExportKind.PngTransparent:
                        await ExportService.ExportPngAsync(dlg.FileName, page, size, transparent: true);
                        break;
                    case ExportKind.Svg:
                        await ExportService.ExportSvgAsync(dlg.FileName, page, size, background: page.Paper != PaperType.None);
                        break;
                }
            }
            _tray?.ShowTip("Đã xuất file", Path.GetFileName(dlg.FileName));
            OpenFolderSelect(dlg.FileName);
        }
        catch (Exception ex)
        {
            ShowError("Không xuất được file", ex);
        }
    }

    private static IDisposable BusyCursor()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        return new ActionDisposable(() => Mouse.OverrideCursor = null);
    }

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch { /* bỏ qua */ }
    }

    private static void OpenFolderSelect(string file)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
        catch { /* bỏ qua */ }
    }

    private void RequestExit()
    {
        if (_canvas?.Document.HasAnyContent == true)
        {
            Window owner = DialogOwner();
            var r = MessageBox.Show(owner, "Thoát OmniPen? Nội dung chưa lưu sẽ bị mất.", "OmniPen",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }
        Shutdown();
    }

    private void ShowError(string title, Exception ex)
    {
        LogError(ex);
        string text = $"{title}.\n\n{ex.Message}";
        if (_toolbar is { IsVisible: true })
            MessageBox.Show(_toolbar, text, "OmniPen", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            MessageBox.Show(text, "OmniPen", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    #endregion

    #region Lỗi & thoát

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Giữ app sống: đang giảng bài mà app tắt đột ngột là mất hết nét vẽ.
        ShowError("Đã xảy ra lỗi không mong muốn", e.Exception);
        e.Handled = true;
    }

    internal static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(Path.Combine(SettingsService.Folder, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppInfo.Version}\n{ex}\n\n");
        }
        catch { /* không ghi được log thì thôi */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_canvas is not null)
        {
            CanvasState s = _canvas.GetState();
            _settings.PenColor = s.PenColor.ToString();
            _settings.PenSize = s.PenSize;
            _settings.AutoScroll = s.AutoScroll;
        }
        if (_toolbar?.GetPhysicalPosition() is { } pos)
        {
            _settings.ToolbarX = pos.X;
            _settings.ToolbarY = pos.Y;
        }
        if (_floating?.GetPhysicalPosition() is { } fpos)
        {
            _settings.FloatingX = fpos.X;
            _settings.FloatingY = fpos.Y;
        }
        if (_mutex is not null) SettingsService.Save(_settings);

        _hotkeys?.Dispose();
        _inputHooks?.Dispose();
        foreach (var p in _blurPatches) p.Close();
        _tray?.Dispose();
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    #endregion
}
