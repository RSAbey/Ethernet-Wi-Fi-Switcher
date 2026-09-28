using System.Security.Principal;

namespace EthernetWifiSwitcher.Core;

internal sealed class StatusSnapshot
{
    public bool EthernetUp { get; init; }
    public string? EthernetAdapter { get; init; }
    public string? CurrentSsid { get; init; }
    public SavedNetwork? Saved { get; init; }
    public string Activity { get; init; } = "Idle";
}

/// <summary>
/// The actual behaviour of the app.
///
///   cable in   -> remember the Wi-Fi network you were on, then (optionally) take Wi-Fi down
///   cable out  -> put Wi-Fi back and re-join the remembered network
///
/// Every transition runs through one semaphore so a fast unplug/replug cannot interleave
/// two opposite sequences.
/// </summary>
internal sealed class SwitchController : IDisposable
{
    private readonly WifiManager _wifi = new();
    private readonly EthernetMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Profiles this app switched to manual, so they can be restored on exit.</summary>
    private readonly HashSet<string> _profilesSetToManual = new(StringComparer.Ordinal);

    private bool _adapterDisabledByUs;
    private string _activity = "Starting up";

    public event EventHandler<string>? Log;
    public event EventHandler? StatusChanged;

    public SwitchController(AppSettings settings)
    {
        _settings = settings;
        _monitor = new EthernetMonitor(settings.DebounceMs);
        _monitor.StateChanged += OnEthernetStateChanged;
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task StartAsync()
    {
        (bool isUp, string? adapter) = _monitor.ReadLinkState();
        WriteLog($"Started. Ethernet is currently {(isUp ? "connected" : "not connected")}.");

        if (!IsElevated && _settings.CapturePassword)
            WriteLog("Not running as administrator — the SSID will be saved but the password cannot be read.");

        if (!_settings.ActOnStartupState)
        {
            RaiseStatusChanged();
            return;
        }

        if (isUp)
        {
            await HandleCablePluggedInAsync(adapter).ConfigureAwait(false);
            return;
        }

        // No cable at launch. If Wi-Fi is already up, leave it alone — forcing the saved
        // network here would drag the user off whatever they deliberately joined.
        WifiStatus wifi = await _wifi.GetStatusAsync(_shutdown.Token).ConfigureAwait(false);
        if (wifi.IsConnected)
        {
            WriteLog($"Already on Wi-Fi \"{wifi.Ssid}\" — leaving it as is.");
            SetActivity($"On Wi-Fi: {wifi.Ssid}");
        }
        else
        {
            await HandleCableUnpluggedAsync(applyDelay: false).ConfigureAwait(false);
        }
    }

    private async void OnEthernetStateChanged(object? sender, EthernetChangedEventArgs e)
    {
        try
        {
            if (e.IsUp) await HandleCablePluggedInAsync(e.AdapterName).ConfigureAwait(false);
            else await HandleCableUnpluggedAsync(applyDelay: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            WriteLog($"Unexpected error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- cable in

    private async Task HandleCablePluggedInAsync(string? ethernetAdapter)
    {
        CancellationToken ct = _shutdown.Token;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetActivity("Cable connected");
            WriteLog($"Ethernet cable connected{(ethernetAdapter is null ? "" : $" ({ethernetAdapter})")}.");

            string? wifiAdapter = _wifi.GetAdapterName();
            if (wifiAdapter is null)
            {
                WriteLog("No wireless adapter found — nothing to do.");
                SetActivity("No Wi-Fi adapter");
                return;
            }

            WifiStatus status = await _wifi.GetStatusAsync(ct).ConfigureAwait(false);

            if (status.IsConnected)
            {
                await CaptureNetworkAsync(status, ct).ConfigureAwait(false);
            }
            else
            {
                WriteLog("Wi-Fi was not connected, so the previously saved network is kept.");
            }

            switch (_settings.OnCablePluggedIn)
            {
                case CableAction.LeaveWifiOn:
                    WriteLog("Leaving Wi-Fi on, as configured.");
                    SetActivity("Using Ethernet (Wi-Fi left on)");
                    break;

                case CableAction.DisconnectWifi:
                    await DisconnectWifiAsync(wifiAdapter, ct).ConfigureAwait(false);
                    SetActivity("Using Ethernet (Wi-Fi disconnected)");
                    break;

                case CableAction.DisableAdapter:
                    await DisableAdapterAsync(wifiAdapter, ct).ConfigureAwait(false);
                    SetActivity("Using Ethernet (Wi-Fi adapter off)");
                    break;
            }
        }
        finally
        {
            _lock.Release();
            RaiseStatusChanged();
        }
    }

    /// <summary>Records the SSID, profile name and — when elevated — the password.</summary>
    public async Task CaptureNetworkAsync(WifiStatus status, CancellationToken ct)
    {
        if (!status.IsConnected || status.ProfileName is null) return;

        var saved = new SavedNetwork
        {
            Ssid = status.Ssid!,
            ProfileName = status.ProfileName,
            CapturedUtc = DateTime.UtcNow
        };

        if (_settings.CapturePassword)
        {
            saved.Password = await _wifi.TryReadPasswordAsync(status.ProfileName, ct).ConfigureAwait(false);

            if (saved.Password is null)
            {
                WriteLog(IsElevated
                    ? $"Saved \"{saved.Ssid}\". No stored password (open network, or enterprise/802.1X login)."
                    : $"Saved \"{saved.Ssid}\". Password needs administrator rights to read.");
            }
            else
            {
                WriteLog($"Saved \"{saved.Ssid}\" with its password (encrypted on disk).");
            }
        }
        else
        {
            WriteLog($"Saved \"{saved.Ssid}\".");
        }

        try
        {
            SavedNetworkStore.Save(saved);
        }
        catch (Exception ex)
        {
            WriteLog($"Could not write the saved network: {ex.Message}");
        }
    }

    private async Task DisconnectWifiAsync(string wifiAdapter, CancellationToken ct)
    {
        SavedNetwork? saved = SavedNetworkStore.Load();

        // Windows re-joins an auto-connect profile within seconds of a disconnect, so the
        // profile is flipped to manual first. It is restored when the cable comes out.
        if (saved is not null && !string.IsNullOrWhiteSpace(saved.ProfileName))
        {
            ProcessResult r = await _wifi.SetAutoConnectAsync(saved.ProfileName, autoConnect: false, ct)
                .ConfigureAwait(false);
            if (r.Ok) _profilesSetToManual.Add(saved.ProfileName);
        }

        ProcessResult d = await _wifi.DisconnectAsync(wifiAdapter, ct).ConfigureAwait(false);
        WriteLog(d.Ok
            ? "Wi-Fi disconnected."
            : $"Could not disconnect Wi-Fi: {d.Text}");
    }

    private async Task DisableAdapterAsync(string wifiAdapter, CancellationToken ct)
    {
        if (!IsElevated)
        {
            WriteLog("Disabling the adapter needs administrator rights. Disconnecting instead.");
            await DisconnectWifiAsync(wifiAdapter, ct).ConfigureAwait(false);
            return;
        }

        ProcessResult r = await _wifi.SetAdapterEnabledAsync(wifiAdapter, enabled: false, ct).ConfigureAwait(false);
        if (r.Ok)
        {
            _adapterDisabledByUs = true;
            WriteLog($"Wireless adapter \"{wifiAdapter}\" disabled.");
        }
        else
        {
            WriteLog($"Could not disable the adapter: {r.Text}");
        }
    }

    // --------------------------------------------------------------- cable out

    private async Task HandleCableUnpluggedAsync(bool applyDelay)
    {
        CancellationToken ct = _shutdown.Token;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetActivity("Cable removed");
            WriteLog("Ethernet cable removed.");

            if (applyDelay && _settings.ReconnectDelay > TimeSpan.Zero)
            {
                WriteLog($"Waiting {_settings.ReconnectDelaySeconds}s before switching to Wi-Fi.");
                await Task.Delay(_settings.ReconnectDelay, ct).ConfigureAwait(false);
            }

            // If the user plugged back in during the delay, abandon the reconnect.
            if (_monitor.ReadLinkState().IsUp)
            {
                WriteLog("Cable is back — staying on Ethernet.");
                SetActivity("Using Ethernet");
                return;
            }

            await RestoreWifiAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
            RaiseStatusChanged();
        }
    }

    public async Task<bool> RestoreWifiAsync(CancellationToken ct)
    {
        SetActivity("Reconnecting to Wi-Fi");

        string? wifiAdapter = _wifi.GetAdapterName();

        if (_adapterDisabledByUs)
        {
            string adapterToEnable = wifiAdapter ?? "Wi-Fi";
            ProcessResult r = await _wifi.SetAdapterEnabledAsync(adapterToEnable, enabled: true, ct)
                .ConfigureAwait(false);

            WriteLog(r.Ok ? "Wireless adapter re-enabled." : $"Could not re-enable the adapter: {r.Text}");
            _adapterDisabledByUs = false;

            // The adapter needs a moment to come back before it accepts a connect request.
            await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            wifiAdapter = _wifi.GetAdapterName() ?? adapterToEnable;
        }

        if (wifiAdapter is null)
        {
            WriteLog("No wireless adapter available.");
            SetActivity("No Wi-Fi adapter");
            return false;
        }

        SavedNetwork? saved = SavedNetworkStore.Load();
        if (saved is null || string.IsNullOrWhiteSpace(saved.ProfileName))
        {
            WriteLog("No saved network yet. Connect to Wi-Fi once with the cable out, then plug it in to record it.");
            SetActivity("Nothing saved to reconnect to");
            return false;
        }

        // Undo the manual setting so Windows also handles this network on its own from now on.
        await _wifi.SetAutoConnectAsync(saved.ProfileName, autoConnect: true, ct).ConfigureAwait(false);
        _profilesSetToManual.Remove(saved.ProfileName);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            WifiStatus current = await _wifi.GetStatusAsync(ct).ConfigureAwait(false);
            if (string.Equals(current.Ssid, saved.Ssid, StringComparison.Ordinal))
            {
                WriteLog($"Connected to \"{saved.Ssid}\".");
                SetActivity($"On Wi-Fi: {saved.Ssid}");
                return true;
            }

            ProcessResult r = await _wifi.ConnectAsync(saved.ProfileName, saved.Ssid, wifiAdapter, ct)
                .ConfigureAwait(false);

            if (!r.Ok)
                WriteLog($"Connect attempt {attempt} failed: {r.Text}");

            // netsh returns as soon as the request is queued, so poll for the real result.
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                WifiStatus check = await _wifi.GetStatusAsync(ct).ConfigureAwait(false);
                if (string.Equals(check.Ssid, saved.Ssid, StringComparison.Ordinal))
                {
                    WriteLog($"Connected to \"{saved.Ssid}\" (attempt {attempt}).");
                    SetActivity($"On Wi-Fi: {saved.Ssid}");
                    return true;
                }
            }

            WriteLog($"Attempt {attempt} did not connect within 12s.");
        }

        WriteLog($"Gave up reconnecting to \"{saved.Ssid}\". It may be out of range or the password may have changed.");
        SetActivity("Reconnect failed");
        return false;
    }

    // ------------------------------------------------------------------ manual

    /// <summary>"Capture now" button — records whatever Wi-Fi is active at this moment.</summary>
    public async Task<bool> CaptureCurrentAsync()
    {
        CancellationToken ct = _shutdown.Token;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            WifiStatus status = await _wifi.GetStatusAsync(ct).ConfigureAwait(false);
            if (!status.IsConnected)
            {
                WriteLog("Not connected to Wi-Fi — nothing to capture.");
                return false;
            }

            await CaptureNetworkAsync(status, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
            RaiseStatusChanged();
        }
    }

    public async Task ReconnectNowAsync()
    {
        CancellationToken ct = _shutdown.Token;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RestoreWifiAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
            RaiseStatusChanged();
        }
    }

