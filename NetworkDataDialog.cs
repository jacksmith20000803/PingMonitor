using System.Drawing;
using System.Windows.Forms;

internal sealed class NetworkDataDialog : Form
{
    private readonly NetworkUsageTracker _usage;
    private readonly DateTimePicker _datePicker;
    private readonly Label _cycleLabel;
    private readonly Label _totalLabel;
    private readonly Label _downloadLabel;
    private readonly Label _uploadLabel;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public NetworkDataDialog(NetworkUsageTracker usage)
    {
        _usage = usage;
        Text = "Network Data";
        ClientSize = new Size(500, 285);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(28, 30, 36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 10f);

        var title = new Label
        {
            Text = "Network data usage",
            Location = new Point(24, 18),
            Size = new Size(300, 32),
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.White
        };
        var dateLabel = new Label
        {
            Text = "Cycle date:",
            Location = new Point(24, 63),
            Size = new Size(90, 26),
            TextAlign = ContentAlignment.MiddleLeft
        };
        DateTime latestDate = _usage.CurrentCycleDate.ToDateTime(TimeOnly.MinValue);
        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            MinDate = _usage.EarliestRecordedDate.ToDateTime(TimeOnly.MinValue),
            MaxDate = latestDate,
            Value = latestDate,
            Location = new Point(116, 62),
            Size = new Size(150, 28)
        };
        _datePicker.ValueChanged += delegate { RefreshUsage(); };

        _cycleLabel = new Label
        {
            Location = new Point(24, 99),
            Size = new Size(452, 24),
            ForeColor = Color.Silver
        };
        _totalLabel = new Label
        {
            Location = new Point(24, 127),
            Size = new Size(452, 44),
            Font = new Font("Segoe UI", 23f, FontStyle.Bold),
            ForeColor = Color.FromArgb(80, 200, 255)
        };
        _downloadLabel = new Label
        {
            Location = new Point(24, 185),
            Size = new Size(215, 54),
            BackColor = Color.FromArgb(40, 44, 52),
            ForeColor = Color.FromArgb(130, 205, 255),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold)
        };
        _uploadLabel = new Label
        {
            Location = new Point(261, 185),
            Size = new Size(215, 54),
            BackColor = Color.FromArgb(40, 44, 52),
            ForeColor = Color.FromArgb(170, 210, 140),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold)
        };
        var note = new Label
        {
            Text = $"Each date shows one reset cycle. Cycles reset at {_usage.ResetTime.ToString(@"hh\:mm")} local time.",
            Location = new Point(24, 250),
            Size = new Size(452, 24),
            ForeColor = Color.Silver,
            Font = new Font("Segoe UI", 8.5f)
        };

        Controls.Add(title);
        Controls.Add(dateLabel);
        Controls.Add(_datePicker);
        Controls.Add(_cycleLabel);
        Controls.Add(_totalLabel);
        Controls.Add(_downloadLabel);
        Controls.Add(_uploadLabel);
        Controls.Add(note);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += delegate
        {
            _usage.Sample();
            RefreshUsage();
        };
        _refreshTimer.Start();
        RefreshUsage();
    }

    private void RefreshUsage()
    {
        DateOnly selectedDate = DateOnly.FromDateTime(_datePicker.Value);
        (long received, long sent) = _usage.GetUsageForDate(selectedDate);
        _cycleLabel.Text = selectedDate == _usage.CurrentCycleDate
            ? "Current reset cycle"
            : "Completed reset cycle";
        _totalLabel.Text = $"Total  {FormatBytes(SaturatingAdd(received, sent))}";
        _downloadLabel.Text = $"DOWNLOAD\n{FormatBytes(received)}";
        _uploadLabel.Text = $"UPLOAD\n{FormatBytes(sent)}";
    }

    private static string FormatBytes(long bytes)
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

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        base.OnFormClosed(e);
    }
}
