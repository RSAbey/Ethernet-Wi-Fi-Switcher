using EthernetWifiSwitcher.Core;

namespace EthernetWifiSwitcher.Ui;

/// <summary>
/// The app's only window. Built in code rather than with the designer so the whole
/// layout stays readable in one file.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly SwitchController _controller;
    private readonly AppSettings _settings;

    private readonly Label _ethernetValue = ValueLabel();
    private readonly Label _wifiValue = ValueLabel();
    private readonly Label _activityValue = ValueLabel();

    private readonly Label _savedSsidValue = ValueLabel();
    private readonly TextBox _passwordBox = new();
    private readonly CheckBox _showPassword = new();
    private readonly Button _copyPassword = new();
    private readonly Label _capturedAt = new();

    private readonly ComboBox _cableAction = new();
    private readonly NumericUpDown _reconnectDelay = new();
    private readonly CheckBox _capturePassword = new();
    private readonly CheckBox _runAtStartup = new();

    private readonly Button _captureNow = new();
    private readonly Button _reconnectNow = new();
    private readonly Button _clearSaved = new();

    private readonly TextBox _log = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private bool _loadingSettings;
    private bool _refreshInFlight;

    public MainForm(SwitchController controller, AppSettings settings)
    {
        _controller = controller;
        _settings = settings;

        BuildLayout();
        LoadSettingsIntoUi();

        _controller.Log += OnControllerLog;
        _controller.StatusChanged += OnControllerStatusChanged;

        _refreshTimer.Interval = 4000;
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _refreshTimer.Start();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        Text = "Ethernet Wi-Fi Switcher";
        ClientSize = new Size(660, 600);
        MinimumSize = new Size(676, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9F);

        // ---- Status -------------------------------------------------------
        var statusBox = new GroupBox
        {
            Text = "Current state",
            Location = new Point(12, 12),
            Size = new Size(636, 104),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        statusBox.Controls.Add(Caption("Ethernet:", 14, 26));
        statusBox.Controls.Add(Place(_ethernetValue, 110, 26, 510));
        statusBox.Controls.Add(Caption("Wi-Fi:", 14, 50));
        statusBox.Controls.Add(Place(_wifiValue, 110, 50, 510));
        statusBox.Controls.Add(Caption("Activity:", 14, 74));
        statusBox.Controls.Add(Place(_activityValue, 110, 74, 510));

        // ---- Saved network -------------------------------------------------
        var savedBox = new GroupBox
        {
            Text = "Saved network",
            Location = new Point(12, 124),
            Size = new Size(636, 110),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        savedBox.Controls.Add(Caption("SSID:", 14, 26));
        savedBox.Controls.Add(Place(_savedSsidValue, 110, 26, 510));

        savedBox.Controls.Add(Caption("Password:", 14, 52));
        _passwordBox.Location = new Point(110, 49);
        _passwordBox.Size = new Size(290, 23);
        _passwordBox.ReadOnly = true;
        _passwordBox.UseSystemPasswordChar = true;
        savedBox.Controls.Add(_passwordBox);

        _showPassword.Text = "Show";
        _showPassword.Location = new Point(410, 51);
        _showPassword.AutoSize = true;
        _showPassword.CheckedChanged += (_, _) => _passwordBox.UseSystemPasswordChar = !_showPassword.Checked;
        savedBox.Controls.Add(_showPassword);

        _copyPassword.Text = "Copy";
        _copyPassword.Location = new Point(482, 48);
        _copyPassword.Size = new Size(80, 25);
        _copyPassword.Click += OnCopyPassword;
        savedBox.Controls.Add(_copyPassword);

        _capturedAt.Location = new Point(110, 78);
        _capturedAt.Size = new Size(510, 18);
        _capturedAt.ForeColor = SystemColors.GrayText;
        savedBox.Controls.Add(_capturedAt);

        // ---- Behaviour -----------------------------------------------------
        var behaviourBox = new GroupBox
        {
            Text = "Behaviour",
            Location = new Point(12, 242),
            Size = new Size(636, 122),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        behaviourBox.Controls.Add(Caption("When cable is plugged in:", 14, 28, 160));
        _cableAction.Location = new Point(180, 25);
        _cableAction.Size = new Size(330, 23);
        _cableAction.DropDownStyle = ComboBoxStyle.DropDownList;
        _cableAction.Items.AddRange(new object[]
        {
            "Leave Wi-Fi on",
            "Disconnect Wi-Fi",
            "Switch the Wi-Fi adapter off (needs admin)"
        });
        _cableAction.SelectedIndexChanged += OnSettingChanged;
        behaviourBox.Controls.Add(_cableAction);

        behaviourBox.Controls.Add(Caption("Reconnect delay (seconds):", 14, 60, 160));
        _reconnectDelay.Location = new Point(180, 57);
        _reconnectDelay.Size = new Size(70, 23);
        _reconnectDelay.Minimum = 0;
        _reconnectDelay.Maximum = 120;
        _reconnectDelay.ValueChanged += OnSettingChanged;
        behaviourBox.Controls.Add(_reconnectDelay);

        _capturePassword.Text = "Also save the Wi-Fi password (needs admin)";
        _capturePassword.Location = new Point(280, 59);
        _capturePassword.AutoSize = true;
        _capturePassword.CheckedChanged += OnSettingChanged;
        behaviourBox.Controls.Add(_capturePassword);

        _runAtStartup.Text = "Start automatically when I sign in";
        _runAtStartup.Location = new Point(17, 90);
        _runAtStartup.AutoSize = true;
        _runAtStartup.CheckedChanged += OnStartupChanged;
        behaviourBox.Controls.Add(_runAtStartup);

        // ---- Buttons -------------------------------------------------------
        _captureNow.Text = "Capture current Wi-Fi";
        _captureNow.Location = new Point(12, 376);
        _captureNow.Size = new Size(160, 28);
        _captureNow.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _captureNow.Click += OnCaptureNow;

        _reconnectNow.Text = "Reconnect now";
        _reconnectNow.Location = new Point(180, 376);
        _reconnectNow.Size = new Size(140, 28);
        _reconnectNow.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _reconnectNow.Click += OnReconnectNow;

        _clearSaved.Text = "Forget saved network";
        _clearSaved.Location = new Point(328, 376);
        _clearSaved.Size = new Size(160, 28);
        _clearSaved.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _clearSaved.Click += OnClearSaved;

        // ---- Log -----------------------------------------------------------
        var logCaption = new Label
        {
            Text = "Activity",
            Location = new Point(14, 416),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _log.Location = new Point(12, 436);
        _log.Size = new Size(636, 150);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = SystemColors.Window;
        _log.Font = new Font("Consolas", 8.5F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        Controls.AddRange(new Control[]
        {
            statusBox, savedBox, behaviourBox,
            _captureNow, _reconnectNow, _clearSaved,
            logCaption, _log
        });
    }

    private static Label ValueLabel() => new() { AutoEllipsis = true };

    private static Label Caption(string text, int x, int y, int width = 96) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 18),
        ForeColor = SystemColors.GrayText
    };

    private static Label Place(Label label, int x, int y, int width)
    {
        label.Location = new Point(x, y);
        label.Size = new Size(width, 18);
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        return label;
    }

    // ------------------------------------------------------------- settings io

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        try
        {
            _cableAction.SelectedIndex = (int)_settings.OnCablePluggedIn;
            _reconnectDelay.Value = Math.Clamp(_settings.ReconnectDelaySeconds, 0, 120);
            _capturePassword.Checked = _settings.CapturePassword;
            _runAtStartup.Checked = StartupRegistration.IsEnabled();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void OnSettingChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings) return;

        _settings.OnCablePluggedIn = (CableAction)Math.Max(0, _cableAction.SelectedIndex);
        _settings.ReconnectDelaySeconds = (int)_reconnectDelay.Value;
        _settings.CapturePassword = _capturePassword.Checked;
        _settings.Save();

        if (_settings.OnCablePluggedIn == CableAction.DisableAdapter && !SwitchController.IsElevated)
            AppendLog("Note: switching the adapter off needs administrator rights. The app will disconnect Wi-Fi instead.");
    }

    private void OnStartupChanged(object? sender, EventArgs e)
    {
        if (_loadingSettings) return;

        if (!StartupRegistration.SetEnabled(_runAtStartup.Checked))
            AppendLog("Could not change the sign-in startup entry.");
    }

    // ------------------------------------------------------------------ events

    private async void OnCaptureNow(object? sender, EventArgs e)
    {
        _captureNow.Enabled = false;
        try
        {
            await _controller.CaptureCurrentAsync();
            await RefreshStatusAsync();
        }
        finally
        {
            _captureNow.Enabled = true;
        }
    }

    private async void OnReconnectNow(object? sender, EventArgs e)
    {
        _reconnectNow.Enabled = false;
        try
        {
            await _controller.ReconnectNowAsync();
            await RefreshStatusAsync();
        }
        finally
        {
            _reconnectNow.Enabled = true;
        }
    }

    private async void OnClearSaved(object? sender, EventArgs e)
    {
        DialogResult answer = MessageBox.Show(
            this,
            "Forget the saved network? The app will not reconnect automatically until it records a network again.",
            "Forget saved network",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes) return;

        SavedNetworkStore.Clear();
        AppendLog("Saved network cleared.");
        await RefreshStatusAsync();
    }

    private void OnCopyPassword(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_passwordBox.Text)) return;

        try
        {
            Clipboard.SetText(_passwordBox.Text);
            AppendLog("Password copied to the clipboard.");
        }
        catch
        {
            AppendLog("Could not access the clipboard.");
        }
    }

    private void OnControllerLog(object? sender, string line) => RunOnUiThread(() => AppendLog(line));

    private void OnControllerStatusChanged(object? sender, EventArgs e)
        => RunOnUiThread(() => _ = RefreshStatusAsync());

    // ------------------------------------------------------------------ status

    public async Task RefreshStatusAsync()
    {
        // Status changes can arrive in bursts and each snapshot spawns a netsh process,
        // so overlapping refreshes are dropped rather than queued.
        if (_refreshInFlight) return;
        _refreshInFlight = true;

        StatusSnapshot snapshot;
        try
        {
            snapshot = await _controller.GetSnapshotAsync();
        }
        catch
        {
            return;
        }
        finally
        {
            _refreshInFlight = false;
        }

        if (IsDisposed || !IsHandleCreated) return;

        RunOnUiThread(() =>
        {
            _ethernetValue.Text = snapshot.EthernetUp
                ? $"Connected  ({snapshot.EthernetAdapter})"
                : "Not connected";
            _ethernetValue.ForeColor = snapshot.EthernetUp ? Color.FromArgb(0, 120, 60) : SystemColors.ControlText;

            _wifiValue.Text = snapshot.CurrentSsid is null ? "Not connected" : snapshot.CurrentSsid;
            _wifiValue.ForeColor = snapshot.CurrentSsid is null ? SystemColors.ControlText : Color.FromArgb(0, 90, 158);

            _activityValue.Text = snapshot.Activity;

            SavedNetwork? saved = snapshot.Saved;
            if (saved is null)
            {
                _savedSsidValue.Text = "(nothing saved yet)";
                _passwordBox.Text = string.Empty;
                _capturedAt.Text = string.Empty;
                _copyPassword.Enabled = false;
                _showPassword.Enabled = false;
            }
            else
            {
                _savedSsidValue.Text = saved.Ssid;
                _passwordBox.Text = saved.Password ?? string.Empty;
                _capturedAt.Text = saved.HasPassword
                    ? $"Recorded {saved.CapturedUtc.ToLocalTime():dd MMM yyyy HH:mm}"
                    : $"Recorded {saved.CapturedUtc.ToLocalTime():dd MMM yyyy HH:mm} — password not captured";
                _copyPassword.Enabled = saved.HasPassword;
                _showPassword.Enabled = saved.HasPassword;
            }
        });
    }

    public void AppendLog(string line)
    {
        if (IsDisposed) return;

        // Keep the box from growing without bound over a long uptime.
        if (_log.Lines.Length > 500)
            _log.Lines = _log.Lines.Skip(250).ToArray();

        _log.AppendText(line + Environment.NewLine);
    }

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    // Closing the window hides it; the tray icon is the real exit point.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.Log -= OnControllerLog;
            _controller.StatusChanged -= OnControllerStatusChanged;
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
