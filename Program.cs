using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Program
{
    private const string LegacyUsageArgument = "--pingmonitor-legacy-usage-path";
    private const string StartupRegisteredArgument = "--pingmonitor-startup-registered";
    private const string WatchdogRestartArgument = "--pingmonitor-watchdog-restart";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "PingMonitor";
    private const string MutexName = @"Local\PingMonitor.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        string legacyUsagePath = null;
        bool startupAlreadyRegistered = false;
        bool watchdogRestart = false;
        var appArguments = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == LegacyUsageArgument)
            {
                if (i + 1 < args.Length) legacyUsagePath = args[++i];
            }
            else if (args[i] == StartupRegisteredArgument)
                startupAlreadyRegistered = true;
            else if (args[i] == WatchdogRestartArgument)
                watchdogRestart = true;
            else
                appArguments.Add(args[i]);
        }

        if (!startupAlreadyRegistered)
            RegisterStartupForCurrentUser();

        if (!IsAdministrator())
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                start.ArgumentList.Add(LegacyUsageArgument);
                start.ArgumentList.Add(legacyUsagePath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PingMonitor", "network-usage.json"));
                start.ArgumentList.Add(StartupRegisteredArgument);
                if (watchdogRestart) start.ArgumentList.Add(WatchdogRestartArgument);
                foreach (string argument in appArguments)
                    start.ArgumentList.Add(argument);
                Process.Start(start);
            }
            catch (Win32Exception)
            {
                MessageBox.Show(
                    "Administrator access is required for Npcap to capture network packets. PingMonitor did not start.",
                    "PingMonitor network capture",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return;
        }

        using var singleInstance = new System.Threading.Mutex(initiallyOwned: false, name: MutexName);
        bool ownsMutex = false;
        try
        {
            try { ownsMutex = singleInstance.WaitOne(watchdogRestart ? 15000 : 0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += HandleUiException;
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
            string initialHost = appArguments.Count > 0 ? appArguments[0] : null;
            Application.Run(new MainForm(initialHost, legacyUsagePath, watchdogRestart));
        }
        finally
        {
            if (ownsMutex) singleInstance.ReleaseMutex();
        }
    }

    private static void RegisterStartupForCurrentUser()
    {
        try
        {
            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return;
            string command = $"\"{executablePath}\"";
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);
            string existing = key?.GetValue(StartupValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (!string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
                key?.SetValue(StartupValueName, command, RegistryValueKind.String);
        }
        catch
        {
            // Startup registration must not prevent normal app startup.
        }
    }

    internal static bool StartWatchdogRestart()
    {
        try
        {
            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return false;
            var start = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            start.ArgumentList.Add(StartupRegisteredArgument);
            start.ArgumentList.Add(WatchdogRestartArgument);
            return Process.Start(start) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void HandleUiException(object sender, ThreadExceptionEventArgs e)
    {
        if (StartWatchdogRestart())
            Application.Exit();
        else
            MessageBox.Show("PingMonitor encountered an error and could not restart automatically.\n\n" + e.Exception.Message,
                "PingMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        StartWatchdogRestart();
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
