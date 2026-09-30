using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuickVideoCutter;

public sealed class App : Application
{
    [STAThread]
    public static void Main()
    {
        try
        {
            var app = new App
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose
            };
            app.DispatcherUnhandledException += (_, e) =>
            {
                WriteCrashLog(e.Exception);
                MessageBox.Show("Ошибка приложения:\n\n" + e.Exception.Message + "\n\nЛог сохранён в %LOCALAPPDATA%\\QuickVideoCutter\\crash.log",
                    "Quick Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            app.Run(new MainWindow());
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show("Не удалось запустить Quick Video Cutter:\n\n" + ex.Message + "\n\nЛог сохранён в %LOCALAPPDATA%\\QuickVideoCutter\\crash.log",
                "Quick Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static void WriteCrashLog(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickVideoCutter");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                ex + Environment.NewLine + new string('-', 80) + Environment.NewLine);
        }
        catch { }
    }
}

public sealed class MainWindow : Window
{
    readonly Grid root = new();
    readonly MediaElement preview = new();
    readonly TimelineControl timeline = new();
    readonly TextBlock fileNameText = new();
    readonly TextBlock statusText = new();
    readonly TextBlock currentTimeText = new();
    readonly TextBlock rangeText = new();
    Button playButton = null!;
    Button exportButton = null!;
    Button cancelButton = null!;
    readonly ComboBox modeBox = new();
    readonly DispatcherTimer timer;
    string? inputPath;
    string? outputPath;
    TimeSpan duration = TimeSpan.Zero;
    bool isPlaying;
    bool userSeeking;
    Process? activeProcess;
    CancellationTokenSource? activeCts;

    static readonly Brush Bg = Brush("#0B0D10");
    static readonly Brush Panel = Brush("#15181E");
    static readonly Brush Panel2 = Brush("#1B1F27");
    static readonly Brush Text = Brush("#F6F7F9");
    static readonly Brush Muted = Brush("#9AA3B2");
    static readonly Brush Accent = Brush("#6C7CFF");
    static readonly Brush BorderBrush = Brush("#2A303A");

    public MainWindow()
    {
        Title = "Quick Video Cutter";
        Width = 1120;
        Height = 760;
        MinWidth = 860;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI");
        AllowDrop = true;

        root.Margin = new Thickness(24);
        Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var previewCard = BuildPreviewCard();
        Grid.SetRow(previewCard, 1);
        root.Children.Add(previewCard);

        var timelineCard = BuildTimelineCard();
        Grid.SetRow(timelineCard, 2);
        root.Children.Add(timelineCard);

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (!isPlaying || userSeeking || inputPath == null) return;
            if (preview.Position >= timeline.End)
            {
                preview.Pause();
                isPlaying = false;
                playButton.Content = "▶";
                preview.Position = timeline.Start;
                timeline.SetPosition(timeline.Start);
                UpdateTimeLabels(timeline.Start);
                return;
            }

            timeline.SetPosition(preview.Position);
            UpdateTimeLabels(preview.Position);
        };
        timer.Start();

        timeline.SelectionChanged += (_, _) =>
        {
            rangeText.Text = $"{FormatTime(timeline.Start)}  →  {FormatTime(timeline.End)}   •   {FormatTime(timeline.End - timeline.Start)}";
        };
        timeline.SeekRequested += (_, value) =>
        {
            if (inputPath == null) return;
            userSeeking = true;
            preview.Position = value;
            timeline.SetPosition(value);
            UpdateTimeLabels(value);
            userSeeking = false;
        };

        preview.MediaOpened += (_, _) =>
        {
            if (duration == TimeSpan.Zero && preview.NaturalDuration.HasTimeSpan)
            {
                duration = preview.NaturalDuration.TimeSpan;
                timeline.SetDuration(duration);
                timeline.SetSelection(TimeSpan.Zero, duration);
                rangeText.Text = $"{FormatTime(TimeSpan.Zero)}  →  {FormatTime(duration)}   •   {FormatTime(duration)}";
            }
        };
        preview.MediaEnded += (_, _) =>
        {
            isPlaying = false;
            playButton.Content = "▶";
        };

        DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
        };
        Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                await LoadVideoAsync(files[0]);
        };
    }

    FrameworkElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = "Quick Video Cutter",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Text
        });
        left.Children.Add(new TextBlock
        {
            Text = "Preview • Timeline • Precision trim",
            FontSize = 13,
            Foreground = Muted,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var open = MakeButton("＋  Открыть видео", Accent, Text, 150);
        open.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Видео|*.mp4;*.mov;*.mkv;*.avi;*.m4v;*.webm;*.mts;*.m2ts|Все файлы|*.*"
            };
            if (dlg.ShowDialog(this) == true)
                await LoadVideoAsync(dlg.FileName);
        };

        grid.Children.Add(left);
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);
        return grid;
    }

    FrameworkElement BuildPreviewCard()
    {
        var border = Card(new Thickness(0, 0, 0, 16));
        border.Name = "PreviewCard";

        var outer = new Grid { Margin = new Thickness(14) };
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var videoWrap = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(16),
            MinHeight = 330,
            ClipToBounds = true
        };
        var videoGrid = new Grid();
        preview.Stretch = Stretch.Uniform;
        preview.LoadedBehavior = MediaState.Manual;
        preview.UnloadedBehavior = MediaState.Manual;
        preview.ScrubbingEnabled = true;
        videoGrid.Children.Add(preview);

        var placeholder = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        placeholder.Children.Add(new TextBlock
        {
            Text = "🎬",
            FontSize = 54,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Muted
        });
        placeholder.Children.Add(new TextBlock
        {
            Text = "Откройте видео или перетащите его сюда",
            FontSize = 15,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Muted,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        placeholder.Name = "Placeholder";
        videoGrid.Children.Add(placeholder);
        videoWrap.Child = videoGrid;

        var controls = new Grid { Margin = new Thickness(2, 14, 2, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = MakeIconButton("−1s");
        back.Click += (_, _) => SeekBy(-1);
        controls.Children.Add(back);

        var play = MakeIconButton("▶");
        playButton = play;
        play.Margin = new Thickness(8, 0, 8, 0);
        play.Click += (_, _) => TogglePlayback();
        Grid.SetColumn(play, 1);
        controls.Children.Add(play);

        var forward = MakeIconButton("+1s");
        forward.Click += (_, _) => SeekBy(1);
        Grid.SetColumn(forward, 2);
        controls.Children.Add(forward);

        fileNameText.Text = "Нет видео";
        fileNameText.Foreground = Muted;
        fileNameText.VerticalAlignment = VerticalAlignment.Center;
        fileNameText.TextTrimming = TextTrimming.CharacterEllipsis;
        fileNameText.Margin = new Thickness(14, 0, 14, 0);
        Grid.SetColumn(fileNameText, 3);
        controls.Children.Add(fileNameText);

        currentTimeText.Text = "00:00.000 / 00:00.000";
        currentTimeText.Foreground = Text;
        currentTimeText.FontFamily = new FontFamily("Consolas");
        currentTimeText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(currentTimeText, 4);
        controls.Children.Add(currentTimeText);

        outer.Children.Add(videoWrap);
        Grid.SetRow(controls, 1);
        outer.Children.Add(controls);

        border.Child = outer;
        return border;
    }

    FrameworkElement BuildTimelineCard()
    {
        var border = Card(new Thickness(0, 0, 0, 16));
        var stack = new StackPanel { Margin = new Thickness(16) };

        var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        top.Children.Add(new TextBlock
        {
            Text = "Дорожка",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Text
        });

        rangeText.Text = "00:00.000  →  00:00.000";
        rangeText.Foreground = Muted;
        rangeText.FontFamily = new FontFamily("Consolas");
        Grid.SetColumn(rangeText, 1);
        top.Children.Add(rangeText);

        timeline.Height = 112;
        timeline.HorizontalAlignment = HorizontalAlignment.Stretch;
        timeline.Margin = new Thickness(0, 2, 0, 10);

        var hint = new TextBlock
        {
            Text = "Перетаскивайте левый и правый маркеры для обрезки. Клик по дорожке перемещает курсор.",
            FontSize = 12,
            Foreground = Muted
        };

        stack.Children.Add(top);
        stack.Children.Add(timeline);
        stack.Children.Add(hint);
        border.Child = stack;
        return border;
    }

    FrameworkElement BuildFooter()
    {
        var border = Card(new Thickness(0));
        border.Padding = new Thickness(16);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        row.Children.Add(new TextBlock
        {
            Text = "Экспорт:",
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        modeBox.Items.Add("Быстро — без перекодирования");
        modeBox.Items.Add("Точно — H.264 / AAC");
        modeBox.SelectedIndex = 0;
        modeBox.MinWidth = 240;
        modeBox.Height = 38;
        modeBox.Background = Panel2;
        modeBox.Foreground = Text;
        modeBox.BorderBrush = BorderBrush;
        modeBox.Padding = new Thickness(10, 5, 10, 5);
        row.Children.Add(modeBox);

        left.Children.Add(row);
        statusText.Text = "Готово к работе";
        statusText.Foreground = Muted;
        statusText.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(statusText);
        grid.Children.Add(left);

        var cancel = MakeButton("Отмена", Panel2, Text, 110);
        cancelButton = cancel;
        cancel.IsEnabled = false;
        cancel.Margin = new Thickness(12, 0, 0, 0);
        cancel.Click += (_, _) => CancelExport();
        Grid.SetColumn(cancel, 1);
        grid.Children.Add(cancel);

        var export = MakeButton("Экспортировать", Accent, Text, 160);
        exportButton = export;
        export.Margin = new Thickness(12, 0, 0, 0);
        export.Click += async (_, _) => await ExportAsync();
        Grid.SetColumn(export, 2);
        grid.Children.Add(export);

        border.Child = grid;
        return border;
    }

    async Task LoadVideoAsync(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            inputPath = path;
            outputPath = Path.Combine(
                Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.GetFileNameWithoutExtension(path) + "_cut.mp4");

            statusText.Text = "Открываю видео...";
            fileNameText.Text = Path.GetFileName(path);
            preview.Stop();
            preview.Source = new Uri(path, UriKind.Absolute);
            preview.Position = TimeSpan.Zero;
            preview.Play();
            preview.Pause();
            isPlaying = false;
            playButton.Content = "▶";

            var placeholder = FindVisualChild<StackPanel>(root, "Placeholder");
            if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;

            var tools = await EnsureFfmpegAsync(CancellationToken.None);
            duration = await ProbeDurationAsync(tools.ffprobe, path);
            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.FromSeconds(1);

            timeline.SetDuration(duration);
            timeline.SetSelection(TimeSpan.Zero, duration);
            timeline.SetPosition(TimeSpan.Zero);
            UpdateTimeLabels(TimeSpan.Zero);
            rangeText.Text = $"{FormatTime(TimeSpan.Zero)}  →  {FormatTime(duration)}   •   {FormatTime(duration)}";

            statusText.Text = "Создаю миниатюры дорожки...";
            var thumbs = await GenerateThumbnailsAsync(tools.ffmpeg, path, duration);
            timeline.SetThumbnails(thumbs);
            statusText.Text = "Готово";
        }
        catch (Exception ex)
        {
            statusText.Text = "Ошибка загрузки";
            MessageBox.Show(this, ex.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void TogglePlayback()
    {
        if (inputPath == null) return;

        if (isPlaying)
        {
            preview.Pause();
            isPlaying = false;
            playButton.Content = "▶";
        }
        else
        {
            if (preview.Position < timeline.Start || preview.Position >= timeline.End)
                preview.Position = timeline.Start;
            preview.Play();
            isPlaying = true;
            playButton.Content = "⏸";
        }
    }

    void SeekBy(double seconds)
    {
        if (inputPath == null) return;
        var next = preview.Position + TimeSpan.FromSeconds(seconds);
        if (next < TimeSpan.Zero) next = TimeSpan.Zero;
        if (next > duration) next = duration;
        preview.Position = next;
        timeline.SetPosition(next);
        UpdateTimeLabels(next);
    }

    void UpdateTimeLabels(TimeSpan current)
    {
        currentTimeText.Text = $"{FormatTime(current)} / {FormatTime(duration)}";
    }

    async Task ExportAsync()
    {
        if (inputPath == null || !File.Exists(inputPath))
        {
            MessageBox.Show(this, "Сначала откройте видео.", "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var save = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileName(outputPath ?? "cut.mp4"),
            InitialDirectory = Path.GetDirectoryName(outputPath ?? inputPath)
        };

        if (save.ShowDialog(this) != true) return;
        outputPath = save.FileName;

        try
        {
            SetBusy(true);
            activeCts = new CancellationTokenSource();
            var tools = await EnsureFfmpegAsync(activeCts.Token);

            var start = timeline.Start;
            var end = timeline.End;
            var span = end - start;
            if (span <= TimeSpan.Zero)
                throw new InvalidOperationException("Диапазон обрезки пустой.");

            statusText.Text = "Экспорт 0%";

            var psi = new ProcessStartInfo(tools.ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            void A(string v) => psi.ArgumentList.Add(v);

            A("-hide_banner"); A("-y");
            A("-ss"); A(start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            A("-i"); A(inputPath);
            A("-t"); A(span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

            if (modeBox.SelectedIndex == 0)
            {
                A("-c"); A("copy");
                A("-avoid_negative_ts"); A("make_zero");
            }
            else
            {
                A("-c:v"); A("libx264");
                A("-preset"); A("veryfast");
                A("-crf"); A("18");
                A("-c:a"); A("aac");
                A("-b:a"); A("192k");
                A("-movflags"); A("+faststart");
            }

            A("-progress"); A("pipe:1");
            A("-nostats");
            A(outputPath);

            activeProcess = new Process { StartInfo = psi };
            activeProcess.Start();
            var errTask = activeProcess.StandardError.ReadToEndAsync();

            while (!activeProcess.StandardOutput.EndOfStream)
            {
                activeCts.Token.ThrowIfCancellationRequested();
                var line = await activeProcess.StandardOutput.ReadLineAsync();
                if (line == null) break;

                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line[12..], out var us))
                {
                    var pct = Math.Clamp((int)Math.Round((us / 1_000_000.0) / Math.Max(0.001, span.TotalSeconds) * 100), 0, 100);
                    statusText.Text = $"Экспорт {pct}%";
                }
            }

            await activeProcess.WaitForExitAsync(activeCts.Token);
            var err = await errTask;

            if (activeProcess.ExitCode != 0)
                throw new Exception(string.IsNullOrWhiteSpace(err) ? "FFmpeg завершился с ошибкой." : LastLines(err, 10));

            statusText.Text = "Экспорт завершён";
            MessageBox.Show(this, "Видео сохранено.", "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Экспорт отменён";
        }
        catch (Exception ex)
        {
            statusText.Text = "Ошибка экспорта";
            MessageBox.Show(this, ex.Message, "Video Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            activeProcess?.Dispose();
            activeProcess = null;
            activeCts?.Dispose();
            activeCts = null;
            SetBusy(false);
        }
    }

    void CancelExport()
    {
        try
        {
            activeCts?.Cancel();
            if (activeProcess is { HasExited: false })
                activeProcess.Kill(true);
        }
        catch { }
    }

    void SetBusy(bool busy)
    {
        exportButton.IsEnabled = !busy;
        cancelButton.IsEnabled = busy;
        modeBox.IsEnabled = !busy;
    }

    static async Task<(string ffmpeg, string ffprobe)> EnsureFfmpegAsync(CancellationToken token)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickVideoCutter");
        Directory.CreateDirectory(dir);

        var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
        var ffprobe = Path.Combine(dir, "ffprobe.exe");

        if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            return (ffmpeg, ffprobe);

        var zipPath = Path.Combine(dir, "ffmpeg.zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QuickVideoCutter/2.0");

        using (var response = await http.GetAsync(
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            HttpCompletionOption.ResponseHeadersRead,
            token))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(token);
            await using var dst = File.Create(zipPath);
            await src.CopyToAsync(dst, token);
        }

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var f1 = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException("ffmpeg.exe не найден в архиве.");
            var f2 = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException("ffprobe.exe не найден в архиве.");
            f1.ExtractToFile(ffmpeg, true);
            f2.ExtractToFile(ffprobe, true);
        }

        try { File.Delete(zipPath); } catch { }
        return (ffmpeg, ffprobe);
    }

    static async Task<TimeSpan> ProbeDurationAsync(string ffprobe, string file)
    {
        var psi = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(file);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffprobe.");
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0)
            return TimeSpan.FromSeconds(sec);

        return TimeSpan.Zero;
    }

    static async Task<List<BitmapImage>> GenerateThumbnailsAsync(string ffmpeg, string file, TimeSpan duration)
    {
        var temp = Path.Combine(Path.GetTempPath(), "QuickVideoCutter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var pattern = Path.Combine(temp, "thumb_%02d.jpg");
        var interval = Math.Max(0.2, duration.TotalSeconds / 12.0);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(file);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"fps=1/{interval.ToString("0.###", CultureInfo.InvariantCulture)},scale=240:-2");
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("12");
        psi.ArgumentList.Add("-q:v");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add(pattern);

        using (var p = Process.Start(psi))
        {
            if (p != null)
                await p.WaitForExitAsync();
        }

        var list = new List<BitmapImage>();
        foreach (var path in Directory.GetFiles(temp, "thumb_*.jpg").OrderBy(x => x))
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            list.Add(img);
        }

        try { Directory.Delete(temp, true); } catch { }
        return list;
    }

    static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        return t.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    static string LastLines(string text, int count)
    {
        var lines = text.Split((char)10, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.TrimEnd((char)13))
                        .ToArray();
        return string.Join(Environment.NewLine, lines.Length <= count ? lines : lines[(lines.Length - count)..]);
    }

    static Border Card(Thickness margin) => new()
    {
        Background = Panel,
        BorderBrush = BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(18),
        Margin = margin
    };

    static Button MakeButton(string text, Brush background, Brush foreground, double width)
    {
        var b = new Button
        {
            Content = text,
            Width = width,
            Height = 42,
            Background = background,
            Foreground = foreground,
            BorderBrush = Brushes.Transparent,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        return b;
    }

    static Button MakeIconButton(string text)
    {
        return new Button
        {
            Content = text,
            Width = 50,
            Height = 38,
            Background = Panel2,
            Foreground = Text,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
    }

    static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name) return typed;
            var nested = FindVisualChild<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }
}

public sealed class TimelineControl : FrameworkElement
{
    readonly List<BitmapImage> thumbnails = new();
    TimeSpan duration = TimeSpan.FromSeconds(1);
    TimeSpan start = TimeSpan.Zero;
    TimeSpan end = TimeSpan.FromSeconds(1);
    TimeSpan position = TimeSpan.Zero;
    DragMode dragMode = DragMode.None;

    public event EventHandler? SelectionChanged;
    public event EventHandler<TimeSpan>? SeekRequested;

    public TimeSpan Start => start;
    public TimeSpan End => end;

    enum DragMode { None, Start, End, Position }

    public TimelineControl()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
    }

    public void SetDuration(TimeSpan value)
    {
        duration = value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : value;
        start = TimeSpan.Zero;
        end = duration;
        position = TimeSpan.Zero;
        InvalidateVisual();
    }

    public void SetSelection(TimeSpan a, TimeSpan b)
    {
        start = Clamp(a);
        end = Clamp(b);
        if (end < start) (start, end) = (end, start);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SetPosition(TimeSpan value)
    {
        position = Clamp(value);
        InvalidateVisual();
    }

    public void SetThumbnails(IEnumerable<BitmapImage> images)
    {
        thumbnails.Clear();
        thumbnails.AddRange(images);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = Math.Max(1, ActualWidth);
        var h = Math.Max(1, ActualHeight);
        var r = new Rect(0, 0, w, h);

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(12, 14, 18)), null, r, 12, 12);

        if (thumbnails.Count > 0)
        {
            var cellW = w / thumbnails.Count;
            for (var i = 0; i < thumbnails.Count; i++)
            {
                var cell = new Rect(i * cellW, 0, cellW + 1, h);
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawImage(thumbnails[i], cell);
                dc.Pop();
            }
        }
        else
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(26, 30, 37)), null, r, 12, 12);
            var ft = new FormattedText(
                "Timeline preview",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                14,
                new SolidColorBrush(Color.FromRgb(132, 142, 158)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(14, (h - ft.Height) / 2));
        }

        var sx = X(start);
        var ex = X(end);
        var px = X(position);

        var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        if (sx > 0) dc.DrawRectangle(shade, null, new Rect(0, 0, sx, h));
        if (ex < w) dc.DrawRectangle(shade, null, new Rect(ex, 0, w - ex, h));

        var accent = new SolidColorBrush(Color.FromRgb(108, 124, 255));
        dc.DrawRectangle(null, new Pen(accent, 3), new Rect(sx, 1.5, Math.Max(1, ex - sx), h - 3));

        DrawHandle(dc, sx, h, accent, true);
        DrawHandle(dc, ex, h, accent, false);

        var playPen = new Pen(Brushes.White, 2);
        dc.DrawLine(playPen, new Point(px, 0), new Point(px, h));
        dc.DrawEllipse(Brushes.White, null, new Point(px, 8), 4, 4);
    }

    static void DrawHandle(DrawingContext dc, double x, double h, Brush accent, bool left)
    {
        var width = 13.0;
        var rect = new Rect(left ? x : x - width, 0, width, h);
        dc.DrawRoundedRectangle(accent, null, rect, 6, 6);

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.4);
        var cx = left ? x + 6 : x - 6;
        dc.DrawLine(pen, new Point(cx - 1.8, h / 2 - 9), new Point(cx - 1.8, h / 2 + 9));
        dc.DrawLine(pen, new Point(cx + 1.8, h / 2 - 9), new Point(cx + 1.8, h / 2 + 9));
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        var x = e.GetPosition(this).X;
        var sx = X(start);
        var ex = X(end);

        if (Math.Abs(x - sx) <= 18)
            dragMode = DragMode.Start;
        else if (Math.Abs(x - ex) <= 18)
            dragMode = DragMode.End;
        else
        {
            dragMode = DragMode.Position;
            position = TimeAt(x);
            SeekRequested?.Invoke(this, position);
            InvalidateVisual();
        }
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (dragMode == DragMode.None || e.LeftButton != MouseButtonState.Pressed) return;
        var t = TimeAt(e.GetPosition(this).X);

        if (dragMode == DragMode.Start)
        {
            var max = end - TimeSpan.FromMilliseconds(100);
            start = t > max ? max : t;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (position < start) position = start;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (dragMode == DragMode.End)
        {
            var min = start + TimeSpan.FromMilliseconds(100);
            end = t < min ? min : t;
            if (end > duration) end = duration;
            if (position > end) position = end;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            position = t;
            SeekRequested?.Invoke(this, position);
        }

        InvalidateVisual();
    }

    void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragMode == DragMode.Start || dragMode == DragMode.End)
        {
            if (position < start || position > end)
                position = start;
            SeekRequested?.Invoke(this, position);
        }

        dragMode = DragMode.None;
        ReleaseMouseCapture();
    }

    void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var delta = e.Delta > 0 ? 0.5 : -0.5;
        position = Clamp(position + TimeSpan.FromSeconds(delta));
        SeekRequested?.Invoke(this, position);
        InvalidateVisual();
    }

    double X(TimeSpan t)
    {
        return ActualWidth <= 0 ? 0 : Math.Clamp(t.TotalSeconds / Math.Max(0.001, duration.TotalSeconds), 0, 1) * ActualWidth;
    }

    TimeSpan TimeAt(double x)
    {
        if (ActualWidth <= 0) return TimeSpan.Zero;
        var ratio = Math.Clamp(x / ActualWidth, 0, 1);
        return TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
    }

    TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (value > duration) return duration;
        return value;
    }
}