    public async Task<StatusSnapshot> GetSnapshotAsync()
    {
        (bool isUp, string? adapter) = _monitor.ReadLinkState();
        WifiStatus wifi = await _wifi.GetStatusAsync(_shutdown.Token).ConfigureAwait(false);

        return new StatusSnapshot
        {
            EthernetUp = isUp,
            EthernetAdapter = adapter,
            CurrentSsid = wifi.Ssid,
            Saved = SavedNetworkStore.Load(),
            Activity = _activity
        };
    }

    // ----------------------------------------------------------------- helpers

    private void SetActivity(string activity)
    {
        _activity = activity;
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void WriteLog(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        Log?.Invoke(this, line);

        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    /// <summary>
    /// Leaves the machine in a sane state: any profile this app forced to manual goes back to
    /// auto, and a disabled adapter is switched back on. Without this, quitting while the cable
    /// is plugged in would leave Wi-Fi permanently not auto-joining.
    /// </summary>
    public void RestoreSystemState()
    {
        try
        {
            foreach (string profile in _profilesSetToManual.ToList())
            {
                _wifi.SetAutoConnectAsync(profile, autoConnect: true, CancellationToken.None)
                     .GetAwaiter().GetResult();
            }
            _profilesSetToManual.Clear();

            if (_adapterDisabledByUs)
            {
                string adapter = _wifi.GetAdapterName() ?? "Wi-Fi";
                _wifi.SetAdapterEnabledAsync(adapter, enabled: true, CancellationToken.None)
                     .GetAwaiter().GetResult();
                _adapterDisabledByUs = false;
            }
        }
        catch
        {
            // Best effort during shutdown.
        }
    }

    public void Dispose()
    {
        RestoreSystemState();

        _shutdown.Cancel();
        _monitor.StateChanged -= OnEthernetStateChanged;
        _monitor.Dispose();
        _shutdown.Dispose();
        _lock.Dispose();
    }
}
