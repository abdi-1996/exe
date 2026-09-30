using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuickVideoCutter;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    readonly TextBox inputBox = new() { ReadOnly = true, PlaceholderText = "Выберите видео..." };
    readonly TextBox outputBox = new() { PlaceholderText = "Куда сохранить MP4..." };
    readonly TextBox startBox = new() { Text = "00:00:00.000" };
    readonly TextBox endBox = new() { Text = "00:00:10.000" };
    readonly ComboBox modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button cutButton = new() { Text = "ВЫРЕЗАТЬ", Height = 44 };
    readonly Button cancelButton = new() { Text = "Отмена", Height = 44, Enabled = false };
    readonly Button folderButton = new() { Text = "Открыть папку", Enabled = false };
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 18 };
    readonly Label status = new() { Text = "Готово к работе", AutoSize = true };
    Process? activeProcess;
    CancellationTokenSource? cts;

    public MainForm()
    {
        Text = "Quick Video Cutter";
        Width = 720;
        Height = 430;
        MinimumSize = new System.Drawing.Size(680, 410);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new System.Drawing.Font("Segoe UI", 10F);
        AllowDrop = true;

        modeBox.Items.AddRange(new object[] {
            "Быстро — без перекодирования",
            "Точно — H.264 / AAC"
        });
        modeBox.SelectedIndex = 0;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 10 };
        root.RowStyles.Clear();
        for (int i = 0; i < 10; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label { Text = "Quick Video Cutter", Font = new System.Drawing.Font("Segoe UI Semibold", 22F), AutoSize = true, Margin = new Padding(0,0,0,6) };
        var subtitle = new Label { Text = "Быстрая резка видео на ПК", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(0,0,0,18) };
        root.Controls.Add(title);
        root.Controls.Add(subtitle);

        root.Controls.Add(MakeFileRow("Видео", inputBox, "Выбрать", PickInput));
        root.Controls.Add(MakeTimesRow());
        root.Controls.Add(MakeLabeled("Режим", modeBox));
        root.Controls.Add(MakeFileRow("Сохранить", outputBox, "Обзор", PickOutput));

        var actions = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Margin = new Padding(0,16,0,10) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
        cutButton.Dock = DockStyle.Fill; cancelButton.Dock = DockStyle.Fill; folderButton.Dock = DockStyle.Fill;
        cutButton.Click += async (_, _) => await CutAsync();
        cancelButton.Click += (_, _) => CancelCut();
        folderButton.Click += (_, _) => OpenOutputFolder();
        actions.Controls.Add(cutButton,0,0); actions.Controls.Add(cancelButton,1,0); actions.Controls.Add(folderButton,2,0);
        root.Controls.Add(actions);
        progress.Dock = DockStyle.Top;
        root.Controls.Add(progress);
        status.Margin = new Padding(0,10,0,0);
        root.Controls.Add(status);

        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) SetInput(files[0]);
        };
    }

    Control MakeFileRow(string labelText, TextBox box, string buttonText, EventHandler handler)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Margin = new Padding(0,0,0,10) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        var lab = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left };
        box.Dock = DockStyle.Fill;
        var btn = new Button { Text = buttonText, Dock = DockStyle.Fill, Height = 34 };
        btn.Click += handler;
        panel.Controls.Add(lab,0,0); panel.Controls.Add(box,1,0); panel.Controls.Add(btn,2,0);
        return panel;
    }

    Control MakeTimesRow()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, Margin = new Padding(0,0,0,10) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.Controls.Add(new Label { Text = "Начало", AutoSize = true, Anchor = AnchorStyles.Left },0,0);
        startBox.Dock = DockStyle.Fill; panel.Controls.Add(startBox,1,0);
        panel.Controls.Add(new Label { Text = "Конец", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(12,0,0,0) },2,0);
        endBox.Dock = DockStyle.Fill; panel.Controls.Add(endBox,3,0);
        return panel;
    }

    Control MakeLabeled(string labelText, Control control)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0,0,0,10) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left },0,0);
        control.Dock = DockStyle.Fill; panel.Controls.Add(control,1,0);
        return panel;
    }

    void PickInput(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Filter = "Видео|*.mp4;*.mov;*.mkv;*.avi;*.m4v;*.webm;*.mts;*.m2ts|Все файлы|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetInput(dlg.FileName);
    }

    void SetInput(string path)
    {
        inputBox.Text = path;
        var dir = Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = Path.GetFileNameWithoutExtension(path);
        outputBox.Text = Path.Combine(dir, name + "_cut.mp4");
        folderButton.Enabled = false;
    }

    void PickOutput(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog { Filter = "MP4 видео|*.mp4", DefaultExt = "mp4", AddExtension = true, FileName = string.IsNullOrWhiteSpace(outputBox.Text) ? "cut.mp4" : Path.GetFileName(outputBox.Text) };
        if (!string.IsNullOrWhiteSpace(outputBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(outputBox.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) outputBox.Text = dlg.FileName;
    }

    async Task CutAsync()
    {
        if (!File.Exists(inputBox.Text)) { MessageBox.Show(this, "Сначала выберите видео.", "Video Cutter", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!TryParseTime(startBox.Text, out var start) || !TryParseTime(endBox.Text, out var end) || end <= start)
        { MessageBox.Show(this, "Проверьте время. Пример: 00:01:12.500", "Video Cutter", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.IsNullOrWhiteSpace(outputBox.Text)) { MessageBox.Show(this, "Укажите файл сохранения."); return; }

        try
        {
            SetBusy(true);
            cts = new CancellationTokenSource();
            status.Text = "Подготовка FFmpeg...";
            var ffmpeg = await EnsureFfmpegAsync(cts.Token);
            status.Text = "Режу видео...";
            progress.Value = 0;

            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(inputBox.Text);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add((end-start).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            if (modeBox.SelectedIndex == 0)
            {
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-avoid_negative_ts"); psi.ArgumentList.Add("make_zero");
            }
            else
            {
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("18");
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("192k");
                psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            }
            psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1"); psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add(outputBox.Text);

            activeProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            activeProcess.Start();
            var stderrTask = activeProcess.StandardError.ReadToEndAsync();
            var durationSec = Math.Max(0.001, (end-start).TotalSeconds);
            while (!activeProcess.StandardOutput.EndOfStream)
            {
                cts.Token.ThrowIfCancellationRequested();
                var line = await activeProcess.StandardOutput.ReadLineAsync();
                if (line == null) break;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us))
                    progress.Value = Math.Clamp((int)Math.Round((us / 1_000_000.0) / durationSec * 100), 0, 100);
                else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) && long.TryParse(line[12..], out var ms))
                    progress.Value = Math.Clamp((int)Math.Round((ms / 1_000_000.0) / durationSec * 100), 0, 100);
            }
            await activeProcess.WaitForExitAsync(cts.Token);
            var err = await stderrTask;
            if (activeProcess.ExitCode != 0) throw new Exception(string.IsNullOrWhiteSpace(err) ? "FFmpeg завершился с ошибкой." : LastLines(err, 12));
            progress.Value = 100;
            status.Text = "Готово: " + Path.GetFileName(outputBox.Text);
            folderButton.Enabled = true;
            MessageBox.Show(this, "Фрагмент сохранён!", "Video Cutter", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { status.Text = "Операция отменена"; }
        catch (Exception ex) { status.Text = "Ошибка"; MessageBox.Show(this, ex.Message, "Video Cutter — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { activeProcess?.Dispose(); activeProcess = null; cts?.Dispose(); cts = null; SetBusy(false); }
    }

    void CancelCut()
    {
        try { cts?.Cancel(); if (activeProcess is { HasExited: false }) activeProcess.Kill(true); } catch { }
    }

    void SetBusy(bool busy)
    {
        cutButton.Enabled = !busy; cancelButton.Enabled = busy; modeBox.Enabled = !busy; startBox.Enabled = !busy; endBox.Enabled = !busy;
    }

    void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(outputBox.Text)) return;
        var dir = Path.GetDirectoryName(outputBox.Text);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    static bool TryParseTime(string text, out TimeSpan value)
    {
        value = default;
        text = text.Trim().Replace(',', '.');
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value) && value >= TimeSpan.Zero) return true;
        var p = text.Split(':');
        try
        {
            double sec; int min=0, hour=0;
            if (p.Length == 1) sec = double.Parse(p[0], CultureInfo.InvariantCulture);
            else if (p.Length == 2) { min = int.Parse(p[0]); sec = double.Parse(p[1], CultureInfo.InvariantCulture); }
            else if (p.Length == 3) { hour = int.Parse(p[0]); min = int.Parse(p[1]); sec = double.Parse(p[2], CultureInfo.InvariantCulture); }
            else return false;
            if (sec < 0 || min < 0 || hour < 0) return false;
            value = TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
            return true;
        } catch { return false; }
    }

    static async Task<string> EnsureFfmpegAsync(CancellationToken token)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickVideoCutter");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "ffmpeg.exe");
        if (File.Exists(exe)) return exe;

        var zipPath = Path.Combine(dir, "ffmpeg.zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QuickVideoCutter/1.0");
        using (var response = await http.GetAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(token);
            await using var dst = File.Create(zipPath);
            await src.CopyToAsync(dst, token);
        }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = System.Linq.Enumerable.FirstOrDefault(zip.Entries, e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new InvalidDataException("В архиве FFmpeg не найден ffmpeg.exe");
            entry.ExtractToFile(exe, true);
        }
        try { File.Delete(zipPath); } catch { }
        return exe;
    }

    static string LastLines(string text, int count)
    {
        var lines = text.Replace("", "").Split('
', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Length <= count ? lines : lines[(lines.Length-count)..]);
    }
}
