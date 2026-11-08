using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace OmniPen.Services;

/// <summary>
/// Dựng công thức LaTeX thành ảnh PNG nền trong suốt bằng bộ LaTeX cài trên máy
/// (MiKTeX hoặc TeX Live): latex → file .dvi → dvipng → .png.
/// Dùng dvipng thay vì PDF vì cho ảnh nền trong suốt, cắt sát công thức (-T tight) và nhanh.
/// </summary>
public static class LatexService
{
    public const string DownloadUrl = "https://miktex.org/download";

    /// <summary>Tìm thư mục chứa latex.exe và dvipng.exe. Trả null nếu chưa cài.</summary>
    public static string? FindBinFolder(string? customFolder)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(customFolder)) candidates.Add(customFolder);

        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(local, @"Programs\MiKTeX\miktex\bin\x64"));
        candidates.Add(Path.Combine(pf, @"MiKTeX\miktex\bin\x64"));
        candidates.Add(Path.Combine(local, @"Programs\MiKTeX\miktex\bin"));

        // TeX Live: C:\texlive\2025\bin\windows (bản mới) hoặc \win32 (bản cũ).
        try
        {
            if (Directory.Exists(@"C:\texlive"))
                foreach (string year in Directory.GetDirectories(@"C:\texlive").OrderByDescending(d => d))
                {
                    candidates.Add(Path.Combine(year, @"bin\windows"));
                    candidates.Add(Path.Combine(year, @"bin\win32"));
                }
        }
        catch { /* không có quyền đọc */ }

        foreach (string dir in candidates)
        {
            try
            {
                string d = dir.Trim().Trim('"');
                if (File.Exists(Path.Combine(d, "latex.exe")) && File.Exists(Path.Combine(d, "dvipng.exe")))
                    return d;
            }
            catch { /* đường dẫn PATH không hợp lệ */ }
        }
        return null;
    }

    /// <summary>Dựng công thức. Trả về (png, độ phân giải DPI đã dùng).</summary>
    /// <exception cref="InvalidOperationException">Lỗi cú pháp LaTeX (thông báo lấy từ log).</exception>
    public static async Task<(byte[] Png, int Dpi)> RenderAsync(string formula, Color color, string binFolder,
        CancellationToken ct = default)
    {
        const int dpi = 600;
        string work = Path.Combine(Path.GetTempPath(), "OmniPen", "latex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            string body = formula.Trim();
            // Người dùng gõ công thức "trần" (x^2+1) → tự đặt vào môi trường toán hiển thị.
            // Nếu đã tự viết $...$, \[...\] hay \begin{...} thì giữ nguyên.
            bool hasMath = body.StartsWith('$') || body.StartsWith(@"\[") || body.StartsWith(@"\begin");
            if (!hasMath) body = $@"$\displaystyle {body}$";

            string tex = $$"""
                \documentclass[12pt]{article}
                \usepackage[utf8]{inputenc}
                \usepackage{amsmath,amssymb,amsfonts}
                \usepackage{xcolor}
                \pagestyle{empty}
                \begin{document}
                \color[RGB]{{{color.R}},{{color.G}},{{color.B}}}
                {{body}}
                \end{document}
                """;
            await File.WriteAllTextAsync(Path.Combine(work, "f.tex"), tex, new UTF8Encoding(false), ct);

            var (code, output) = await RunAsync(Path.Combine(binFolder, "latex.exe"),
                "-interaction=nonstopmode -halt-on-error f.tex", work, ct);
            if (code != 0 || !File.Exists(Path.Combine(work, "f.dvi")))
                throw new InvalidOperationException(ExtractError(work, output));

            (code, output) = await RunAsync(Path.Combine(binFolder, "dvipng.exe"),
                $"-D {dpi} -T tight -bg Transparent -o f.png f.dvi", work, ct);
            string png = Path.Combine(work, "f.png");
            if (code != 0 || !File.Exists(png))
                throw new InvalidOperationException("dvipng không tạo được ảnh.\n" + output.Trim());

            return (await File.ReadAllBytesAsync(png, ct), dpi);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* file đang bị khóa – để Windows dọn %TEMP% */ }
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true // để latex không chờ người dùng gõ khi gặp lỗi
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Không chạy được {exe}");
        p.StandardInput.Close();

        Task<string> outTask = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> errTask = p.StandardError.ReadToEndAsync(ct);

        // MiKTeX có thể tự tải gói còn thiếu ở lần chạy đầu → cho tối đa 60 giây.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new InvalidOperationException("LaTeX chạy quá lâu (quá 60 giây) nên đã bị dừng.");
        }
        return (p.ExitCode, await outTask + await errTask);
    }

    /// <summary>Lấy dòng lỗi "! ..." trong log LaTeX để báo người dùng dễ hiểu.</summary>
    private static string ExtractError(string work, string output)
    {
        string log = output;
        try
        {
            string logFile = Path.Combine(work, "f.log");
            if (File.Exists(logFile)) log = File.ReadAllText(logFile);
        }
        catch { }

        var lines = log.Split('\n');
        int i = Array.FindIndex(lines, l => l.StartsWith('!'));
        if (i < 0) return "LaTeX báo lỗi khi dịch công thức.";
        return "Lỗi LaTeX:\n" + string.Join("\n", lines.Skip(i).Take(4)).Trim();
    }
}
