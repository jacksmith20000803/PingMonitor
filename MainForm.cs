using System;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;

public class MainForm : Form
{
    private const int MaxBars = 60;

    private const string DefaultHost = "baidu.com";

    private const int W = 180;

    private const int H = 90;

    private const int HeaderH = 18;

    private const int ValueH = 24;

    private string _host;

    private readonly int[] _bars = new int[60];

    private int _count;

    private int _head;

    private bool _drag;

    private Point _dragPt;

    private readonly Panel _graph;

    private readonly Label _title;

    private readonly Label _close;

    private readonly Label _value;

    private readonly NotifyIcon _tray;

    private readonly System.Windows.Forms.Timer _timer;

    private static string HostFilePath => Path.Combine(AppContext.BaseDirectory, "pinghost.txt");

    public MainForm(string initialHost = null)
    {
        _host = NormalizeHost(initialHost);
        if (string.IsNullOrEmpty(_host))
            _host = LoadSavedHost();
        if (string.IsNullOrEmpty(_host))
            _host = DefaultHost;

        base.ClientSize = new Size(180, 90);
        base.FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(28, 30, 36);
        base.TopMost = true;
        base.ShowInTaskbar = false;
        base.StartPosition = FormStartPosition.Manual;
        base.Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 180 - 16, Screen.PrimaryScreen.WorkingArea.Top + 16);
        _title = new Label
        {
            Location = new Point(4, 0),
            Size = new Size(158, 18),
            Text = _host,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(40, 44, 52),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _close = new Label
        {
            Location = new Point(162, 0),
            Size = new Size(18, 18),
            Text = "×",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(40, 44, 52),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        _close.Click += delegate
        {
            Hide();
        };
        _close.MouseEnter += delegate
        {
            _close.BackColor = Color.FromArgb(200, 60, 60);
        };
        _close.MouseLeave += delegate
        {
            _close.BackColor = Color.FromArgb(40, 44, 52);
        };
        MouseEventHandler value = delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _drag = true;
                _dragPt = e.Location;
            }
        };
        MouseEventHandler value2 = delegate (object s, MouseEventArgs e)
        {
            if (_drag)
            {
                base.Left += e.X - _dragPt.X;
                base.Top += e.Y - _dragPt.Y;
            }
        };
        MouseEventHandler value3 = delegate
        {
            _drag = false;
        };
        _title.MouseDown += value;
        _title.MouseMove += value2;
        _title.MouseUp += value3;
        // NEW: double-click title to change host (original style untouched)
        _title.DoubleClick += delegate { ShowSetHostDialog(); };
        _graph = new Panel
        {
            Location = new Point(0, 18),
            Size = new Size(180, 48),
            BackColor = Color.FromArgb(18, 20, 24)
        };
        _graph.Paint += DrawBars;
        // NEW: right-click graph also opens host setting (does not alter look)
        _graph.MouseClick += delegate (object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) ShowSetHostDialog();
        };
        _value = new Label
        {
            Location = new Point(0, 66),
            Size = new Size(180, 24),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(80, 200, 255),
            BackColor = Color.FromArgb(28, 30, 36),
            Text = "-- ms"
        };
        base.Controls.Add(_title);
        base.Controls.Add(_close);
        base.Controls.Add(_graph);
        base.Controls.Add(_value);
        _tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Visible = true,
            Text = "Ping Monitor - " + _host
        };
        ContextMenuStrip contextMenuStrip = new ContextMenuStrip
        {
            Items =
            {
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
                base.Visible = !base.Visible;
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
                _title.Text = _host;
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
        int num;
        try
        {
            Ping ping = new Ping();
            try
            {
                PingReply pingReply = await ping.SendPingAsync(_host, 2000);
                num = (int)((pingReply.Status == IPStatus.Success) ? pingReply.RoundtripTime : (-1));
            }
            finally
            {
                ((IDisposable)ping)?.Dispose();
            }
        }
        catch
        {
            num = -1;
        }
        _bars[_head] = num;
        _head = (_head + 1) % 60;
        if (_count < 60)
        {
            _count++;
        }
        _value.Text = ((num < 0) ? "timeout" : (num + " ms"));
        _value.ForeColor = ((num < 0) ? Color.FromArgb(230, 90, 90) : Color.FromArgb(80, 200, 255));
        _graph.Invalidate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void DrawBars(object sender, PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(_graph.BackColor);
        if (_count == 0)
        {
            return;
        }
        int width = _graph.Width;
        int height = _graph.Height;
        int num = Math.Max(2, width / 60);
        int num2 = 1;
        int width2 = Math.Max(1, num - num2);
        int num3 = 1;
        for (int i = 0; i < _count; i++)
        {
            if (_bars[i] > num3)
            {
                num3 = _bars[i];
            }
        }
        for (int j = 0; j < _count; j++)
        {
            int num4 = (_head - _count + j + 60) % 60;
            int num5 = _bars[num4];
            int x = j * num;
            int num6 = ((num5 < 0) ? 2 : ((int)((float)num5 / (float)num3 * (float)(height - 2))));
            int y = height - num6;
            using SolidBrush brush = new SolidBrush((num5 < 0) ? Color.FromArgb(230, 90, 90) : Color.FromArgb(80, 180, 230));
            graphics.FillRectangle(brush, x, y, width2, num6);
        }
    }

    private static Icon MakeIcon()
    {
        Bitmap bitmap = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using Pen pen = new Pen(Color.FromArgb(80, 200, 255), 1.6f);
            graphics.DrawLine(pen, 2, 12, 5, 6);
            graphics.DrawLine(pen, 5, 6, 8, 9);
            graphics.DrawLine(pen, 8, 9, 11, 3);
            graphics.DrawLine(pen, 11, 3, 14, 8);
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    // Small input dialog in the same dark style as the widget
    private class SetHostDialog : Form
    {
        private readonly TextBox _txt;
        public string HostValue => _txt.Text;

        public SetHostDialog(string current)
        {
            Text = "Set Host";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(320, 122);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            BackColor = Color.FromArgb(32, 33, 36);
            Font = new Font("Segoe UI", 9f);

            var lbl = new Label
            {
                Text = "IP or domain (e.g. google.com, 8.8.8.8):",
                Location = new Point(14, 12),
                Size = new Size(292, 20),
                ForeColor = Color.FromArgb(220, 220, 220),
                BackColor = Color.FromArgb(32, 33, 36)
            };
            _txt = new TextBox
            {
                Text = current,
                Location = new Point(14, 36),
                Size = new Size(292, 26),
                Font = new Font("Segoe UI", 10f),
                BackColor = Color.FromArgb(18, 20, 24),
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
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White
            };
            ok.FlatAppearance.BorderSize = 1;
            ok.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
            ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 132, 235);
            ok.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 99, 177);
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
                BackColor = Color.FromArgb(62, 64, 68),
                ForeColor = Color.White
            };
            cancel.FlatAppearance.BorderSize = 1;
            cancel.FlatAppearance.BorderColor = Color.FromArgb(90, 93, 99);
            cancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(75, 78, 84);
            cancel.FlatAppearance.MouseDownBackColor = Color.FromArgb(50, 52, 56);
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
