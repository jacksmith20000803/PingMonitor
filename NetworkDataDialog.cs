using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

internal sealed class NetworkDataDialog : Form
{
    private readonly NetworkUsageTracker _usage;
    private readonly Func<int[]> _pingHistoryProvider;
    private readonly DashboardSurface _surface;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _warningPulseTimer;
    private DateOnly _selectedDate;
    private DateOnly _calendarMonth;
    private DateOnly _earliestDate;
    private DateOnly _latestDate;
    private long _receivedBytes;
    private long _sentBytes;
    private long _totalBytes;
    private int[] _pingSamples = Array.Empty<int>();
    private int[] _validPings = Array.Empty<int>();
    private int _latestPing = -1;
    private string _captureStatus = "";
    private TimeSpan _resetTime;
    private bool _dragging;
    private bool _calendarOpen;
    private int _warningPulseStep;
    private int _warningPulseDirection = 1;
    private Point _dragOriginCursor;
    private Point _dragOriginWindow;

    private DateOnly SelectedDate => _selectedDate;
    private DateOnly CalendarMonth => _calendarMonth;
    private DateOnly EarliestDate => _earliestDate;
    private DateOnly LatestDate => _latestDate;

    public NetworkDataDialog(NetworkUsageTracker usage, Func<int[]> pingHistoryProvider)
    {
        _usage = usage;
        _pingHistoryProvider = pingHistoryProvider;
        Text = "Network Data";
        ClientSize = new Size(760, 524);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(3, 10, 27);

        _selectedDate = _usage.CurrentCycleDate;
        _calendarMonth = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);
        _earliestDate = _usage.EarliestRecordedDate;
        _latestDate = _usage.CurrentCycleDate;
        _surface = new DashboardSurface(this) { Dock = DockStyle.Fill };
        _surface.MouseDown += SurfaceMouseDown;
        _surface.MouseMove += SurfaceMouseMove;
        _surface.MouseUp += delegate { _dragging = false; };
        _surface.MouseLeave += delegate { _surface.Cursor = Cursors.Default; };
        _surface.MouseClick += SurfaceMouseClick;
        Controls.Add(_surface);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += delegate
        {
            _usage.Sample();
            RefreshUsage();
        };
        _refreshTimer.Start();
        _warningPulseTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _warningPulseTimer.Tick += delegate
        {
            if (_totalBytes >= NetworkUsageTracker.DailyAlertThresholdBytes)
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
        RefreshUsage();
    }

    private void SurfaceMouseDown(object sender, MouseEventArgs e)
    {
        Point designPoint = _surface.ToDesignPoint(e.Location);
        if (e.Button == MouseButtons.Left && designPoint.Y < 96 && !DashboardSurface.CloseBounds.Contains(designPoint))
        {
            _dragging = true;
            _dragOriginCursor = Cursor.Position;
            _dragOriginWindow = Location;
        }
    }

