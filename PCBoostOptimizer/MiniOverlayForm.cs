using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PCBoostOptimizer;

internal sealed class MiniOverlayForm : Form
{
    private readonly Label _cpuValue = CreateLabel("—", 18F, FontStyle.Bold, Color.White);
    private readonly Label _memoryValue = CreateLabel("—", 18F, FontStyle.Bold, Color.White);
    private readonly Label _diskValue = CreateLabel("—", 18F, FontStyle.Bold, Color.White);
    private readonly Label _statusValue = CreateLabel("Запуск мониторинга…", 8.5F, FontStyle.Regular, Color.FromArgb(174, 193, 220));
    private Point _dragCursorStart;
    private Point _dragFormStart;
    private bool _dragging;

    public MiniOverlayForm()
    {
        Text = "PC Boost — состояние ПК";
        ClientSize = new Size(308, 156);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(18, 30, 49);
        Opacity = 0.96;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Padding = new Padding(1);

        var title = CreateLabel("⚡  PC BOOST", 10F, FontStyle.Bold, Color.FromArgb(116, 175, 255));
        title.AutoSize = true;
        title.Location = new Point(16, 13);
        Controls.Add(title);

        var hint = CreateLabel("перетащите окно за свободную область", 7.5F, FontStyle.Regular, Color.FromArgb(135, 154, 181));
        hint.AutoSize = true;
        hint.Location = new Point(16, 33);
        Controls.Add(hint);

        var closeButton = new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            ForeColor = Color.FromArgb(181, 198, 222),
            BackColor = BackColor,
            Font = new Font("Segoe UI", 15F, FontStyle.Regular, GraphicsUnit.Point),
            Size = new Size(34, 31),
            Location = new Point(266, 7),
            Cursor = Cursors.Hand
        };
        closeButton.Click += (_, _) => HideOverlayRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(closeButton);

        var separator = new Panel { BackColor = Color.FromArgb(55, 75, 107), Location = new Point(15, 53), Size = new Size(278, 1) };
        Controls.Add(separator);

        AddMetric("ЦП", _cpuValue, 16);
        AddMetric("ОЗУ", _memoryValue, 111);
        AddMetric("ДИСК", _diskValue, 207);

        _statusValue.Location = new Point(16, 127);
        _statusValue.Size = new Size(270, 18);
        _statusValue.AutoEllipsis = true;
        Controls.Add(_statusValue);

        AttachDrag(this);
        AttachDrag(title);
        AttachDrag(hint);
        AttachDrag(separator);
        AttachDrag(_cpuValue);
        AttachDrag(_memoryValue);
        AttachDrag(_diskValue);
        AttachDrag(_statusValue);

        Resize += (_, _) => ApplyRoundedRegion();
        ApplyRoundedRegion();
    }

    public event EventHandler? HideOverlayRequested;
    public event EventHandler? OverlayLocationChanged;

    public void UpdateSnapshot(PerformanceSnapshot snapshot, string status, Color statusColor)
    {
        _cpuValue.Text = $"{snapshot.CpuUsagePercent:N0}%";
        _memoryValue.Text = $"{snapshot.MemoryUsagePercent:N0}%";
        _diskValue.Text = $"{snapshot.DiskUsagePercent:N0}%";
        _statusValue.Text = status;
        _statusValue.ForeColor = statusColor;
    }

    public void SetInitialLocation(int left, int top)
    {
        if (left >= 0 && top >= 0)
        {
            Location = new Point(left, top);
            return;
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
    }

    private void AddMetric(string title, Label value, int left)
    {
        var titleLabel = CreateLabel(title, 8F, FontStyle.Bold, Color.FromArgb(145, 166, 197));
        titleLabel.AutoSize = true;
        titleLabel.Location = new Point(left, 69);
        Controls.Add(titleLabel);

        value.AutoSize = true;
        value.Location = new Point(left, 86);
        Controls.Add(value);
        AttachDrag(titleLabel);
    }

    private void AttachDrag(Control control)
    {
        control.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left)
            {
                return;
            }
            _dragCursorStart = System.Windows.Forms.Cursor.Position;
            _dragFormStart = Location;
            _dragging = true;
        };
        control.MouseMove += (_, eventArgs) =>
        {
            if (!_dragging || eventArgs.Button != MouseButtons.Left)
            {
                return;
            }
            var cursor = System.Windows.Forms.Cursor.Position;
            Location = new Point(
                _dragFormStart.X + cursor.X - _dragCursorStart.X,
                _dragFormStart.Y + cursor.Y - _dragCursorStart.Y);
        };
        control.MouseUp += (_, eventArgs) =>
        {
            if (_dragging)
            {
                _dragging = false;
                OverlayLocationChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    private void ApplyRoundedRegion()
    {
        var rectangle = ClientRectangle;
        rectangle.Width--;
        rectangle.Height--;
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }

        using var path = new GraphicsPath();
        const int radius = 15;
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        var previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
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
}
