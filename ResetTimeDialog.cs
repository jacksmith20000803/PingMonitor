using System.Drawing;
using System.Windows.Forms;

internal sealed class ResetTimeDialog : Form
{
    private readonly DateTimePicker _timePicker;

    public TimeSpan ResetTime => _timePicker.Value.TimeOfDay;

    public ResetTimeDialog(TimeSpan currentResetTime)
    {
        Text = "Set Reset Time";
        ClientSize = new Size(360, 165);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(7, 15, 32);
        ForeColor = Color.FromArgb(220, 232, 250);
        Font = new Font("Segoe UI", 9f);

        var label = new Label
        {
            Text = "Reset network usage every day at (local time):",
            Location = new Point(18, 20),
            Size = new Size(324, 24)
        };
        _timePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm",
            ShowUpDown = true,
            Value = DateTime.Today.Add(currentResetTime),
            Location = new Point(18, 53),
            Size = new Size(90, 28)
        };
        var note = new Label
        {
            Text = "Applying a different time starts a fresh usage cycle immediately.",
            Location = new Point(18, 91),
            Size = new Size(324, 24),
            ForeColor = Color.FromArgb(145, 169, 205)
        };
        var apply = new Button
        {
            Text = "Apply",
            DialogResult = DialogResult.OK,
            Location = new Point(174, 125),
            Size = new Size(78, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 126, 255),
            ForeColor = Color.White
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(262, 125),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(22, 39, 70),
            ForeColor = Color.White
        };

        AcceptButton = apply;
        CancelButton = cancel;
        Controls.Add(label);
        Controls.Add(_timePicker);
        Controls.Add(note);
        Controls.Add(apply);
        Controls.Add(cancel);
    }
}
