using EthernetWifiSwitcher.Core;
using EthernetWifiSwitcher.Ui;

namespace EthernetWifiSwitcher;

internal static class Program
{
    private const string MutexName = @"Local\EthernetWifiSwitcher.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Ethernet Wi-Fi Switcher is already running. Look for the coloured dot in the notification area.",
                "Already running",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        // Launched by the sign-in entry -> go straight to the tray without showing the window.
        bool startHidden = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        Application.Run(new TrayApplicationContext(startHidden));

        GC.KeepAlive(mutex);
    }

    private static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(
                AppPaths.LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  CRASH: {ex}{Environment.NewLine}");
        }
        catch
        {
            // Nothing else we can do at this point.
        }

        MessageBox.Show(
            $"Something went wrong:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "Ethernet Wi-Fi Switcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