    private void SurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            UpdateCursor(Cursor.Position);
            return;
        }
        Point cursor = Cursor.Position;
        Location = new Point(_dragOriginWindow.X + cursor.X - _dragOriginCursor.X,
            _dragOriginWindow.Y + cursor.Y - _dragOriginCursor.Y);
    }

    private void UpdateCursor(Point screenPosition)
    {
        Point local = _surface.PointToClient(screenPosition);
        Point design = _surface.ToDesignPoint(local);
        _surface.Cursor = DashboardSurface.CloseBounds.Contains(design) ||
            DashboardSurface.DateSelectorBounds.Contains(design) ||
            (_calendarOpen && _surface.IsCalendarInteractivePoint(design)) ? Cursors.Hand : Cursors.Default;
    }

    private void SurfaceMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Point designPoint = _surface.ToDesignPoint(e.Location);
        if (DashboardSurface.CloseBounds.Contains(designPoint))
        {
            Close();
            return;
        }
        if (DashboardSurface.DateSelectorBounds.Contains(designPoint))
        {
            _calendarOpen = !_calendarOpen;
            _calendarMonth = new DateOnly(_selectedDate.Year, _selectedDate.Month, 1);
            _surface.Invalidate();
            return;
        }
        if (_calendarOpen && HandleCalendarClick(designPoint)) return;
        if (_calendarOpen)
        {
            _calendarOpen = false;
            _surface.Invalidate();
        }
    }

    private bool HandleCalendarClick(Point point)
    {
        if (DashboardSurface.PreviousMonthBounds.Contains(point))
        {
            if (_calendarMonth.Year > 1)
                _calendarMonth = _calendarMonth.AddMonths(-1);
            _surface.Invalidate();
            return true;
        }
        if (DashboardSurface.NextMonthBounds.Contains(point))
        {
            DateOnly next = _calendarMonth.AddMonths(1);
            if (next <= _latestDate)
                _calendarMonth = next;
            _surface.Invalidate();
            return true;
        }

        if (!DashboardSurface.CalendarPopupBounds.Contains(point)) return false;
        if (!_surface.TryGetCalendarDate(point, out DateOnly date)) return true;
        if (date < _earliestDate || date > _latestDate || !_usage.HasUsageForDate(date)) return true;

        _selectedDate = date;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        _calendarOpen = false;
        RefreshUsage();
        return true;
    }

    private void RefreshUsage()
    {
        _latestDate = _usage.CurrentCycleDate;
        _earliestDate = _usage.EarliestRecordedDate;
        if (_selectedDate > _latestDate) _selectedDate = _latestDate;
        if (_selectedDate < _earliestDate) _selectedDate = _earliestDate;

        (_receivedBytes, _sentBytes) = _usage.GetUsageForDate(_selectedDate);
        _totalBytes = SaturatingAdd(_receivedBytes, _sentBytes);
        _captureStatus = _usage.CaptureStatus;
        _resetTime = _usage.ResetTime;
        _pingSamples = _selectedDate == _usage.CurrentCycleDate
            ? _pingHistoryProvider?.Invoke() ?? Array.Empty<int>()
            : _usage.GetPingSamplesForDate(_selectedDate);
        _validPings = _pingSamples.Where(value => value >= 0).ToArray();
        _latestPing = _pingSamples.Length == 0 ? -1 : _pingSamples[^1];
        _surface.Invalidate();
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _warningPulseTimer.Stop();
        _warningPulseTimer.Dispose();
        base.OnFormClosed(e);
    }

    private sealed class DashboardSurface : Control
    {
        private readonly NetworkDataDialog _owner;
        private static readonly Color Cyan = Color.FromArgb(66, 224, 255);
        private static readonly Color Blue = Color.FromArgb(0, 139, 255);
        private static readonly Color Green = Color.FromArgb(45, 239, 130);
        private static readonly Color Tangerine = Color.FromArgb(255, 140, 26);
        private static readonly Color AlertRed = Color.FromArgb(241, 70, 85);
        private static readonly Color Violet = Color.FromArgb(145, 67, 255);
        public static readonly Rectangle CloseBounds = new(838, 12, 48, 54);
        public static readonly Rectangle DateSelectorBounds = new(618, 114, 230, 58);
        public static readonly Rectangle CalendarPopupBounds = new(603, 166, 275, 266);
        public static readonly Rectangle PreviousMonthBounds = new(609, 172, 30, 30);
        public static readonly Rectangle NextMonthBounds = new(842, 172, 30, 30);

        public DashboardSurface(NetworkDataDialog owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public Point ToDesignPoint(Point point) => new(
            (int)Math.Round(point.X * 900f / Math.Max(1, Width)),
            (int)Math.Round(point.Y * 620f / Math.Max(1, Height)));

        public Point ToClientLocation(Point point) => new(
            (int)Math.Round(point.X * Width / 900f),
            (int)Math.Round(point.Y * Height / 620f));

        public bool IsCalendarInteractivePoint(Point point)
        {
            if (PreviousMonthBounds.Contains(point)) return _owner.CalendarMonth.Year > 1;
            if (NextMonthBounds.Contains(point))
                return _owner.CalendarMonth < new DateOnly(_owner.LatestDate.Year, _owner.LatestDate.Month, 1);
            return TryGetCalendarDate(point, out DateOnly date) &&
                date >= _owner.EarliestDate && date <= _owner.LatestDate &&
                _owner._usage.HasUsageForDate(date);
        }

        public bool TryGetCalendarDate(Point point, out DateOnly date)
        {
            date = default;
            const int gridLeft = 618;
            const int gridTop = 242;
            const int cellWidth = 35;
            const int cellHeight = 31;
            if (point.X < gridLeft || point.X >= gridLeft + cellWidth * 7 ||
                point.Y < gridTop || point.Y >= gridTop + cellHeight * 6)
                return false;
            int column = (point.X - gridLeft) / cellWidth;
            int row = (point.Y - gridTop) / cellHeight;
            if (column < 0 || column >= 7 || row < 0 || row >= 6)
                return false;
            int offset = (int)_owner.CalendarMonth.DayOfWeek;
            int day = row * 7 + column - offset + 1;
            int daysInMonth = DateTime.DaysInMonth(_owner.CalendarMonth.Year, _owner.CalendarMonth.Month);
            if (day < 1 || day > daysInMonth) return false;
            date = new DateOnly(_owner.CalendarMonth.Year, _owner.CalendarMonth.Month, day);
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(Width / 900f, Height / 620f);
            DrawBackground(g);
            DrawHeader(g);
            DrawUsageSummary(g);
            DrawPingChart(g);
            DrawTrafficCards(g);
            DrawFooter(g);
            if (_owner._calendarOpen) DrawCalendarPopup(g);
        }

        private void DrawBackground(Graphics g)
        {
            Rectangle bounds = new(0, 0, 900, 620);
            using var background = new LinearGradientBrush(bounds, Color.FromArgb(9, 18, 39), Color.FromArgb(4, 10, 25), 90f);
            g.FillRectangle(background, bounds);
            using var panel = new LinearGradientBrush(bounds, Color.FromArgb(12, 22, 43), Color.FromArgb(7, 15, 32), 90f);
            g.FillRectangle(panel, bounds);
            using Pen divider = new(Color.FromArgb(32, 104, 148, 221), 1f);
            g.DrawLine(divider, 40, 91, 860, 91);
            g.DrawLine(divider, 40, 565, 860, 565);
        }

        private void DrawHeader(Graphics g)
        {
            DrawWifiMark(g, 70, 36);
            using Font pingFont = new("Segoe UI", 32, FontStyle.Bold, GraphicsUnit.Pixel);
            using Font monitorFont = new("Segoe UI", 32, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("Ping", pingFont, Brushes.White, 127, 27);
            using var cyanBrush = new SolidBrush(Cyan);
            g.DrawString("Monitor", monitorFont, cyanBrush, 208, 27);

            using Pen closePen = new(Color.FromArgb(176, 207, 251), 3.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(closePen, 856, 33, 870, 47);
            g.DrawLine(closePen, 870, 33, 856, 47);
        }

        private void DrawUsageSummary(Graphics g)
        {
            const float cx = 137, cy = 181, radius = 70;
            RectangleF ring = new(cx - radius, cy - radius, radius * 2, radius * 2);
            Color usageColor = _owner._totalBytes >= 10_000_000_000L
                ? AlertRed
                : _owner._totalBytes >= NetworkUsageTracker.DailyAlertThresholdBytes
                    ? Tangerine
                    : Green;
            using Pen track = new(Color.FromArgb(40, 89, 148, 125), 10f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using Pen halo = new(Color.FromArgb(28, usageColor), 19f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using Pen arc = new(usageColor, 8f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(track, ring, 135, 270);
            g.DrawArc(halo, ring, 135, 270);
            g.DrawArc(arc, ring, 135, 270);

            using Font totalFont = new(_owner._totalBytes >= 1_000_000_000 ? "Segoe UI" : "Segoe UI", 35, FontStyle.Bold, GraphicsUnit.Pixel);
            using Font totalCaptionFont = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.FromArgb(242, 248, 255));
            using var muted = new SolidBrush(Color.FromArgb(159, 190, 231));
            DrawCentered(g, FormatCompactBytes(_owner._totalBytes), totalFont, white, new RectangleF(73, 151, 128, 48));
            DrawCentered(g, "DAILY TOTAL", totalCaptionFont, muted, new RectangleF(79, 196, 116, 24));

            bool online = _owner._latestPing >= 0;
            Color statusColor = online ? Green : Color.FromArgb(255, 92, 111);
            using var statusBrush = new SolidBrush(statusColor);
            g.FillEllipse(statusBrush, 264, 153, 15, 15);
            using Font statusFont = new("Segoe UI", 31, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString(online ? "Online" : "Offline", statusFont, statusBrush, 291, 140);
            using Font detailFont = new("Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
            using var detailBrush = new SolidBrush(Color.FromArgb(166, 190, 228));
            g.DrawString(online ? "Stable connection" : "Waiting for connection", detailFont, detailBrush, 292, 180);

            RectangleF datePill = DateSelectorBounds;
            using GraphicsPath datePath = RoundRect(datePill, 23);
            using var dateFill = new LinearGradientBrush(datePill, Color.FromArgb(28, 45, 81), Color.FromArgb(17, 29, 58), 90f);
            g.FillPath(dateFill, datePath);
            using Pen dateBorder = new(Color.FromArgb(36, 89, 160, 255), 1.1f);
            g.DrawPath(dateBorder, datePath);
            DrawCalendarIcon(g, 637, 129);
            using Font dateFont = new("Segoe UI", 21, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(_owner.SelectedDate.ToString("M/d/yyyy"), dateFont, Brushes.White, 677, 128);
            using Pen chevron = new(Color.FromArgb(170, 207, 255), 3f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(chevron, 819, 137, 829, 147);
            g.DrawLine(chevron, 829, 147, 839, 137);

        }

        private void DrawCalendarPopup(Graphics g)
        {
            RectangleF popup = CalendarPopupBounds;
            using GraphicsPath path = RoundRect(popup, 12);
            using var fill = new LinearGradientBrush(popup, Color.FromArgb(19, 34, 62), Color.FromArgb(11, 23, 45), 90f);
            g.FillPath(fill, path);
            using Pen border = new(Color.FromArgb(87, 82, 137, 210), 1f);
            g.DrawPath(border, path);

            bool canPrevious = _owner.CalendarMonth.Year > 1;
            bool canNext = _owner.CalendarMonth < new DateOnly(_owner.LatestDate.Year, _owner.LatestDate.Month, 1);
            DrawCalendarArrow(g, PreviousMonthBounds, false, canPrevious);
            DrawCalendarArrow(g, NextMonthBounds, true, canNext);
            using Font monthFont = new("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var primary = new SolidBrush(Color.FromArgb(232, 242, 255));
            DrawCentered(g, _owner.CalendarMonth.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy"),
                monthFont, primary, new RectangleF(640, 173, 204, 28));

            string[] weekdays = { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
            using Font weekdayFont = new("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            using var muted = new SolidBrush(Color.FromArgb(142, 169, 207));
            for (int column = 0; column < 7; column++)
                DrawCentered(g, weekdays[column], weekdayFont, muted,
                    new RectangleF(618 + column * 35, 207, 35, 23));

            using Font dayFont = new("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            int offset = (int)_owner.CalendarMonth.DayOfWeek;
            int daysInMonth = DateTime.DaysInMonth(_owner.CalendarMonth.Year, _owner.CalendarMonth.Month);
            for (int day = 1; day <= daysInMonth; day++)
            {
                int index = offset + day - 1;
                int column = index % 7;
                int row = index / 7;
                RectangleF cell = new(618 + column * 35, 242 + row * 31, 35, 31);
                DateOnly date = new(_owner.CalendarMonth.Year, _owner.CalendarMonth.Month, day);
                bool available = date >= _owner.EarliestDate && date <= _owner.LatestDate && _owner._usage.HasUsageForDate(date);
                Color marker = Color.FromArgb(34, 222, 128);
                if (available)
                {
                    (long received, long sent) = _owner._usage.GetUsageForDate(date);
                    long total = SaturatingAdd(received, sent);
                    marker = total >= 10_000_000_000L
                        ? AlertRed
                        : total >= NetworkUsageTracker.DailyAlertThresholdBytes
                            ? Tangerine
                            : Color.FromArgb(34, 222, 128);
                    float diameter = 26f;
                    RectangleF circle = new(cell.X + (cell.Width - diameter) / 2f,
                        cell.Y + (cell.Height - diameter) / 2f, diameter, diameter);
                    using var circleFill = new SolidBrush(marker);
                    using var circleOutline = new Pen(date == _owner._selectedDate
                        ? Color.FromArgb(245, 245, 250, 255) : Color.FromArgb(240, marker),
                        date == _owner._selectedDate ? 2f : 1.4f);
                    g.FillEllipse(circleFill, circle);
                    g.DrawEllipse(circleOutline, circle);
                }

                using var dayBrush = new SolidBrush(available
                    ? date == _owner._selectedDate ? Color.White : Color.FromArgb(215, 230, 250)
                    : Color.FromArgb(75, 96, 126));
                DrawCentered(g, day.ToString(), dayFont, dayBrush,
                    new RectangleF(cell.X, cell.Y + 2, cell.Width, cell.Height - 4));
            }
        }

        private static void DrawCalendarArrow(Graphics g, Rectangle bounds, bool next, bool enabled)
        {
            Color color = enabled ? Color.FromArgb(151, 190, 242) : Color.FromArgb(63, 82, 112);
            using var brush = new SolidBrush(color);
            float x = bounds.X + bounds.Width / 2f;
            float y = bounds.Y + bounds.Height / 2f;
            PointF[] points = next
                ? new[] { new PointF(x - 3, y - 6), new PointF(x + 3, y), new PointF(x - 3, y + 6) }
                : new[] { new PointF(x + 3, y - 6), new PointF(x - 3, y), new PointF(x + 3, y + 6) };
            g.FillPolygon(brush, points);
        }

        private void DrawPingChart(Graphics g)
        {
            RectangleF chart = new(40, 277, 820, 151);
            using GraphicsPath path = RoundRect(chart, 18);
            using var fill = new LinearGradientBrush(chart, Color.FromArgb(11, 26, 51), Color.FromArgb(6, 17, 37), 90f);
            g.FillPath(fill, path);
            using Pen border = new(Color.FromArgb(47, 53, 106, 186), 1f);
            g.DrawPath(border, path);

            using Font titleFont = new("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var titleBrush = new SolidBrush(Cyan);
            DrawPingIcon(g, 59, 291);
            g.DrawString("Ping History", titleFont, Brushes.White, 101, 290);
            DrawPingStatistics(g);

            using Pen divider = new(Color.FromArgb(31, 95, 144, 211), 1f);
            g.DrawLine(divider, 52, 324, 848, 324);
            DrawPingBars(g, chart);
            if (_owner._totalBytes >= NetworkUsageTracker.DailyAlertThresholdBytes)
            {
                float plotTop = 324f;
                float plotCenterY = plotTop + (chart.Bottom - plotTop) / 2f;
                DrawWarning(g, chart.Left + chart.Width / 2f, plotCenterY,
                    44 + _owner._warningPulseStep, _owner._totalBytes);
            }
        }

        private void DrawPingStatistics(Graphics g)
        {
            string low = _owner._validPings.Length == 0 ? "--" : _owner._validPings.Min().ToString();
            string average = _owner._validPings.Length == 0 ? "--" : Math.Round(_owner._validPings.Average()).ToString("0");
            string max = _owner._validPings.Length == 0 ? "--" : _owner._validPings.Max().ToString();
            using Font font = new("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var labelBrush = new SolidBrush(Color.FromArgb(159, 185, 225));
            using var lowBrush = new SolidBrush(Green);
            using var avgBrush = new SolidBrush(Cyan);
            using var maxBrush = new SolidBrush(Color.FromArgb(255, 194, 92));
            g.DrawString("Low", font, labelBrush, 520, 294);
            g.DrawString(low + " ms", font, lowBrush, 549, 294);
            using Pen separator = new(Color.FromArgb(71, 107, 150, 208), 1f);
            g.DrawLine(separator, 621, 292, 621, 314);
            g.DrawString("Avg", font, labelBrush, 638, 294);
            g.DrawString(average + " ms", font, avgBrush, 668, 294);
            g.DrawLine(separator, 747, 292, 747, 314);
            g.DrawString("Max", font, labelBrush, 761, 294);
            g.DrawString(max + " ms", font, maxBrush, 794, 294);
        }

        private void DrawPingBars(Graphics g, RectangleF chart)
        {
            float baseline = chart.Bottom - 3;
            const float left = 53f;
            const float width = 10f;
            const float step = 13.15f;
            int max = Math.Max(1, _owner._validPings.DefaultIfEmpty(1).Max());
            using var normalFill = new LinearGradientBrush(chart,
                Color.FromArgb(54, 218, 255), Color.FromArgb(0, 112, 239), 90f);
            using var timeoutFill = new LinearGradientBrush(chart,
                Color.FromArgb(255, 85, 110), Color.FromArgb(191, 51, 83), 90f);
            for (int i = 0; i < _owner._pingSamples.Length && i < 60; i++)
            {
                int ping = _owner._pingSamples[i];
                float barHeight = ping < 0 ? 4 : Math.Max(6, ping / (float)max * 81);
                RectangleF bar = new(left + i * step, baseline - barHeight, width, barHeight);
                FillRoundRect(g, ping < 0 ? timeoutFill : normalFill, bar, 4);
            }
        }

        private static void DrawWarning(Graphics g, float centerX, float centerY, float size, long totalBytes)
        {
            Color warning = totalBytes >= 10_000_000_000L
                ? AlertRed
                : Tangerine;
            PointF[] triangle =
            {
                new(centerX, centerY - size * .58f),
                new(centerX + size * .56f, centerY + size * .44f),
                new(centerX - size * .56f, centerY + size * .44f)
            };
            using var halo = new SolidBrush(Color.FromArgb(55, warning));
            using var fill = new SolidBrush(warning);
            using var border = new Pen(Color.FromArgb(235, 255, 207, 132), 1.3f);
            g.FillEllipse(halo, centerX - size * .72f, centerY - size * .72f, size * 1.44f, size * 1.44f);
            g.FillPolygon(fill, triangle);
            g.DrawPolygon(border, triangle);
            using Font markFont = new("Segoe UI", Math.Max(9f, size * .57f), FontStyle.Bold);
            using var markBrush = new SolidBrush(Color.White);
            DrawCentered(g, "!", markFont, markBrush,
                new RectangleF(centerX - size * .18f, centerY - size * .24f, size * .36f, size * .58f));
        }

        private void DrawTrafficCards(Graphics g)
        {
            DrawTrafficCard(g, new Rectangle(40, 451, 393, 98), "Download", _owner._receivedBytes, false);
            DrawTrafficCard(g, new Rectangle(457, 451, 403, 98), "Upload", _owner._sentBytes, true);
        }

        private static void DrawTrafficCard(Graphics g, Rectangle bounds, string title, long bytes, bool upload)
        {
            RectangleF card = bounds;
            using GraphicsPath path = RoundRect(card, 18);
            using var fill = new LinearGradientBrush(card, Color.FromArgb(14, 28, 55), Color.FromArgb(9, 18, 40), 90f);
            g.FillPath(fill, path);
            using Pen border = new(Color.FromArgb(35, 69, 113, 194), 1f);
            g.DrawPath(border, path);

            Color accent = upload ? Violet : Blue;
            using var iconFill = new SolidBrush(Color.FromArgb(42, accent));
            g.FillEllipse(iconFill, bounds.X + 18, bounds.Y + 18, 62, 62);
            using Pen arrow = new(upload ? Color.FromArgb(207, 152, 255) : Cyan, 5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float cx = bounds.X + 49;
            if (upload)
            {
                g.DrawLine(arrow, cx, bounds.Y + 65, cx, bounds.Y + 30);
                g.DrawLine(arrow, cx, bounds.Y + 30, cx - 13, bounds.Y + 43);
                g.DrawLine(arrow, cx, bounds.Y + 30, cx + 13, bounds.Y + 43);
            }
            else
            {
                g.DrawLine(arrow, cx, bounds.Y + 29, cx, bounds.Y + 64);
                g.DrawLine(arrow, cx, bounds.Y + 64, cx - 13, bounds.Y + 51);
                g.DrawLine(arrow, cx, bounds.Y + 64, cx + 13, bounds.Y + 51);
            }

            using Font titleFont = new("Segoe UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
            using Font valueFont = new("Segoe UI", 29, FontStyle.Bold, GraphicsUnit.Pixel);
            using var titleBrush = new SolidBrush(Color.FromArgb(169, 196, 238));
            g.DrawString(title, titleFont, titleBrush, bounds.X + 98, bounds.Y + 16);
            g.DrawString(FormatCompactBytes(bytes), valueFont, Brushes.White, bounds.X + 98, bounds.Y + 43);
        }

        private void DrawFooter(Graphics g)
        {
            Color statusColor = _owner._captureStatus.StartsWith("Capturing", StringComparison.OrdinalIgnoreCase)
                ? Green : Color.FromArgb(255, 192, 89);
            using var statusBrush = new SolidBrush(statusColor);
            g.FillEllipse(statusBrush, 48, 581, 13, 13);
            using Font font = new("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var muted = new SolidBrush(Color.FromArgb(168, 193, 231));
            string status = _owner._captureStatus;
            while (status.Length > 57 && TextWidth(g, status, font) > 525)
                status = status[..^1];
            if (status != _owner._captureStatus && status.Length > 3) status = status[..^1] + "…";
            g.DrawString(status, font, muted, 72, 578);

            g.DrawString($"Cycles reset at {_owner._resetTime:hh\\:mm} local time", font, muted, 616, 578);
        }

        private static float TextWidth(Graphics g, string text, Font font) => g.MeasureString(text, font).Width;

        private static void DrawWifiMark(Graphics g, float x, float y)
        {
            using Pen cyan = new(Color.FromArgb(0, 229, 255), 5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using Pen green = new(Color.FromArgb(0, 236, 137), 5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(cyan, x, y, 50, 34, 222, 96);
            g.DrawArc(cyan, x + 10, y + 10, 30, 22, 222, 96);
            g.DrawArc(green, x + 19, y + 20, 12, 9, 222, 96);
            using var statusDot = new SolidBrush(Color.FromArgb(0, 236, 137));
            g.FillEllipse(statusDot, x + 21.5f, y + 28.5f, 7f, 7f);
        }

        private static void DrawPingIcon(Graphics g, float x, float y)
        {
            using Pen pen = new(Cyan, 3f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            PointF[] points =
            {
                new(x, y + 12), new(x + 8, y + 12), new(x + 13, y + 3),
                new(x + 19, y + 22), new(x + 25, y + 7), new(x + 30, y + 12),
                new(x + 38, y + 12)
            };
            g.DrawLines(pen, points);
        }

        private static void DrawCalendarIcon(Graphics g, float x, float y)
        {
            using Pen pen = new(Color.FromArgb(112, 223, 255), 2.4f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using GraphicsPath path = RoundRect(new RectangleF(x, y + 2, 24, 23), 4);
            g.DrawPath(pen, path);
            g.DrawLine(pen, x + 1, y + 9, x + 23, y + 9);
            g.DrawLine(pen, x + 6, y, x + 6, y + 6);
            g.DrawLine(pen, x + 18, y, x + 18, y + 6);
        }

        private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF bounds)
        {
            SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, bounds.Left + (bounds.Width - size.Width) / 2,
                bounds.Top + (bounds.Height - size.Height) / 2);
        }

        private static void FillRoundRect(Graphics g, Brush brush, RectangleF bounds, float radius)
        {
            using GraphicsPath path = RoundRect(bounds, radius);
            g.FillPath(brush, path);
        }

        private static GraphicsPath RoundRect(RectangleF bounds, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
