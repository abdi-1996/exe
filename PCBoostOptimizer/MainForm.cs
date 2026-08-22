using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PCBoostOptimizer;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(15, 23, 42);
    private static readonly Color Sidebar = Color.FromArgb(17, 27, 46);
    private static readonly Color Surface = Color.FromArgb(27, 39, 61);
    private static readonly Color SurfaceAlt = Color.FromArgb(34, 49, 74);
    private static readonly Color Border = Color.FromArgb(52, 70, 98);
    private static readonly Color Accent = Color.FromArgb(72, 141, 232);
    private static readonly Color AccentHover = Color.FromArgb(91, 158, 246);
    private static readonly Color Success = Color.FromArgb(80, 199, 143);
    private static readonly Color Warning = Color.FromArgb(244, 181, 72);
    private static readonly Color Danger = Color.FromArgb(241, 106, 112);
    private static readonly Color PrimaryText = Color.FromArgb(239, 245, 255);
    private static readonly Color MutedText = Color.FromArgb(163, 180, 205);

    private readonly OptimizerEngine _engine = new();
    private readonly PerformanceMonitor _performanceMonitor = new();
    private readonly AppSettings _settings;
    private readonly MiniOverlayForm _overlay;
    private readonly System.Windows.Forms.Timer _monitorTimer = new() { Interval = 2_000 };
    private readonly NotifyIcon _trayIcon = new();
    private readonly Panel _contentHost = new();
    private readonly Dictionary<string, Button> _navigationButtons = new();
    private readonly Dictionary<string, Label> _metricLabels = new();
    private readonly Label _pageTitle = new();
    private readonly Label _pageSubtitle = new();
    private readonly Label _adminStatus = new();
    private readonly Button _adminButton = new();

    private string _currentPage = string.Empty;
    private Label? _dashboardRecommendation;
    private Label? _dashboardStatus;
    private Button? _dashboardRefreshButton;
    private ListView? _cleanupList;
    private List<CleanupTarget> _cleanupTargets = [];
    private Label? _cleanupStatus;
    private Button? _cleanupScanButton;
    private Button? _cleanupRunButton;
    private ListView? _startupList;
    private Label? _startupStatus;
    private ListView? _processList;
    private Label? _processStatus;
    private Button? _processRefreshButton;
    private ToolStripMenuItem? _trayOverlayItem;
    private ToolStripMenuItem? _trayMonitoringItem;
    private Label? _realtimeStatus;
    private Label? _realtimeCpu;
    private Label? _realtimeMemory;
    private Label? _realtimeDisk;
    private Label? _realtimeMaintenance;
    private PerformanceSnapshot? _latestPerformance;
    private bool _monitorTickRunning;
    private bool _maintenanceRunning;
    private bool _isExiting;
    private readonly bool _launchMinimized;
    private string _maintenanceMessage = "Фоновое обслуживание ожидает следующую проверку.";

    public MainForm(bool launchMinimized)
    {
        _launchMinimized = launchMinimized;
        _settings = AppSettingsStore.Load();
        _overlay = new MiniOverlayForm();

        Text = "PC Boost — оптимизация Windows";
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = PrimaryText;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildShell();
        InitializeBackgroundServices();
        ShowDashboard();
    }

    private void BuildShell()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 236,
            BackColor = Sidebar
        };

        var brandMark = new Label
        {
            Text = "⚡",
            Font = new Font("Segoe UI Emoji", 24F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Accent,
            AutoSize = true,
            Location = new Point(27, 27)
        };
        sidebar.Controls.Add(brandMark);

        var brandName = CreateLabel("PC Boost", 18F, FontStyle.Bold, PrimaryText);
        brandName.Location = new Point(73, 27);
        brandName.AutoSize = true;
        sidebar.Controls.Add(brandName);

        var brandDescription = CreateLabel("Безопасная оптимизация Windows", 9F, FontStyle.Regular, MutedText);
        brandDescription.Location = new Point(28, 70);
        brandDescription.AutoSize = true;
        sidebar.Controls.Add(brandDescription);

        var divider = new Panel
        {
            Height = 1,
            Width = 184,
            BackColor = Border,
            Location = new Point(26, 103)
        };
        sidebar.Controls.Add(divider);

        AddNavigationButton(sidebar, "dashboard", "▦   Обзор", 128, ShowDashboard);
        AddNavigationButton(sidebar, "cleanup", "⌫   Очистка", 177, ShowCleanup);
        AddNavigationButton(sidebar, "startup", "↗   Автозагрузка", 226, ShowStartup);
        AddNavigationButton(sidebar, "processes", "▤   Процессы", 275, ShowProcesses);
        AddNavigationButton(sidebar, "tools", "⚒   Инструменты", 324, ShowTools);
        AddNavigationButton(sidebar, "realtime", "◉   Фоновый режим", 373, ShowRealtime);
        AddNavigationButton(sidebar, "about", "ⓘ   О программе", 422, ShowAbout);

        var safeCard = new CardPanel
        {
            Width = 184,
            Height = 122,
            Location = new Point(26, sidebar.Height - 150),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            BackColor = SurfaceAlt,
            BorderColor = Border
        };
        var safeTitle = CreateLabel("Защита данных", 10F, FontStyle.Bold, Success);
        safeTitle.Location = new Point(15, 15);
        safeTitle.AutoSize = true;
        safeCard.Controls.Add(safeTitle);
        var safeText = CreateLabel("В фоне — только старые\nфайлы из %TEMP%.", 9F, FontStyle.Regular, MutedText);
        safeText.Location = new Point(15, 42);
        safeText.AutoSize = true;
        safeCard.Controls.Add(safeText);
        sidebar.Controls.Add(safeCard);

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.BackColor = Background;
        Controls.Add(_contentHost);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Background,
            Padding = new Padding(32, 18, 32, 16)
        };
        header.Paint += (_, eventArgs) =>
        {
            using var pen = new Pen(Border);
            eventArgs.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        _pageTitle.Font = new Font("Segoe UI", 20F, FontStyle.Bold, GraphicsUnit.Point);
        _pageTitle.ForeColor = PrimaryText;
        _pageTitle.AutoSize = true;
        _pageTitle.Location = new Point(32, 17);
        header.Controls.Add(_pageTitle);

        _pageSubtitle.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _pageSubtitle.ForeColor = MutedText;
        _pageSubtitle.AutoSize = true;
        _pageSubtitle.Location = new Point(34, 52);
        header.Controls.Add(_pageSubtitle);

        _adminButton.Text = "Запустить как администратор";
        StyleButton(_adminButton, Accent, AccentHover, 10F, 8);
        _adminButton.Size = new Size(210, 38);
        _adminButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _adminButton.Location = new Point(header.Width - 242, 27);
        _adminButton.Click += (_, _) => RestartAsAdministrator();
        header.Controls.Add(_adminButton);

        _adminStatus.AutoSize = true;
        _adminStatus.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _adminStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _adminStatus.Location = new Point(header.Width - 420, 38);
        header.Controls.Add(_adminStatus);
        header.Resize += (_, _) => LayoutHeader(header);

        Controls.Add(header);
        Controls.Add(sidebar);
        LayoutHeader(header);
    }

    private void InitializeBackgroundServices()
    {
        _overlay.HideOverlayRequested += (_, _) => SetOverlayVisible(false);
        _overlay.OverlayLocationChanged += (_, _) =>
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
            AppSettingsStore.Save(_settings);
        };

        var trayMenu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Открыть PC Boost");
        showItem.Click += (_, _) => ShowMainWindow();
        _trayOverlayItem = new ToolStripMenuItem("Панель поверх программ")
        {
            CheckOnClick = true,
            Checked = _settings.OverlayVisible
        };
        _trayOverlayItem.Click += (_, _) => SetOverlayVisible(_trayOverlayItem.Checked);
        _trayMonitoringItem = new ToolStripMenuItem("Фоновый мониторинг")
        {
            CheckOnClick = true,
            Checked = _settings.BackgroundMonitoringEnabled
        };
        _trayMonitoringItem.Click += (_, _) => SetBackgroundMonitoringEnabled(_trayMonitoringItem.Checked);
        var exitItem = new ToolStripMenuItem("Выйти из PC Boost");
        exitItem.Click += (_, _) => ExitApplication();
        trayMenu.Items.Add(showItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_trayOverlayItem);
        trayMenu.Items.Add(_trayMonitoringItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.Text = "PC Boost — фоновая оптимизация";
        _trayIcon.ContextMenuStrip = trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        _monitorTimer.Tick += async (_, _) => await RunMonitorTickAsync();
        Shown += async (_, _) =>
        {
            if (_settings.OverlayVisible)
            {
                SetOverlayVisible(true, persist: false);
            }
            _monitorTimer.Start();
            await RunMonitorTickAsync();
            if (_launchMinimized)
            {
                BeginInvoke(new Action(HideMainWindow));
            }
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && !_isExiting)
            {
                HideMainWindow();
            }
        };
        FormClosing += OnMainFormClosing;
    }

    private async Task RunMonitorTickAsync()
    {
        if (_monitorTickRunning || !_settings.BackgroundMonitoringEnabled || _isExiting)
        {
            return;
        }

        _monitorTickRunning = true;
        try
        {
            var snapshot = await Task.Run(_performanceMonitor.Capture);
            if (IsDisposed || _isExiting)
            {
                return;
            }

            _latestPerformance = snapshot;
            if (_settings.AutoMaintenanceEnabled &&
                !_maintenanceRunning &&
                DateTime.UtcNow - _settings.LastAutomaticMaintenanceUtc >= TimeSpan.FromMinutes(30))
            {
                _settings.LastAutomaticMaintenanceUtc = DateTime.UtcNow;
                _maintenanceMessage = "Фоновое обслуживание: проверяем старые временные файлы…";
                AppSettingsStore.Save(_settings);
                _ = RunAutomaticMaintenanceAsync();
            }
            else if (!_settings.AutoMaintenanceEnabled)
            {
                _maintenanceMessage = "Автоочистка временных файлов отключена.";
            }

            UpdateRealtimeVisuals(snapshot);
        }
        catch (Exception exception)
        {
            _maintenanceMessage = $"Фоновый мониторинг временно недоступен: {exception.Message}";
            UpdateRealtimeStatusOnly();
        }
        finally
        {
            _monitorTickRunning = false;
        }
    }

    private async Task RunAutomaticMaintenanceAsync()
    {
        _maintenanceRunning = true;
        try
        {
            var cleanup = await _engine.CleanStaleUserTempFilesAsync(7);
            _maintenanceMessage = cleanup.FreedBytes > 0
                ? $"Фоновая очистка: освобождено {OptimizerEngine.FormatBytes(cleanup.FreedBytes)} старых временных файлов."
                : "Фоновая очистка: старых временных файлов не найдено.";
        }
        catch (Exception exception)
        {
            _maintenanceMessage = $"Фоновая очистка не завершена: {exception.Message}";
        }
        finally
        {
            _maintenanceRunning = false;
        }

        if (IsDisposed || _isExiting)
        {
            return;
        }
        if (_latestPerformance is not null)
        {
            UpdateRealtimeVisuals(_latestPerformance);
        }
        else
        {
            UpdateRealtimeStatusOnly();
        }
    }

    private void UpdateRealtimeVisuals(PerformanceSnapshot snapshot)
    {
        var (status, color) = GetPerformanceStatus(snapshot);
        if (_settings.OverlayVisible)
        {
            if (!_overlay.Visible)
            {
                _overlay.SetInitialLocation(_settings.OverlayLeft, _settings.OverlayTop);
                _overlay.Show();
            }
            _overlay.UpdateSnapshot(snapshot, status, color);
        }

        if (_currentPage == "realtime")
        {
            SetLabelText(_realtimeCpu, $"{snapshot.CpuUsagePercent:N0}%", snapshot.CpuUsagePercent >= 90 ? Danger : PrimaryText);
            SetLabelText(_realtimeMemory, $"{snapshot.MemoryUsagePercent:N0}%", snapshot.MemoryUsagePercent >= 85 ? Warning : PrimaryText);
            SetLabelText(_realtimeDisk, $"{snapshot.DiskUsagePercent:N0}%", snapshot.DiskUsagePercent >= 85 ? Warning : PrimaryText);
            SetLabelText(_realtimeStatus, status, color);
            SetLabelText(_realtimeMaintenance, _maintenanceMessage, MutedText);
        }
    }

    private void UpdateRealtimeStatusOnly()
    {
        if (_currentPage == "realtime")
        {
            SetLabelText(_realtimeStatus, _maintenanceMessage, Warning);
            SetLabelText(_realtimeMaintenance, "", MutedText);
        }
    }

    private static void SetLabelText(Label? label, string text, Color color)
    {
        if (label is not null && !label.IsDisposed)
        {
            label.Text = text;
            label.ForeColor = color;
        }
    }

    private static (string Message, Color Color) GetPerformanceStatus(PerformanceSnapshot snapshot)
    {
        if (snapshot.CpuUsagePercent >= 90)
        {
            return ("Высокая нагрузка на процессор", Danger);
        }
        if (snapshot.MemoryUsagePercent >= 90)
        {
            return ("Мало свободной оперативной памяти", Warning);
        }
        if (snapshot.DiskUsagePercent >= 90)
        {
            return ("На системном диске почти нет места", Warning);
        }
        if (snapshot.CpuUsagePercent >= 70 || snapshot.MemoryUsagePercent >= 80)
        {
            return ("Повышенная нагрузка — мониторинг активен", Warning);
        }
        return ("Система работает нормально", Success);
    }

    private void SetOverlayVisible(bool visible, bool persist = true)
    {
        _settings.OverlayVisible = visible;
        if (_trayOverlayItem is not null)
        {
            _trayOverlayItem.Checked = visible;
        }

        if (visible)
        {
            _overlay.SetInitialLocation(_settings.OverlayLeft, _settings.OverlayTop);
            if (!_overlay.Visible)
            {
                _overlay.Show();
            }
            _overlay.TopMost = true;
            if (_latestPerformance is not null)
            {
                var (status, color) = GetPerformanceStatus(_latestPerformance);
                _overlay.UpdateSnapshot(_latestPerformance, status, color);
            }
        }
        else if (_overlay.Visible)
        {
            _overlay.Hide();
        }

        if (persist)
        {
            AppSettingsStore.Save(_settings);
        }
    }

    private void SetBackgroundMonitoringEnabled(bool enabled)
    {
        _settings.BackgroundMonitoringEnabled = enabled;
        if (_trayMonitoringItem is not null)
        {
            _trayMonitoringItem.Checked = enabled;
        }
        AppSettingsStore.Save(_settings);
        if (enabled)
        {
            _maintenanceMessage = "Фоновый мониторинг снова активен.";
            _ = RunMonitorTickAsync();
        }
        else
        {
            _maintenanceMessage = "Фоновый мониторинг приостановлен.";
            if (_settings.OverlayVisible && _latestPerformance is not null)
            {
                _overlay.UpdateSnapshot(_latestPerformance, _maintenanceMessage, Warning);
            }
            UpdateRealtimeStatusOnly();
        }
    }

    private void HideMainWindow()
    {
        if (Visible)
        {
            Hide();
        }
        if (_settings.OverlayVisible && !_overlay.Visible)
        {
            SetOverlayVisible(true, persist: false);
        }
    }

    private void ShowMainWindow()
    {
        if (!Visible)
        {
            Show();
        }
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_isExiting)
        {
            _monitorTimer.Stop();
            _trayIcon.Visible = false;
            _overlay.Hide();
            _overlay.Dispose();
            return;
        }

        eventArgs.Cancel = true;
        HideMainWindow();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void LayoutHeader(Panel header)
    {
        _adminButton.Left = header.ClientSize.Width - _adminButton.Width - 32;
        _adminStatus.Left = _adminButton.Left - _adminStatus.Width - 14;
    }

    private void AddNavigationButton(Control parent, string key, string text, int top, Action action)
    {
        var button = new Button
        {
            Text = text,
            Tag = key,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = Sidebar,
            ForeColor = MutedText,
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18, 0, 0, 0),
            Size = new Size(212, 43),
            Location = new Point(12, top),
            Cursor = Cursors.Hand
        };
        button.MouseEnter += (_, _) =>
        {
            if (!string.Equals(_currentPage, key, StringComparison.Ordinal))
            {
                button.BackColor = Surface;
                button.ForeColor = PrimaryText;
            }
        };
        button.MouseLeave += (_, _) =>
        {
            if (!string.Equals(_currentPage, key, StringComparison.Ordinal))
            {
                button.BackColor = Sidebar;
                button.ForeColor = MutedText;
            }
        };
        button.Click += (_, _) => action();
        _navigationButtons[key] = button;
        parent.Controls.Add(button);
    }

    private void BeginPage(string key, string title, string subtitle)
    {
        _currentPage = key;
        _pageTitle.Text = title;
        _pageSubtitle.Text = subtitle;
        UpdateAdminStatus();

        foreach (var (buttonKey, button) in _navigationButtons)
        {
            var selected = string.Equals(buttonKey, key, StringComparison.Ordinal);
            button.BackColor = selected ? SurfaceAlt : Sidebar;
            button.ForeColor = selected ? PrimaryText : MutedText;
        }

        foreach (Control control in _contentHost.Controls)
        {
            control.Dispose();
        }
        _contentHost.Controls.Clear();
        _contentHost.AutoScrollPosition = Point.Empty;
    }

    private Panel CreatePageCanvas()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            AutoScroll = true,
            Padding = new Padding(0, 0, 0, 30)
        };
        _contentHost.Controls.Add(page);
        return page;
    }

    private void ShowDashboard()
    {
        BeginPage("dashboard", "Обзор системы", "Быстрая диагностика и безопасные действия для ускорения Windows");
        _metricLabels.Clear();
        var page = CreatePageCanvas();

        var greeting = new CardPanel
        {
            Location = new Point(30, 26),
            Size = new Size(850, 111),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var greetingTitle = CreateLabel("Готово к проверке", 17F, FontStyle.Bold, PrimaryText);
        greetingTitle.Location = new Point(23, 19);
        greetingTitle.AutoSize = true;
        greeting.Controls.Add(greetingTitle);
        var greetingText = CreateLabel("Мониторинг обновляется в реальном времени; автообслуживание касается только старых файлов из %TEMP%.", 10F, FontStyle.Regular, MutedText);
        greetingText.Location = new Point(24, 55);
        greetingText.AutoSize = true;
        greeting.Controls.Add(greetingText);

        _dashboardRefreshButton = new Button
        {
            Text = "Обновить данные",
            Size = new Size(160, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(greeting.Width - 183, 36)
        };
        StyleButton(_dashboardRefreshButton, Accent, AccentHover, 9.5F, 7);
        _dashboardRefreshButton.Click += async (_, _) => await RefreshDashboardAsync();
        greeting.Controls.Add(_dashboardRefreshButton);
        greeting.Resize += (_, _) => _dashboardRefreshButton.Left = greeting.ClientSize.Width - _dashboardRefreshButton.Width - 23;
        page.Controls.Add(greeting);

        _dashboardStatus = CreateLabel("Сканирование системы…", 9F, FontStyle.Regular, MutedText);
        _dashboardStatus.Location = new Point(31, 148);
        _dashboardStatus.AutoSize = true;
        page.Controls.Add(_dashboardStatus);

        var cards = new TableLayoutPanel
        {
            Location = new Point(24, 176),
            Width = 856,
            Height = 148,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0)
        };
        for (var index = 0; index < 4; index++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        }
        cards.Controls.Add(CreateMetricCard("ОЗУ", "memory", "Свободная память"), 0, 0);
        cards.Controls.Add(CreateMetricCard("Диск C:", "disk", "Свободное место"), 1, 0);
        cards.Controls.Add(CreateMetricCard("Временные файлы", "temporary", "Можно безопасно очистить"), 2, 0);
        cards.Controls.Add(CreateMetricCard("Автозагрузка", "startup", "Включённых программ"), 3, 0);
        page.Controls.Add(cards);

        var recommendationsCard = new CardPanel
        {
            Location = new Point(30, 350),
            Size = new Size(850, 184),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var recommendationsTitle = CreateLabel("Рекомендации", 13F, FontStyle.Bold, PrimaryText);
        recommendationsTitle.Location = new Point(23, 20);
        recommendationsTitle.AutoSize = true;
        recommendationsCard.Controls.Add(recommendationsTitle);
        _dashboardRecommendation = CreateLabel("Проверяем состояние системы…", 10F, FontStyle.Regular, MutedText);
        _dashboardRecommendation.Location = new Point(24, 55);
        _dashboardRecommendation.Size = new Size(720, 85);
        _dashboardRecommendation.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _dashboardRecommendation.AutoSize = false;
        _dashboardRecommendation.MaximumSize = new Size(760, 0);
        recommendationsCard.Controls.Add(_dashboardRecommendation);
        var cleanupButton = new Button
        {
            Text = "Открыть очистку",
            Size = new Size(175, 39),
            Location = new Point(24, 130)
        };
        StyleButton(cleanupButton, Accent, AccentHover, 9.5F, 7);
        cleanupButton.Click += (_, _) => ShowCleanup();
        recommendationsCard.Controls.Add(cleanupButton);
        var startupButton = new Button
        {
            Text = "Проверить автозагрузку",
            Size = new Size(210, 39),
            Location = new Point(211, 130)
        };
        StyleButton(startupButton, SurfaceAlt, Border, 9.5F, 7);
        startupButton.Click += (_, _) => ShowStartup();
        recommendationsCard.Controls.Add(startupButton);
        page.Controls.Add(recommendationsCard);

        var safetyCard = new CardPanel
        {
            Location = new Point(30, 558),
            Size = new Size(850, 105),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(25, 65, 62),
            BorderColor = Color.FromArgb(50, 126, 113)
        };
        var safetyIcon = CreateLabel("✓", 20F, FontStyle.Bold, Success);
        safetyIcon.Location = new Point(22, 28);
        safetyIcon.AutoSize = true;
        safetyCard.Controls.Add(safetyIcon);
        var safetyLabel = CreateLabel("Безопасный подход", 11F, FontStyle.Bold, PrimaryText);
        safetyLabel.Location = new Point(58, 20);
        safetyLabel.AutoSize = true;
        safetyCard.Controls.Add(safetyLabel);
        var safetyDescription = CreateLabel("PC Boost не удаляет драйверы, обновления, личные файлы и не использует рискованную «чистку реестра».", 9.5F, FontStyle.Regular, MutedText);
        safetyDescription.Location = new Point(58, 48);
        safetyDescription.Size = new Size(730, 36);
        safetyDescription.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        safetyCard.Controls.Add(safetyDescription);
        page.Controls.Add(safetyCard);

        _ = RefreshDashboardAsync();
    }

    private async Task RefreshDashboardAsync()
    {
        if (_currentPage != "dashboard" || _dashboardRefreshButton is null)
        {
            return;
        }

        try
        {
            _dashboardRefreshButton.Enabled = false;
            if (_dashboardStatus is not null)
            {
                _dashboardStatus.Text = "Сканирование системы…";
            }

            var snapshot = await _engine.GetSystemSnapshotAsync();
            if (IsDisposed || _currentPage != "dashboard")
            {
                return;
            }

            SetMetric("memory", $"{OptimizerEngine.FormatBytes((long)snapshot.AvailableMemoryBytes)} / {OptimizerEngine.FormatBytes((long)snapshot.TotalMemoryBytes)}");
            SetMetric("disk", $"{OptimizerEngine.FormatBytes(snapshot.SystemDriveFreeBytes)} свободно");
            SetMetric("temporary", OptimizerEngine.FormatBytes(snapshot.TempFilesBytes));
            SetMetric("startup", $"{snapshot.StartupItemsCount} программ");
            if (_dashboardStatus is not null)
            {
                _dashboardStatus.Text = $"{snapshot.ComputerName} • {snapshot.WindowsVersion} • непрерывная работа: {FormatUptime(snapshot.Uptime)}";
            }
            UpdateRecommendations(snapshot);
        }
        catch (Exception exception)
        {
            if (_dashboardStatus is not null && _currentPage == "dashboard")
            {
                _dashboardStatus.Text = $"Не удалось завершить сканирование: {exception.Message}";
            }
        }
        finally
        {
            if (!IsDisposed && _currentPage == "dashboard" && _dashboardRefreshButton is not null)
            {
                _dashboardRefreshButton.Enabled = true;
            }
        }
    }

    private void UpdateRecommendations(SystemSnapshot snapshot)
    {
        if (_dashboardRecommendation is null)
        {
            return;
        }

        var advice = new List<string>();
        if (snapshot.TempFilesBytes >= 1_024L * 1_024 * 1_024)
        {
            advice.Add($"• Освободите {OptimizerEngine.FormatBytes(snapshot.TempFilesBytes)} временных файлов на вкладке «Очистка».");
        }
        if (snapshot.SystemDriveTotalBytes > 0 && snapshot.SystemDriveFreeBytes * 100 / snapshot.SystemDriveTotalBytes < 15)
        {
            advice.Add("• На системном диске мало свободного места. Желательно оставить не менее 15–20%.");
        }
        if (snapshot.StartupItemsCount > 8)
        {
            advice.Add("• Проверьте автозагрузку: ненужные приложения могут замедлять запуск Windows.");
        }
        if (snapshot.AvailableMemoryBytes > 0 && snapshot.TotalMemoryBytes > 0 && snapshot.AvailableMemoryBytes * 100 / snapshot.TotalMemoryBytes < 20)
        {
            advice.Add("• Свободной оперативной памяти мало — откройте вкладку «Процессы» и закройте ненужные программы.");
        }
        if (advice.Count == 0)
        {
            advice.Add("• Критичных проблем не найдено. Для профилактики проверяйте временные файлы и автозагрузку раз в несколько недель.");
        }
        if (!snapshot.IsAdministrator)
        {
            advice.Add("• Для очистки системной папки Temp и проверки SFC при необходимости запустите приложение от имени администратора.");
        }

        _dashboardRecommendation.Text = string.Join(Environment.NewLine, advice);
    }

    private CardPanel CreateMetricCard(string title, string metricKey, string caption)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(6),
            BackColor = Surface,
            BorderColor = Border
        };
        var titleLabel = CreateLabel(title, 10F, FontStyle.Bold, MutedText);
        titleLabel.Location = new Point(17, 17);
        titleLabel.AutoSize = true;
        card.Controls.Add(titleLabel);
        var valueLabel = CreateLabel("—", 15F, FontStyle.Bold, PrimaryText);
        valueLabel.Location = new Point(17, 52);
        valueLabel.AutoSize = true;
        valueLabel.MaximumSize = new Size(175, 0);
        card.Controls.Add(valueLabel);
        var captionLabel = CreateLabel(caption, 8.5F, FontStyle.Regular, MutedText);
        captionLabel.Location = new Point(17, 107);
        captionLabel.AutoSize = true;
        captionLabel.MaximumSize = new Size(180, 0);
        card.Controls.Add(captionLabel);
        _metricLabels[metricKey] = valueLabel;
        return card;
    }

    private void SetMetric(string metricKey, string value)
    {
        if (_metricLabels.TryGetValue(metricKey, out var label) && !label.IsDisposed)
        {
            label.Text = value;
        }
    }

    private void ShowCleanup()
    {
        BeginPage("cleanup", "Очистка", "Выберите только то, что хотите удалить. Личные документы и программы не затрагиваются.");
        var page = CreatePageCanvas();

        var intro = new CardPanel
        {
            Location = new Point(30, 26),
            Size = new Size(850, 88),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var introTitle = CreateLabel("Сначала — сканирование", 13F, FontStyle.Bold, PrimaryText);
        introTitle.Location = new Point(22, 16);
        introTitle.AutoSize = true;
        intro.Controls.Add(introTitle);
        var introDescription = CreateLabel("Показанный размер — оценка. Занятые файлами Windows данные будут безопасно пропущены.", 9.5F, FontStyle.Regular, MutedText);
        introDescription.Location = new Point(22, 45);
        introDescription.AutoSize = true;
        intro.Controls.Add(introDescription);
        page.Controls.Add(intro);

        var listCard = new CardPanel
        {
            Location = new Point(30, 136),
            Size = new Size(850, 360),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border,
            Padding = new Padding(15)
        };
        _cleanupList = CreateDarkListView(checkBoxes: true);
        _cleanupList.Bounds = new Rectangle(15, 16, listCard.Width - 30, 328);
        _cleanupList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _cleanupList.Columns.Add("Категория", 200);
        _cleanupList.Columns.Add("Что будет очищено", 430);
        _cleanupList.Columns.Add("Размер", 130, HorizontalAlignment.Right);
        listCard.Controls.Add(_cleanupList);
        page.Controls.Add(listCard);

        _cleanupTargets = _engine.GetCleanupTargets().ToList();
        foreach (var target in _cleanupTargets)
        {
            var item = new ListViewItem(target.Title)
            {
                Tag = target,
                Checked = target.Recommended
            };
            item.SubItems.Add(target.Description);
            item.SubItems.Add("—");
            if (target.RequiresAdministrator && !NativeMethods.IsAdministrator())
            {
                item.ForeColor = Warning;
            }
            _cleanupList.Items.Add(item);
        }

        _cleanupStatus = CreateLabel("Нажмите «Сканировать», чтобы увидеть объём для очистки.", 9.5F, FontStyle.Regular, MutedText);
        _cleanupStatus.Location = new Point(31, 516);
        _cleanupStatus.AutoSize = true;
        page.Controls.Add(_cleanupStatus);

        _cleanupScanButton = new Button
        {
            Text = "Сканировать",
            Size = new Size(155, 42),
            Location = new Point(30, 550)
        };
        StyleButton(_cleanupScanButton, SurfaceAlt, Border, 10F, 8);
        _cleanupScanButton.Click += async (_, _) => await ScanCleanupAsync();
        page.Controls.Add(_cleanupScanButton);

        _cleanupRunButton = new Button
        {
            Text = "Очистить выбранное",
            Size = new Size(205, 42),
            Location = new Point(196, 550)
        };
        StyleButton(_cleanupRunButton, Accent, AccentHover, 10F, 8);
        _cleanupRunButton.Click += async (_, _) => await RunCleanupAsync();
        page.Controls.Add(_cleanupRunButton);

        var adminHint = CreateLabel("Жёлтые пункты требуют права администратора.", 9F, FontStyle.Regular, Warning);
        adminHint.Location = new Point(30, 609);
        adminHint.AutoSize = true;
        adminHint.Visible = !NativeMethods.IsAdministrator();
        page.Controls.Add(adminHint);
    }

    private async Task ScanCleanupAsync()
    {
        if (_cleanupList is null || _cleanupScanButton is null || _cleanupRunButton is null)
        {
            return;
        }

        try
        {
            _cleanupScanButton.Enabled = false;
            _cleanupRunButton.Enabled = false;
            SetCleanupStatus("Сканируем выбранные папки…");
            var total = await _engine.ScanCleanupTargetsAsync(_cleanupTargets);
            if (IsDisposed || _currentPage != "cleanup" || _cleanupList is null)
            {
                return;
            }

            foreach (ListViewItem item in _cleanupList.Items)
            {
                if (item.Tag is CleanupTarget target)
                {
                    item.SubItems[2].Text = target.Kind == CleanupKind.RecycleBin ? "—" : OptimizerEngine.FormatBytes(target.SizeBytes);
                }
            }
            SetCleanupStatus($"Найдено до {OptimizerEngine.FormatBytes(total)} временных файлов. Выберите нужные категории и подтвердите очистку.");
        }
        catch (Exception exception)
        {
            SetCleanupStatus($"Сканирование не удалось: {exception.Message}");
        }
        finally
        {
            if (!IsDisposed && _currentPage == "cleanup")
            {
                if (_cleanupScanButton is not null) _cleanupScanButton.Enabled = true;
                if (_cleanupRunButton is not null) _cleanupRunButton.Enabled = true;
            }
        }
    }

    private async Task RunCleanupAsync()
    {
        if (_cleanupList is null || _cleanupRunButton is null || _cleanupScanButton is null)
        {
            return;
        }

        var selected = _cleanupList.CheckedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag as CleanupTarget)
            .Where(target => target is not null)
            .Cast<CleanupTarget>()
            .ToArray();

        if (selected.Length == 0)
        {
            ShowInfo("Выберите хотя бы одну категорию для очистки.", "Ничего не выбрано");
            return;
        }

        var names = string.Join(Environment.NewLine, selected.Select(x => $"• {x.Title}"));
        var answer = MessageBox.Show(
            $"Будут очищены следующие категории:\n\n{names}\n\nЗанятые файлы Windows будут пропущены. Продолжить?",
            "Подтвердите очистку",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _cleanupRunButton.Enabled = false;
            _cleanupScanButton.Enabled = false;
            SetCleanupStatus("Идёт очистка… Не закрывайте приложение.");
            var results = await _engine.CleanAsync(selected);
            if (IsDisposed || _currentPage != "cleanup")
            {
                return;
            }

            var freed = results.Sum(x => x.FreedBytes);
            var skipped = results.Sum(x => x.SkippedFiles);
            SetCleanupStatus($"Готово: освобождено {OptimizerEngine.FormatBytes(freed)}. Пропущено занятых или защищённых файлов: {skipped}.");
            var resultText = string.Join(Environment.NewLine, results.Select(x => $"• {x.Target}: {OptimizerEngine.FormatBytes(x.FreedBytes)} — {x.Note}"));
            ShowInfo(resultText, "Очистка завершена");
            await ScanCleanupAsync();
        }
        catch (Exception exception)
        {
            SetCleanupStatus($"Очистка не завершена: {exception.Message}");
        }
        finally
        {
            if (!IsDisposed && _currentPage == "cleanup")
            {
                if (_cleanupRunButton is not null) _cleanupRunButton.Enabled = true;
                if (_cleanupScanButton is not null) _cleanupScanButton.Enabled = true;
            }
        }
    }

    private void SetCleanupStatus(string message)
    {
        if (_cleanupStatus is not null && !_cleanupStatus.IsDisposed)
        {
            _cleanupStatus.Text = message;
        }
    }

    private void ShowStartup()
    {
        BeginPage("startup", "Автозагрузка", "Контроль программ, которые запускаются вместе с вашей учётной записью Windows");
        var page = CreatePageCanvas();

        var note = new CardPanel
        {
            Location = new Point(30, 26),
            Size = new Size(850, 96),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var noteTitle = CreateLabel("Изменения обратимы", 13F, FontStyle.Bold, Success);
        noteTitle.Location = new Point(22, 16);
        noteTitle.AutoSize = true;
        note.Controls.Add(noteTitle);
        var noteText = CreateLabel("Отключённые здесь записи сохраняются в резервной копии PC Boost и могут быть включены в один клик.", 9.5F, FontStyle.Regular, MutedText);
        noteText.Location = new Point(22, 46);
        noteText.AutoSize = true;
        note.Controls.Add(noteText);
        page.Controls.Add(note);

        var listCard = new CardPanel
        {
            Location = new Point(30, 146),
            Size = new Size(850, 366),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border,
            Padding = new Padding(15)
        };
        _startupList = CreateDarkListView();
        _startupList.Bounds = new Rectangle(15, 16, listCard.Width - 30, 334);
        _startupList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _startupList.Columns.Add("Программа", 190);
        _startupList.Columns.Add("Команда / путь", 370);
        _startupList.Columns.Add("Статус", 100);
        _startupList.Columns.Add("Источник", 180);
        listCard.Controls.Add(_startupList);
        page.Controls.Add(listCard);

        _startupStatus = CreateLabel("", 9.5F, FontStyle.Regular, MutedText);
        _startupStatus.Location = new Point(31, 533);
        _startupStatus.AutoSize = true;
        page.Controls.Add(_startupStatus);

        var refresh = new Button { Text = "Обновить", Size = new Size(132, 42), Location = new Point(30, 567) };
        StyleButton(refresh, SurfaceAlt, Border, 10F, 8);
        refresh.Click += (_, _) => RefreshStartupList();
        page.Controls.Add(refresh);

        var disable = new Button { Text = "Отключить выбранное", Size = new Size(205, 42), Location = new Point(176, 567) };
        StyleButton(disable, Accent, AccentHover, 10F, 8);
        disable.Click += (_, _) => DisableSelectedStartup();
        page.Controls.Add(disable);

        var enable = new Button { Text = "Включить обратно", Size = new Size(190, 42), Location = new Point(393, 567) };
        StyleButton(enable, SurfaceAlt, Border, 10F, 8);
        enable.Click += (_, _) => EnableSelectedStartup();
        page.Controls.Add(enable);

        var settings = new Button { Text = "Настройки Windows", Size = new Size(180, 42), Location = new Point(595, 567) };
        StyleButton(settings, SurfaceAlt, Border, 10F, 8);
        settings.Click += (_, _) =>
        {
            if (!_engine.LaunchExternalTool("ms-settings:startupapps"))
            {
                ShowInfo("Не удалось открыть настройки автозагрузки Windows.", "Не удалось открыть");
            }
        };
        page.Controls.Add(settings);

        RefreshStartupList();
    }

    private void RefreshStartupList()
    {
        if (_startupList is null || _currentPage != "startup")
        {
            return;
        }

        _startupList.BeginUpdate();
        _startupList.Items.Clear();
        var items = _engine.GetStartupItems();
        foreach (var startupItem in items)
        {
            var item = new ListViewItem(startupItem.Name) { Tag = startupItem };
            item.SubItems.Add(startupItem.Command);
            item.SubItems.Add(startupItem.IsEnabled ? "Включено" : "Отключено");
            item.SubItems.Add(startupItem.SourceLabel);
            item.ForeColor = startupItem.IsEnabled ? PrimaryText : MutedText;
            _startupList.Items.Add(item);
        }
        _startupList.EndUpdate();
        if (_startupStatus is not null)
        {
            var enabled = items.Count(x => x.IsEnabled);
            _startupStatus.Text = $"Показано: {items.Count}. Включено при входе в Windows: {enabled}.";
        }
    }

    private void DisableSelectedStartup()
    {
        var selected = GetSelectedStartupItem();
        if (selected is null)
        {
            ShowInfo("Сначала выберите запись в списке.", "Не выбрана запись");
            return;
        }
        if (!selected.IsEnabled)
        {
            ShowInfo("Эта запись уже отключена. Нажмите «Включить обратно», если хотите её вернуть.", "Уже отключено");
            return;
        }

        var answer = MessageBox.Show(
            $"Отключить «{selected.Name}» из автозагрузки?\n\nПрограмма не будет удалена — PC Boost сохранит резервную копию для обратного включения.",
            "Подтвердите действие",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _engine.DisableStartupItem(selected, out var message);
        RefreshStartupList();
        ShowInfo(message, "Автозагрузка");
    }

    private void EnableSelectedStartup()
    {
        var selected = GetSelectedStartupItem();
        if (selected is null)
        {
            ShowInfo("Сначала выберите запись в списке.", "Не выбрана запись");
            return;
        }
        if (selected.IsEnabled)
        {
            ShowInfo("Эта запись уже включена в автозагрузку.", "Уже включено");
            return;
        }

        _engine.EnableStartupItem(selected, out var message);
        RefreshStartupList();
        ShowInfo(message, "Автозагрузка");
    }

    private StartupItem? GetSelectedStartupItem()
    {
        return _startupList?.SelectedItems.Count > 0
            ? _startupList.SelectedItems[0].Tag as StartupItem
            : null;
    }

    private void ShowProcesses()
    {
        BeginPage("processes", "Процессы", "Показывает приложения с наибольшим использованием памяти. Завершайте только знакомые программы.");
        var page = CreatePageCanvas();

        var warningCard = new CardPanel
        {
            Location = new Point(30, 26),
            Size = new Size(850, 92),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(70, 50, 25),
            BorderColor = Color.FromArgb(130, 94, 45)
        };
        var warningTitle = CreateLabel("Внимание", 12.5F, FontStyle.Bold, Warning);
        warningTitle.Location = new Point(22, 16);
        warningTitle.AutoSize = true;
        warningCard.Controls.Add(warningTitle);
        var warningText = CreateLabel("Завершение приложения может привести к потере несохранённых данных. Критические системные процессы заблокированы.", 9.5F, FontStyle.Regular, MutedText);
        warningText.Location = new Point(22, 46);
        warningText.AutoSize = true;
        warningCard.Controls.Add(warningText);
        page.Controls.Add(warningCard);

        var listCard = new CardPanel
        {
            Location = new Point(30, 142),
            Size = new Size(850, 372),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border,
            Padding = new Padding(15)
        };
        _processList = CreateDarkListView();
        _processList.Bounds = new Rectangle(15, 16, listCard.Width - 30, 340);
        _processList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _processList.Columns.Add("Процесс", 360);
        _processList.Columns.Add("PID", 110, HorizontalAlignment.Right);
        _processList.Columns.Add("Использование памяти", 200, HorizontalAlignment.Right);
        listCard.Controls.Add(_processList);
        page.Controls.Add(listCard);

        _processStatus = CreateLabel("", 9.5F, FontStyle.Regular, MutedText);
        _processStatus.Location = new Point(31, 535);
        _processStatus.AutoSize = true;
        page.Controls.Add(_processStatus);

        _processRefreshButton = new Button { Text = "Обновить", Size = new Size(132, 42), Location = new Point(30, 568) };
        StyleButton(_processRefreshButton, SurfaceAlt, Border, 10F, 8);
        _processRefreshButton.Click += async (_, _) => await RefreshProcessListAsync();
        page.Controls.Add(_processRefreshButton);

        var end = new Button { Text = "Завершить выбранное", Size = new Size(206, 42), Location = new Point(176, 568) };
        StyleButton(end, Danger, Color.FromArgb(255, 125, 130), 10F, 8);
        end.Click += (_, _) => EndSelectedProcess();
        page.Controls.Add(end);

        var taskManager = new Button { Text = "Диспетчер задач", Size = new Size(180, 42), Location = new Point(394, 568) };
        StyleButton(taskManager, SurfaceAlt, Border, 10F, 8);
        taskManager.Click += (_, _) => _engine.LaunchExternalTool("taskmgr.exe");
        page.Controls.Add(taskManager);

        _ = RefreshProcessListAsync();
    }

    private async Task RefreshProcessListAsync()
    {
        if (_currentPage != "processes" || _processList is null || _processRefreshButton is null)
        {
            return;
        }
        try
        {
            _processRefreshButton.Enabled = false;
            if (_processStatus is not null) _processStatus.Text = "Получаем список процессов…";
            var processes = await Task.Run(_engine.GetProcesses);
            if (IsDisposed || _currentPage != "processes" || _processList is null)
            {
                return;
            }

            _processList.BeginUpdate();
            _processList.Items.Clear();
            foreach (var process in processes)
            {
                var item = new ListViewItem(process.Name) { Tag = process };
                item.SubItems.Add(process.Id.ToString());
                item.SubItems.Add(process.MemoryLabel);
                _processList.Items.Add(item);
            }
            _processList.EndUpdate();
            if (_processStatus is not null) _processStatus.Text = $"Показано процессов: {processes.Count}. Список отсортирован по используемой памяти.";
        }
        catch (Exception exception)
        {
            if (_processStatus is not null) _processStatus.Text = $"Не удалось обновить список: {exception.Message}";
        }
        finally
        {
            if (!IsDisposed && _currentPage == "processes" && _processRefreshButton is not null)
            {
                _processRefreshButton.Enabled = true;
            }
        }
    }

    private void EndSelectedProcess()
    {
        var selected = _processList?.SelectedItems.Count > 0
            ? _processList.SelectedItems[0].Tag as ProcessItem
            : null;
        if (selected is null)
        {
            ShowInfo("Выберите процесс в списке.", "Не выбран процесс");
            return;
        }

        var answer = MessageBox.Show(
            $"Завершить процесс «{selected.Name}» (PID {selected.Id})?\n\nНесохранённые данные в этой программе могут быть потеряны.",
            "Подтвердите завершение",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        _engine.TryTerminateProcess(selected, out var message);
        ShowInfo(message, "Процессы");
        _ = RefreshProcessListAsync();
    }

    private void ShowTools()
    {
        BeginPage("tools", "Инструменты", "Запускайте только нужные системные действия. PC Boost всегда показывает, что будет сделано.");
        var page = CreatePageCanvas();

        var grid = new TableLayoutPanel
        {
            Location = new Point(24, 26),
            Width = 856,
            Height = 500,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        for (var index = 0; index < 3; index++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        }

        grid.Controls.Add(CreateToolCard(
            "Сбросить DNS-кэш",
            "Помогает, если сайты открывают старый адрес или не открываются после смены сети.",
            "Выполнить",
            Accent,
            async () => await RunToolCommandAsync("Сброс DNS-кэша", _engine.FlushDnsAsync)), 0, 0);
        grid.Controls.Add(CreateToolCard(
            "Очистка диска Windows",
            "Откроет встроенный мастер Windows для очистки системного диска.",
            "Открыть",
            SurfaceAlt,
            () =>
            {
                LaunchTool("cleanmgr.exe", "Очистка диска Windows");
                return Task.CompletedTask;
            }), 1, 0);
        grid.Controls.Add(CreateToolCard(
            "Оптимизация накопителей",
            "Откроет штатный инструмент Windows для анализа и оптимизации HDD/SSD.",
            "Открыть",
            SurfaceAlt,
            () =>
            {
                LaunchTool("dfrgui.exe", "Оптимизация накопителей");
                return Task.CompletedTask;
            }), 0, 1);
        grid.Controls.Add(CreateToolCard(
            "Питание и производительность",
            "Откроет настройки режима питания Windows. На ноутбуке учитывайте расход батареи.",
            "Открыть",
            SurfaceAlt,
            () =>
            {
                LaunchTool("ms-settings:powersleep", "Настройки питания");
                return Task.CompletedTask;
            }), 1, 1);
        grid.Controls.Add(CreateToolCard(
            "Точка восстановления",
            "Создаст точку восстановления перед серьёзными изменениями. Нужны права администратора.",
            "Создать",
            Success,
            async () => await CreateRestorePointAsync()), 0, 2);
        grid.Controls.Add(CreateToolCard(
            "Проверка системных файлов",
            "Запустит SFC /scannow. Проверка может занять длительное время и требует администратора.",
            "Запустить SFC",
            Warning,
            async () => await RunSfcAsync()), 1, 2);
        page.Controls.Add(grid);

        var updateButton = new Button
        {
            Text = "Открыть Центр обновления Windows",
            Size = new Size(280, 42),
            Location = new Point(30, 552)
        };
        StyleButton(updateButton, SurfaceAlt, Border, 10F, 8);
        updateButton.Click += (_, _) => LaunchTool("ms-settings:windowsupdate", "Центр обновления Windows");
        page.Controls.Add(updateButton);
    }

    private CardPanel CreateToolCard(string title, string description, string buttonText, Color buttonColor, Func<Task> action)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(6),
            BackColor = Surface,
            BorderColor = Border
        };
        var titleLabel = CreateLabel(title, 12F, FontStyle.Bold, PrimaryText);
        titleLabel.Location = new Point(20, 20);
        titleLabel.AutoSize = true;
        titleLabel.MaximumSize = new Size(340, 0);
        card.Controls.Add(titleLabel);
        var descriptionLabel = CreateLabel(description, 9.2F, FontStyle.Regular, MutedText);
        descriptionLabel.Location = new Point(20, 54);
        descriptionLabel.Size = new Size(340, 54);
        descriptionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        descriptionLabel.AutoSize = false;
        card.Controls.Add(descriptionLabel);
        var actionButton = new Button
        {
            Text = buttonText,
            Size = new Size(150, 37),
            Location = new Point(20, 122),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        StyleButton(actionButton, buttonColor, GetHoverColor(buttonColor), 9.3F, 7);
        actionButton.Click += async (_, _) => await action();
        card.Controls.Add(actionButton);
        return card;
    }

    private void LaunchTool(string fileName, string title)
    {
        if (!_engine.LaunchExternalTool(fileName))
        {
            ShowInfo($"Не удалось открыть: {title}.", "Не удалось открыть");
        }
    }

    private async Task RunToolCommandAsync(string title, Task<CommandResult> commandTask)
    {
        UseWaitCursor = true;
        try
        {
            var result = await commandTask;
            var output = result.Output.Length > 1_500 ? result.Output[..1_500] + "…" : result.Output;
            ShowInfo(output, result.Success ? title : $"{title}: ошибка");
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task CreateRestorePointAsync()
    {
        if (!EnsureAdministrator("Для создания точки восстановления"))
        {
            return;
        }
        var answer = MessageBox.Show(
            "Создать точку восстановления «PC Boost Restore Point»? Windows может ограничить частоту создания точек.",
            "Точка восстановления",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            await RunToolCommandAsync("Точка восстановления", _engine.CreateRestorePointAsync());
        }
    }

    private async Task RunSfcAsync()
    {
        if (!EnsureAdministrator("Для проверки системных файлов SFC"))
        {
            return;
        }
        var answer = MessageBox.Show(
            "Запустить SFC /scannow? Проверка может занять 10–30 минут. Не выключайте компьютер до её завершения.",
            "Проверка системных файлов",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            await RunToolCommandAsync("SFC завершён", _engine.RunSystemFileCheckerAsync());
        }
    }

    private void ShowRealtime()
    {
        BeginPage("realtime", "Фоновый режим", "Реальное состояние ПК каждые 2 секунды и безопасное обслуживание без закрытия ваших программ");
        var page = CreatePageCanvas();

        var statusCard = new CardPanel
        {
            Location = new Point(30, 26),
            Size = new Size(850, 106),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var statusTitle = CreateLabel("Статус в реальном времени", 13F, FontStyle.Bold, PrimaryText);
        statusTitle.AutoSize = true;
        statusTitle.Location = new Point(22, 17);
        statusCard.Controls.Add(statusTitle);
        _realtimeStatus = CreateLabel("Запуск мониторинга…", 11F, FontStyle.Bold, Accent);
        _realtimeStatus.AutoSize = true;
        _realtimeStatus.Location = new Point(22, 49);
        statusCard.Controls.Add(_realtimeStatus);
        _realtimeMaintenance = CreateLabel(_maintenanceMessage, 8.8F, FontStyle.Regular, MutedText);
        _realtimeMaintenance.AutoSize = false;
        _realtimeMaintenance.Size = new Size(650, 20);
        _realtimeMaintenance.Location = new Point(22, 76);
        _realtimeMaintenance.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        statusCard.Controls.Add(_realtimeMaintenance);
        page.Controls.Add(statusCard);

        var metrics = new TableLayoutPanel
        {
            Location = new Point(24, 154),
            Width = 856,
            Height = 132,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ColumnCount = 3,
            RowCount = 1
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
        metrics.Controls.Add(CreateRealtimeMetricCard("Загрузка ЦП", "cpu", out _realtimeCpu), 0, 0);
        metrics.Controls.Add(CreateRealtimeMetricCard("Использование ОЗУ", "memory", out _realtimeMemory), 1, 0);
        metrics.Controls.Add(CreateRealtimeMetricCard("Использование диска", "disk", out _realtimeDisk), 2, 0);
        page.Controls.Add(metrics);

        var settingsCard = new CardPanel
        {
            Location = new Point(30, 316),
            Size = new Size(850, 235),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var settingsTitle = CreateLabel("Настройки фоновой работы", 13F, FontStyle.Bold, PrimaryText);
        settingsTitle.AutoSize = true;
        settingsTitle.Location = new Point(22, 18);
        settingsCard.Controls.Add(settingsTitle);

        var monitoringCheckbox = CreateSettingCheckbox("Фоновый мониторинг в реальном времени", _settings.BackgroundMonitoringEnabled);
        monitoringCheckbox.Location = new Point(22, 58);
        monitoringCheckbox.CheckedChanged += (_, _) => SetBackgroundMonitoringEnabled(monitoringCheckbox.Checked);
        settingsCard.Controls.Add(monitoringCheckbox);

        var autoMaintenanceCheckbox = CreateSettingCheckbox("Автообслуживание: чистить старые файлы из пользовательского Temp", _settings.AutoMaintenanceEnabled);
        autoMaintenanceCheckbox.Location = new Point(22, 91);
        autoMaintenanceCheckbox.CheckedChanged += (_, _) =>
        {
            _settings.AutoMaintenanceEnabled = autoMaintenanceCheckbox.Checked;
            _maintenanceMessage = autoMaintenanceCheckbox.Checked
                ? "Автообслуживание включено: проверка временных файлов каждые 30 минут."
                : "Автоочистка временных файлов отключена.";
            AppSettingsStore.Save(_settings);
            UpdateRealtimeStatusOnly();
        };
        settingsCard.Controls.Add(autoMaintenanceCheckbox);

        var overlayCheckbox = CreateSettingCheckbox("Показывать мини-панель поверх всех программ", _settings.OverlayVisible);
        overlayCheckbox.Location = new Point(22, 124);
        overlayCheckbox.CheckedChanged += (_, _) => SetOverlayVisible(overlayCheckbox.Checked);
        settingsCard.Controls.Add(overlayCheckbox);

        var startupCheckbox = CreateSettingCheckbox("Запускать PC Boost вместе с Windows в фоновом режиме", _engine.IsPcBoostStartupEnabled());
        startupCheckbox.Location = new Point(22, 157);
        var updatingStartup = false;
        startupCheckbox.CheckedChanged += (_, _) =>
        {
            if (updatingStartup)
            {
                return;
            }
            if (!_engine.SetPcBoostStartupEnabled(startupCheckbox.Checked, Application.ExecutablePath, out var message))
            {
                updatingStartup = true;
                startupCheckbox.Checked = _engine.IsPcBoostStartupEnabled();
                updatingStartup = false;
            }
            _maintenanceMessage = message;
            UpdateRealtimeStatusOnly();
        };
        settingsCard.Controls.Add(startupCheckbox);

        var settingsHint = CreateLabel("Автообслуживание не трогает документы, фото, программы, драйверы, службы, реестр или корзину. Удаляются только незанятые файлы старше 7 дней в пользовательском %TEMP%.", 8.4F, FontStyle.Regular, MutedText);
        settingsHint.Location = new Point(22, 193);
        settingsHint.Size = new Size(770, 30);
        settingsHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        settingsCard.Controls.Add(settingsHint);
        page.Controls.Add(settingsCard);

        var showWindowButton = new Button
        {
            Text = "Показать мини-панель",
            Size = new Size(210, 42),
            Location = new Point(30, 579)
        };
        StyleButton(showWindowButton, Accent, AccentHover, 10F, 8);
        showWindowButton.Click += (_, _) => SetOverlayVisible(true);
        page.Controls.Add(showWindowButton);

        var hideToTrayButton = new Button
        {
            Text = "Свернуть в трей",
            Size = new Size(175, 42),
            Location = new Point(252, 579)
        };
        StyleButton(hideToTrayButton, SurfaceAlt, Border, 10F, 8);
        hideToTrayButton.Click += (_, _) => HideMainWindow();
        page.Controls.Add(hideToTrayButton);

        if (_latestPerformance is not null)
        {
            UpdateRealtimeVisuals(_latestPerformance);
        }
        else if (!_settings.BackgroundMonitoringEnabled)
        {
            UpdateRealtimeStatusOnly();
        }
    }

    private CardPanel CreateRealtimeMetricCard(string title, string key, out Label metricLabel)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(6),
            BackColor = Surface,
            BorderColor = Border
        };
        var titleLabel = CreateLabel(title, 9.5F, FontStyle.Bold, MutedText);
        titleLabel.AutoSize = true;
        titleLabel.Location = new Point(17, 17);
        card.Controls.Add(titleLabel);
        metricLabel = CreateLabel("—", 22F, FontStyle.Bold, PrimaryText);
        metricLabel.AutoSize = true;
        metricLabel.Location = new Point(17, 49);
        card.Controls.Add(metricLabel);
        var caption = CreateLabel(key == "cpu" ? "Текущая нагрузка" : key == "memory" ? "Занято памяти" : "Занято системного диска", 8.4F, FontStyle.Regular, MutedText);
        caption.AutoSize = true;
        caption.Location = new Point(17, 94);
        card.Controls.Add(caption);
        return card;
    }

    private static CheckBox CreateSettingCheckbox(string text, bool value)
    {
        return new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            ForeColor = PrimaryText,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.7F, FontStyle.Regular, GraphicsUnit.Point),
            Cursor = Cursors.Hand
        };
    }

    private void ShowAbout()
    {
        BeginPage("about", "О программе", "PC Boost 1.1 — безопасная фоновая оптимизация для Windows 10 и Windows 11");
        var page = CreatePageCanvas();

        var card = new CardPanel
        {
            Location = new Point(30, 26),
            Size = new Size(850, 430),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Surface,
            BorderColor = Border
        };
        var title = CreateLabel("Что делает PC Boost", 16F, FontStyle.Bold, PrimaryText);
        title.Location = new Point(24, 22);
        title.AutoSize = true;
        card.Controls.Add(title);
        var body = CreateLabel(
            "• Анализирует свободное место, память, временные файлы и автозагрузку.\n\n" +
            "• Очищает только выбранные временные папки; занятые и защищённые Windows файлы пропускает.\n\n" +
            "• Может отключать автозагрузку только текущего пользователя и хранит обратимую резервную копию.\n\n" +
            "• Показывает самые «тяжёлые» процессы и защищает критические системные процессы от завершения.\n\n" +
            "• В фоновом режиме обновляет мини-панель поверх программ каждые 2 секунды и может очищать только старые файлы из пользовательского Temp.\n\n" +
            "• Не удаляет драйверы, личные файлы, точки восстановления, обновления Windows и не использует агрессивную чистку реестра.",
            10F,
            FontStyle.Regular,
            MutedText);
        body.Location = new Point(25, 67);
        body.Size = new Size(760, 310);
        body.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        body.AutoSize = false;
        card.Controls.Add(body);
        page.Controls.Add(card);

        var version = CreateLabel("Версия 1.1 • Windows 10/11 x64 • .NET 8", 9.5F, FontStyle.Regular, MutedText);
        version.Location = new Point(31, 480);
        version.AutoSize = true;
        page.Controls.Add(version);
    }

    private void UpdateAdminStatus()
    {
        var isAdministrator = NativeMethods.IsAdministrator();
        _adminStatus.Text = isAdministrator ? "● Администратор" : "● Обычный режим";
        _adminStatus.ForeColor = isAdministrator ? Success : MutedText;
        _adminButton.Visible = !isAdministrator;
        _adminStatus.Left = _adminButton.Visible
            ? _adminButton.Left - _adminStatus.Width - 14
            : _adminButton.Left + _adminButton.Width - _adminStatus.Width;
    }

    private bool EnsureAdministrator(string actionDescription)
    {
        if (NativeMethods.IsAdministrator())
        {
            return true;
        }

        var answer = MessageBox.Show(
            $"{actionDescription} нужны права администратора. Перезапустить PC Boost с повышенными правами?",
            "Нужны права администратора",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1);
        if (answer == DialogResult.Yes)
        {
            RestartAsAdministrator();
        }
        return false;
    }

    private void RestartAsAdministrator()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            _isExiting = true;
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The UAC prompt may be cancelled; the current non-admin session remains usable.
        }
    }

    private static ListView CreateDarkListView(bool checkBoxes = false)
    {
        return new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            GridLines = false,
            CheckBoxes = checkBoxes,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(20, 30, 48),
            ForeColor = PrimaryText,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point)
        };
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", size, style, GraphicsUnit.Point),
            ForeColor = color,
            BackColor = Color.Transparent
        };
    }

    private static void StyleButton(Button button, Color baseColor, Color hoverColor, float fontSize, int radius)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = baseColor;
        button.ForeColor = PrimaryText;
        button.Font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.Tag = new ButtonColorState(baseColor, hoverColor);
        button.MouseEnter += (_, _) =>
        {
            if (button.Enabled && button.Tag is ButtonColorState state)
            {
                button.BackColor = state.Hover;
            }
        };
        button.MouseLeave += (_, _) =>
        {
            if (button.Tag is ButtonColorState state)
            {
                button.BackColor = state.Base;
            }
        };
        button.EnabledChanged += (_, _) => button.ForeColor = button.Enabled ? PrimaryText : MutedText;
        if (radius > 0)
        {
            button.Resize += (_, _) => button.Region = CreateRoundRegion(button.ClientRectangle, radius);
        }
        button.Region = CreateRoundRegion(button.ClientRectangle, radius);
    }

    private static Region CreateRoundRegion(Rectangle rectangle, int radius)
    {
        var path = CreateRoundedPath(rectangle, radius);
        return new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var arc = new Rectangle(rectangle.X, rectangle.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color GetHoverColor(Color baseColor)
    {
        return Color.FromArgb(
            Math.Min(baseColor.R + 18, 255),
            Math.Min(baseColor.G + 18, 255),
            Math.Min(baseColor.B + 18, 255));
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays} д. {uptime.Hours} ч."
            : uptime.TotalHours >= 1 ? $"{uptime.Hours} ч. {uptime.Minutes} мин." : $"{uptime.Minutes} мин.";
    }

    private static void ShowInfo(string message, string title)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private sealed record ButtonColorState(Color Base, Color Hover);

    private sealed class CardPanel : Panel
    {
        public Color BorderColor { get; init; } = Border;
        public int CornerRadius { get; init; } = 13;

        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Surface;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rectangle = ClientRectangle;
            rectangle.Width--;
            rectangle.Height--;
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                return;
            }

            using var path = CreateRoundedPath(rectangle, CornerRadius);
            using var fill = new SolidBrush(BackColor);
            using var pen = new Pen(BorderColor);
            eventArgs.Graphics.FillPath(fill, path);
            eventArgs.Graphics.DrawPath(pen, path);
        }
    }
}
