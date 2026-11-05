using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new System.Threading.Mutex(
            initiallyOwned: true,
            name: @"Local\PingMonitor.SingleInstance",
            createdNew: out bool isFirstInstance);
        if (!isFirstInstance)
            return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string initial = args.Length > 0 ? args[0] : null;
        Application.Run(new MainForm(initial));
    }
}
