using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

public class MainForm : Form
{
    private const int MaxBars = 60;

    private const string DefaultHost = "baidu.com";

    private const int W = 180;

    private const int H = 90;

    private string _host;

    private readonly int[] _bars = new int[60];

    private int _count;

    private int _head;

    private bool _drag;

    private bool _dragged;

    private bool _pingInProgress;

    private Point _dragOriginScreen;

    private Point _dragOriginWindow;

    private readonly DashboardSurface _surface;

    private int _currentPing = -1;

    private int CurrentPing => _currentPing;

    private readonly ToolTip _usageTip = new ToolTip();

    private readonly System.Windows.Forms.Timer _tooltipTimer;

    private Point _lastTooltipCursor;

    private DateTime _tooltipStationarySince;

    private bool _tooltipVisible;

    private string _tooltipText = "Daily network usage. Right-click for network data and settings.";

    private readonly NetworkUsageTracker _usage;

    private readonly NotifyIcon _tray;

    private readonly Icon _trayIcon;

    private readonly System.Windows.Forms.Timer _timer;

    private readonly System.Windows.Forms.Timer _usageTimer;

    private readonly System.Windows.Forms.Timer _healthTimer;

    private readonly bool _watchdogRestartUsed;

    private DateTime? _captureFaultStartedUtc;

    private readonly System.Windows.Forms.Timer _warningPulseTimer;

    private int _warningPulseStep;

    private int _warningPulseDirection = 1;

    private static string HostFilePath => Path.Combine(AppContext.BaseDirectory, "pinghost.txt");

    private static string UsageFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PingMonitor", "network-usage.json");

    private static string GetLegacyUsageFilePath(string suppliedPath) =>
        suppliedPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PingMonitor", "network-usage.json");

