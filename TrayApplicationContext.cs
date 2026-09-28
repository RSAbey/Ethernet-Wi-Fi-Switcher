using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using EthernetWifiSwitcher.Core;

namespace EthernetWifiSwitcher.Ui;

/// <summary>
/// Keeps the app alive in the notification area. The window is secondary — closing it
/// only hides it, and Exit here is the single shutdown path.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly SwitchController _controller;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly Icon _ethernetIcon;
    private readonly Icon _wifiIcon;
    private readonly Icon _idleIcon;

    private bool _exiting;

    public TrayApplicationContext(bool startHidden)
    {
        AppPaths.EnsureCreated();

        _settings = AppSettings.Load();
        _controller = new SwitchController(_settings);

        _form = new MainForm(_controller, _settings);
        _ = _form.Handle;                       // force handle creation so BeginInvoke works while hidden

        _ethernetIcon = CreateDotIcon(Color.FromArgb(0, 120, 60));
        _wifiIcon = CreateDotIcon(Color.FromArgb(0, 110, 200));
        _idleIcon = CreateDotIcon(Color.FromArgb(130, 130, 130));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Capture current Wi-Fi", null, async (_, _) => await _controller.CaptureCurrentAsync());
        menu.Items.Add("Reconnect to Wi-Fi now", null, async (_, _) => await _controller.ReconnectNowAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Ethernet Wi-Fi Switcher",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        _controller.StatusChanged += OnStatusChanged;

        if (!startHidden) ShowWindow();

        // Fire and forget: startup work must not block the message loop.
        _ = _controller.StartAsync();
    }

    private void ShowWindow()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;

        _form.BringToFront();
        _form.Activate();
        _ = _form.RefreshStatusAsync();
    }

    private async void OnStatusChanged(object? sender, EventArgs e)
    {
        if (_exiting) return;

        try
        {
            StatusSnapshot snapshot = await _controller.GetSnapshotAsync();

            void Apply()
            {
                if (_exiting) return;

                _tray.Icon = snapshot.EthernetUp ? _ethernetIcon
                           : snapshot.CurrentSsid is not null ? _wifiIcon
                           : _idleIcon;

                string line2 = snapshot.EthernetUp
                    ? "Ethernet"
                    : snapshot.CurrentSsid ?? "Not connected";

                // NotifyIcon.Text is capped at 63 characters by Windows.
                string text = $"Ethernet Wi-Fi Switcher — {line2}";
                _tray.Text = text.Length > 63 ? text[..63] : text;
            }

            if (_form.IsHandleCreated && _form.InvokeRequired) _form.BeginInvoke(Apply);
            else Apply();
        }
        catch
        {
            // Tray decoration is cosmetic; never let it surface an error.
        }
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _tray.Visible = false;
        _controller.StatusChanged -= OnStatusChanged;

        // Puts any profile we forced to "manual" back to "auto" and re-enables a
        // disabled adapter, so quitting never leaves Wi-Fi in a broken state.
        _controller.Dispose();

        _form.Dispose();
        _tray.Dispose();
        _ethernetIcon.Dispose();
        _wifiIcon.Dispose();
        _idleIcon.Dispose();

        ExitThread();
    }

    /// <summary>Draws a small coloured dot at runtime, so no .ico asset has to ship.</summary>
    private static Icon CreateDotIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 4, 4, 24, 24);

            using var ring = new Pen(Color.FromArgb(220, 255, 255, 255), 2.5f);
            g.DrawEllipse(ring, 4, 4, 24, 24);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();      // clone so the icon survives DestroyIcon
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
