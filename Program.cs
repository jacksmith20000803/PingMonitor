using System;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string initial = args.Length > 0 ? args[0] : null;
        Application.Run(new MainForm(initial));
    }
}