    public MainForm(string initialHost = null, string legacyUsageFilePath = null, bool watchdogRestartUsed = false)
    {
        _watchdogRestartUsed = watchdogRestartUsed;
        _host = NormalizeHost(initialHost);
        if (string.IsNullOrEmpty(_host))
            _host = LoadSavedHost();
        if (string.IsNullOrEmpty(_host))
            _host = DefaultHost;

        _usage = new NetworkUsageTracker(UsageFilePath, GetLegacyUsageFilePath(legacyUsageFilePath));

        base.ClientSize = new Size(W, H);
        base.FormBorderStyle = FormBorderStyle.None;
        base.BackColor = Color.FromArgb(3, 10, 27);
        base.TopMost = true;
        base.ShowInTaskbar = false;
        base.StartPosition = FormStartPosition.Manual;
        base.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - W - 16, Screen.PrimaryScreen.WorkingArea.Top + 16);
        _surface = new DashboardSurface(this)
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(3, 10, 27),
            ContextMenuStrip = null
        };
        _surface.MouseDown += SurfaceMouseDown;
        _surface.MouseMove += SurfaceMouseMove;
        _surface.MouseUp += delegate { _drag = false; };
        _surface.MouseEnter += delegate
        {
            _surface.Cursor = Cursors.Hand;
            ResetTooltipStationaryTime();
        };
        _surface.MouseLeave += delegate
        {
            if (!_drag) _surface.Cursor = Cursors.Default;
            HideUsageTooltip();
        };
        _surface.MouseClick += SurfaceMouseClick;
        _usageTip.AutoPopDelay = 60000;
        _lastTooltipCursor = Cursor.Position;
        _tooltipStationarySince = DateTime.UtcNow;
        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _tooltipTimer.Tick += delegate { UpdateUsageTooltip(); };
        _tooltipTimer.Start();
        Controls.Add(_surface);

        var widgetMenu = new ContextMenuStrip();
        widgetMenu.Items.Add("Network Data", null, delegate { ShowNetworkData(); });
        widgetMenu.Items.Add("Set Reset Time...", null, delegate { ShowSetResetTimeDialog(); });
        widgetMenu.Items.Add("Set Host...", null, delegate { ShowSetHostDialog(); });
        widgetMenu.Items.Add("Hide", null, delegate { Hide(); });
        widgetMenu.Items.Add("Exit", null, delegate
        {
            _tray.Visible = false;
            Application.Exit();
        });
        base.ContextMenuStrip = widgetMenu;
        _surface.ContextMenuStrip = widgetMenu;
        _trayIcon = MakeIcon();
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "Ping Monitor - " + _host
        };
        ContextMenuStrip contextMenuStrip = new ContextMenuStrip
        {
            Items =
            {
                {
                    "Network Data",
                    (Image)null,
                    (EventHandler)delegate { ShowNetworkData(); }
                },
                {
                    "Set Reset Time...",
                    (Image)null,
                    (EventHandler)delegate { ShowSetResetTimeDialog(); }
                },
                {
                    "Set Host...",
                    (Image)null,
                    (EventHandler)delegate
                    {
                        ShowSetHostDialog();
                    }
                },
                {
                    "Show / Hide",
                    (Image)null,
                    (EventHandler)delegate
                    {
                        base.Visible = !base.Visible;
                    }
                },
                {
                    "Exit",
                    (Image)null,
                    (EventHandler)delegate
                    {
                        _tray.Visible = false;
                        Application.Exit();
                    }
                }
            }
        };
        _tray.ContextMenuStrip = contextMenuStrip;
        _tray.MouseClick += delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (!Visible || WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                    Show();
                    Activate();
                }
                else
                    Hide();
            }
        };
        _timer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _timer.Tick += async delegate
        {
            await Tick();
        };
        _timer.Start();
        _usageTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _usageTimer.Tick += delegate
        {
            _usage.Sample();
            UpdateUsageDisplay();
            CheckDailyUsageAlert();
        };
        _usageTimer.Start();
        _warningPulseTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _warningPulseTimer.Tick += delegate
        {
            if (_usage.TotalBytes >= NetworkUsageTracker.DailyAlertThresholdBytes)
            {
                _warningPulseStep += _warningPulseDirection;
                if (_warningPulseStep >= 6) _warningPulseDirection = -1;
                else if (_warningPulseStep <= 0) _warningPulseDirection = 1;
                _surface.Invalidate();
            }
            else if (_warningPulseStep != 0)
            {
                _warningPulseStep = 0;
                _warningPulseDirection = 1;
                _surface.Invalidate();
            }
        };
        _warningPulseTimer.Start();
        _healthTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _healthTimer.Tick += delegate { CheckApplicationHealth(); };
        _healthTimer.Start();
        CheckDailyUsageAlert();
    }

    private void CheckApplicationHealth()
    {
        string status = _usage.CaptureStatus;
        bool recoverableFault = status.StartsWith("Usage save failed:", StringComparison.OrdinalIgnoreCase) ||
            status.StartsWith("Capture temporarily unavailable; retrying:", StringComparison.OrdinalIgnoreCase);
        if (!recoverableFault)
        {
            _captureFaultStartedUtc = null;
            return;
        }

        _captureFaultStartedUtc ??= DateTime.UtcNow;
        if (_watchdogRestartUsed || DateTime.UtcNow - _captureFaultStartedUtc.Value < TimeSpan.FromSeconds(45))
            return;

        if (Program.StartWatchdogRestart())
        {
            _healthTimer.Stop();
            Application.Exit();
        }
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim();
        if (host.Contains("://"))
        {
            try { host = new Uri(host).Host; } catch { return null; }
        }
        else
        {
            int slash = host.IndexOf('/');
            if (slash >= 0) host = host.Substring(0, slash);
        }
        host = host.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Contains(" ")) return null;
        return host;
    }

    private static string LoadSavedHost()
    {
        try
        {
            if (File.Exists(HostFilePath))
            {
                var h = NormalizeHost(File.ReadAllText(HostFilePath));
                if (!string.IsNullOrEmpty(h)) return h;
            }
        }
        catch { }
        return null;
    }

    private void SaveHost()
    {
        try { File.WriteAllText(HostFilePath, _host); } catch { }
    }

    private void SurfaceMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _drag = true;
            _dragged = false;
            _dragOriginScreen = Cursor.Position;
            _dragOriginWindow = Location;
        }
    }

    private void SurfaceMouseMove(object sender, MouseEventArgs e)
    {
        Point cursorPosition = Cursor.Position;
        if (cursorPosition != _lastTooltipCursor)
        {
            _lastTooltipCursor = cursorPosition;
            _tooltipStationarySince = DateTime.UtcNow;
            HideUsageTooltip();
        }

        if (_drag)
        {
            Point current = Cursor.Position;
            if (Math.Abs(current.X - _dragOriginScreen.X) + Math.Abs(current.Y - _dragOriginScreen.Y) >= 3)
                _dragged = true;
            if (_dragged)
                Location = new Point(_dragOriginWindow.X + current.X - _dragOriginScreen.X,
                    _dragOriginWindow.Y + current.Y - _dragOriginScreen.Y);
            return;
        }

        _surface.Cursor = Cursors.Hand;
    }

    private void SurfaceMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        if (_dragged)
        {
            _dragged = false;
            return;
        }

        if (DashboardSurface.SettingsBounds.Contains(e.Location))
            base.ContextMenuStrip.Show(_surface, DashboardSurface.SettingsBounds.Left, DashboardSurface.SettingsBounds.Bottom);
        else if (DashboardSurface.CloseBounds.Contains(e.Location))
            Hide();
        else if (_surface.HostTextBounds.Contains(e.Location))
            ShowSetHostDialog();
    }

    private void ShowSetHostDialog()
    {
        using (var dlg = new SetHostDialog(_host))
        {
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                var h = NormalizeHost(dlg.HostValue);
                if (string.IsNullOrEmpty(h))
                {
                    MessageBox.Show(this, "Invalid host name or IP address.", "Ping Monitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _host = h;
                _tray.Text = "Ping Monitor - " + _host;
                SaveHost();
                // reset graph so old host data does not mix with new
                Array.Clear(_bars, 0, _bars.Length);
                _count = 0;
                _head = 0;
                _ = Tick();
            }
        }
    }

    private async Task Tick()
    {
        if (_pingInProgress)
            return;

        _pingInProgress = true;
        try
        {
            int num = await ProbeHostAsync(_host);
            _usage.SetTrafficEnabled(num >= 0);

            _bars[_head] = num;
            _head = (_head + 1) % MaxBars;
            if (_count < MaxBars)
                _count++;
            _currentPing = num;
            _surface.Invalidate();
        }
        finally
        {
            _pingInProgress = false;
        }
    }

    private static async Task<int> ProbeHostAsync(string host)
    {
        // ICMP is commonly filtered even when HTTPS works. Race a conventional
        // echo request with a short TCP connection probe so status reflects
        // actual reachability instead of treating ICMP filtering as an outage.
        Task<int> pingTask = ProbeIcmpAsync(host);
        Task<int> tcpTask = ProbeTcpAsync(host);
        while (pingTask != null || tcpTask != null)
        {
            Task<int> completed = pingTask == null ? tcpTask : tcpTask == null ? pingTask : await Task.WhenAny(pingTask, tcpTask);
            int result;
            try { result = await completed; } catch { result = -1; }
            if (result >= 0)
                return result;
            if (ReferenceEquals(completed, pingTask)) pingTask = null;
            if (ReferenceEquals(completed, tcpTask)) tcpTask = null;
        }
        return -1;
    }

    private static async Task<int> ProbeIcmpAsync(string host)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(host, 1800);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
        }
        catch { return -1; }
    }

    private static async Task<int> ProbeTcpAsync(string host)
    {
        foreach (int port in new[] { 443, 80 })
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(900));
                return Math.Max(1, (int)stopwatch.ElapsedMilliseconds);
            }
            catch { }
        }
        return -1;
    }

    private void UpdateUsageDisplay()
    {
        _surface.Invalidate();
        _tooltipText =
            $"Total: {FormatCompactBytes(_usage.TotalBytes)}\n" +
            $"Download / received: {FormatCompactBytes(_usage.ReceivedBytes)}\n" +
            $"Upload / sent: {FormatCompactBytes(_usage.SentBytes)}\n" +
            _usage.CaptureStatus;
    }

    private void ResetTooltipStationaryTime()
    {
        _lastTooltipCursor = Cursor.Position;
        _tooltipStationarySince = DateTime.UtcNow;
        HideUsageTooltip();
    }

    private void UpdateUsageTooltip()
    {
        if (!Visible || WindowState == FormWindowState.Minimized || !_surface.ClientRectangle.Contains(_surface.PointToClient(Cursor.Position)))
        {
            HideUsageTooltip();
            return;
        }

        Point position = Cursor.Position;
        if (position != _lastTooltipCursor)
        {
            _lastTooltipCursor = position;
            _tooltipStationarySince = DateTime.UtcNow;
            HideUsageTooltip();
            return;
        }

        if (!_tooltipVisible && DateTime.UtcNow - _tooltipStationarySince >= TimeSpan.FromSeconds(5))
        {
            Point local = _surface.PointToClient(position);
            _usageTip.Show(_tooltipText, _surface, local.X + 12, local.Y + 18, 60000);
            _tooltipVisible = true;
        }
    }

    private void HideUsageTooltip()
    {
        if (!_tooltipVisible) return;
        _usageTip.Hide(_surface);
        _tooltipVisible = false;
    }

    private void ShowNetworkData()
    {
        using var dialog = new NetworkDataDialog(_usage, GetPingHistorySnapshot);
        dialog.ShowDialog(this);
        UpdateUsageDisplay();
        CheckDailyUsageAlert();
    }

    private int[] GetPingHistorySnapshot()
    {
        var samples = new int[_count];
        for (int i = 0; i < _count; i++)
        {
            int index = (_head - _count + i + MaxBars) % MaxBars;
            samples[i] = _bars[index];
        }
        return samples;
    }

    private void ShowSetResetTimeDialog()
    {
        using var dialog = new ResetTimeDialog(_usage.ResetTime);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _usage.SetResetTime(dialog.ResetTime);
        UpdateUsageDisplay();
        CheckDailyUsageAlert();
    }

    private void CheckDailyUsageAlert()
    {
        if (!_usage.ShouldShowThresholdAlert)
            return;

        _usage.MarkThresholdAlertSent();
        _tray.ShowBalloonTip(
            5000,
            "Ping Monitor",
            $"Network usage in this cycle exceeded 5 GB ({FormatBytes(_usage.TotalBytes)} total).",
            ToolTipIcon.Info);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    private static string FormatCompactBytes(long bytes)
    {
        if (bytes < 1000) return $"{bytes}B";
        string[] units = { "K", "M", "G", "T", "P" };
        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1000;
            unit++;
        } while (value >= 1000 && unit < units.Length - 1);
        return $"{value:0.#}{units[unit]}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _usageTimer.Stop();
        _timer.Dispose();
        _usageTimer.Dispose();
        _warningPulseTimer.Stop();
        _warningPulseTimer.Dispose();
        _healthTimer.Stop();
        _healthTimer.Dispose();
        _usageTip.Dispose();
        _tooltipTimer.Stop();
        _tooltipTimer.Dispose();
        _usage.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();
        base.OnFormClosing(e);
    }

    private sealed class DashboardSurface : Control
    {
        private readonly MainForm _owner;
        private static readonly Color Cyan = Color.FromArgb(0, 220, 255);
        private static readonly Color Blue = Color.FromArgb(0, 126, 255);
        private static readonly Color Green = Color.FromArgb(0, 236, 126);
        private static readonly Color Violet = Color.FromArgb(170, 83, 255);

        public static readonly Rectangle SettingsBounds = new(136, 1, 20, 19);
        public static readonly Rectangle CloseBounds = new(158, 1, 20, 19);
        public Rectangle HostTextBounds { get; private set; }

        public DashboardSurface(MainForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(2, 8, 24));
            DrawFrame(g);
            DrawHeader(g);
            DrawGauge(g);
            DrawChart(g);
            DrawFooter(g);
        }

        private void DrawFrame(Graphics g)
        {
            Rectangle frame = ClientRectangle;
            using var fill = new LinearGradientBrush(frame, Color.FromArgb(7, 24, 53), Color.FromArgb(3, 12, 31), 90f);
            g.FillRectangle(fill, frame);
        }

        private void DrawHeader(Graphics g)
        {
            using Font hostFont = new("Segoe UI", 8.5f, FontStyle.Bold);
            using var hostBrush = new SolidBrush(Color.FromArgb(231, 243, 255));
            string host = _owner._host;
            while (host.Length > 7 && g.MeasureString(host, hostFont).Width > 112)
                host = host[..^1];
            if (host != _owner._host) host = host[..^1] + "…";
            SizeF hostSize = g.MeasureString(host, hostFont);
            HostTextBounds = new Rectangle(6, 3, (int)Math.Ceiling(hostSize.Width), (int)Math.Ceiling(hostSize.Height));
            g.DrawString(host, hostFont, hostBrush, 6, 4);

            DrawGear(g, 140, 5, 11);
            using Pen closePen = new(Color.FromArgb(190, 215, 255), 1.6f)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLine(closePen, 165, 6, 173, 14);
            g.DrawLine(closePen, 173, 6, 165, 14);
        }

        private void DrawGauge(Graphics g)
        {
            RectangleF ring = new(7, 24, 42, 42);
            bool online = _owner.CurrentPing >= 0;
            Color accent = online ? Green : Color.FromArgb(255, 84, 112);
            using Pen track = new(Color.FromArgb(56, 90, 137, 169), 4.5f)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            using Pen halo = new(Color.FromArgb(37, accent), 8f)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            using Pen arc = new(accent, 3.2f)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawArc(track, ring, 135, 270);
            float sweep = online ? GetPingSweep(_owner.CurrentPing) : 14f;
            g.DrawArc(halo, ring, 135, sweep);
            g.DrawArc(arc, ring, 135, sweep);

            string ping = online ? _owner.CurrentPing.ToString() : "—";
            float fontSize = ping.Length >= 4 ? 8.2f : 10.5f;
            using Font pingFont = new("Segoe UI", fontSize, FontStyle.Bold);
            using Font msFont = new("Segoe UI", 7.5f, FontStyle.Bold);
            using var white = new SolidBrush(Color.White);
            DrawCentered(g, ping, pingFont, white, new RectangleF(10, 34, 36, 14));
            DrawCentered(g, "ms", msFont, white, new RectangleF(10, 46, 36, 11));
        }

        private static float GetPingSweep(int pingMilliseconds)
        {
            if (pingMilliseconds <= 60)
                return 260f;
            if (pingMilliseconds <= 600)
                return 260f - (pingMilliseconds - 60) * (40f / 540f);
            if (pingMilliseconds <= 1000)
                return 220f - (pingMilliseconds - 600) * (85f / 400f);
            return Math.Max(14f, 135f - (pingMilliseconds - 1000) * 0.09f);
        }

        private void DrawChart(Graphics g)
        {
            RectangleF chart = new(57, 23, 115, 42);
            using (var fill = new LinearGradientBrush(chart, Color.FromArgb(7, 24, 49), Color.FromArgb(4, 18, 38), 90f))
                g.FillRectangle(fill, chart);
            using Pen edge = new(Color.FromArgb(34, 0, 148, 231), .7f);
            g.DrawLine(edge, chart.Left, chart.Top, chart.Right, chart.Top);
            g.DrawLine(edge, chart.Left, chart.Bottom, chart.Right, chart.Bottom);

            using Pen guide = new(Color.FromArgb(80, 0, 165, 225), .7f)
            { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(guide, 59, 36, 170, 36);
            int max = 1;
            for (int i = 0; i < _owner._count; i++)
                max = Math.Max(max, Math.Max(0, _owner._bars[i]));

            float baseline = 63;
            const float step = 1.78f;
            for (int i = 0; i < _owner._count; i++)
            {
                int index = (_owner._head - _owner._count + i + MaxBars) % MaxBars;
                int sample = _owner._bars[index];
                float height = sample < 0 ? 2.5f : Math.Max(3, sample / (float)max * 31f);
                float x = 60 + i * step;
                RectangleF bar = new(x, baseline - height, 1.2f, height);
                using var brush = new LinearGradientBrush(bar,
                    sample < 0 ? Color.FromArgb(255, 89, 116) : Cyan,
                    sample < 0 ? Color.FromArgb(211, 55, 91) : Blue, 90f);
                g.FillRectangle(brush, bar);
            }

            if (_owner._usage.TotalBytes >= NetworkUsageTracker.DailyAlertThresholdBytes)
                DrawWarning(g, chart.Left + chart.Width / 2f, chart.Top + chart.Height / 2f,
                    18 + _owner._warningPulseStep, _owner._usage.TotalBytes);
        }

        private static void DrawWarning(Graphics g, float centerX, float centerY, float size, long totalBytes)
        {
            Color warning = totalBytes >= 10_000_000_000L
                ? Color.FromArgb(232, 54, 73)
                : Color.FromArgb(35, 91, 205);
            PointF[] triangle =
            {
                new(centerX, centerY - size * .58f),
                new(centerX + size * .56f, centerY + size * .44f),
                new(centerX - size * .56f, centerY + size * .44f)
            };
            using var halo = new SolidBrush(Color.FromArgb(60, warning));
            using var fill = new SolidBrush(warning);
            using var border = new Pen(Color.FromArgb(220, 155, 221, 255), 1.2f);
            g.FillEllipse(halo, centerX - size * .72f, centerY - size * .72f, size * 1.44f, size * 1.44f);
            g.FillPolygon(fill, triangle);
            g.DrawPolygon(border, triangle);
            using Font markFont = new("Segoe UI", Math.Max(9f, size * .57f), FontStyle.Bold);
            using var markBrush = new SolidBrush(Color.White);
            DrawCentered(g, "!", markFont, markBrush,
                new RectangleF(centerX - size * .18f, centerY - size * .24f, size * .36f, size * .58f));
        }

        private void DrawFooter(Graphics g)
        {
            bool online = _owner.CurrentPing >= 0;
            Color stateColor = online ? Green : Color.FromArgb(255, 86, 111);
            using var stateBrush = new SolidBrush(stateColor);
            g.FillEllipse(stateBrush, 8, 73, 6, 6);
            using Font stateFont = new("Segoe UI", 7f, FontStyle.Bold);
            g.DrawString(online ? "ONLINE" : "TIMEOUT", stateFont, stateBrush, 17, 70);

            using Font dataFont = new("Segoe UI", 8.2f, FontStyle.Bold);
            using var downloadBrush = new SolidBrush(Color.FromArgb(78, 206, 255));
            using var uploadBrush = new SolidBrush(Color.FromArgb(190, 135, 255));
            g.DrawString("↓" + FormatCompactBytes(_owner._usage.ReceivedBytes), dataFont, downloadBrush, 67, 70);
            g.DrawString("↑" + FormatCompactBytes(_owner._usage.SentBytes), dataFont, uploadBrush, 119, 70);
        }

        private static void DrawGear(Graphics g, float x, float y, float size)
        {
            float cx = x + size / 2, cy = y + size / 2;
            using var brush = new SolidBrush(Color.FromArgb(185, 216, 255));
            for (int i = 0; i < 8; i++)
            {
                g.TranslateTransform(cx, cy);
                g.RotateTransform(i * 45);
                g.FillRectangle(brush, -1.1f, -size / 2, 2.2f, 3.6f);
                g.ResetTransform();
            }
            g.FillEllipse(brush, cx - size * .36f, cy - size * .36f, size * .72f, size * .72f);
            using var hole = new SolidBrush(Color.FromArgb(5, 19, 45));
            g.FillEllipse(hole, cx - size * .13f, cy - size * .13f, size * .26f, size * .26f);
        }

        private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF bounds)
        {
            SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, bounds.Left + (bounds.Width - size.Width) / 2, bounds.Top + (bounds.Height - size.Height) / 2);
        }

    }
    private static Icon MakeIcon()
    {
        using Bitmap bitmap = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using Pen pen = new Pen(Color.FromArgb(80, 200, 255), 1.6f);
            graphics.DrawLine(pen, 2, 12, 5, 6);
            graphics.DrawLine(pen, 5, 6, 8, 9);
            graphics.DrawLine(pen, 8, 9, 11, 3);
            graphics.DrawLine(pen, 11, 3, 14, 8);
        }
        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using Icon borrowedIcon = Icon.FromHandle(iconHandle);
            return (Icon)borrowedIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Small input dialog in the same dark style as the widget
    private class SetHostDialog : Form
    {
        private readonly TextBox _txt;
        public string HostValue => _txt.Text;

        public SetHostDialog(string current)
        {
            Text = "Set Host";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(320, 122);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            BackColor = Color.FromArgb(7, 15, 32);
            Font = new Font("Segoe UI", 9f);

            var lbl = new Label
            {
                Text = "IP or domain (e.g. google.com, 8.8.8.8):",
                Location = new Point(14, 12),
                Size = new Size(292, 20),
                ForeColor = Color.FromArgb(220, 232, 250),
                BackColor = Color.FromArgb(7, 15, 32)
            };
            _txt = new TextBox
            {
                Text = current,
                Location = new Point(14, 36),
                Size = new Size(292, 26),
                Font = new Font("Segoe UI", 10f),
                BackColor = Color.FromArgb(14, 26, 51),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(142, 76),
                Size = new Size(84, 28),
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(0, 126, 255),
                ForeColor = Color.White
            };
            ok.FlatAppearance.BorderSize = 1;
            ok.FlatAppearance.BorderColor = Color.FromArgb(0, 126, 255);
            ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 148, 255);
            ok.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 94, 202);
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(232, 76),
                Size = new Size(84, 28),
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(22, 39, 70),
                ForeColor = Color.White
            };
            cancel.FlatAppearance.BorderSize = 1;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(60, 88, 137);
            cancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 53, 91);
            cancel.FlatAppearance.MouseDownBackColor = Color.FromArgb(15, 29, 54);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(lbl);
            Controls.Add(_txt);
            Controls.Add(ok);
            Controls.Add(cancel);
            _txt.SelectAll();
        }
    }
}
