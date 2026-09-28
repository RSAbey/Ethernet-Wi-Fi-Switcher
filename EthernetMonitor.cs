using System.Net.NetworkInformation;

namespace EthernetWifiSwitcher.Core;

internal sealed class EthernetChangedEventArgs(bool isUp, string? adapterName) : EventArgs
{
    public bool IsUp { get; } = isUp;
    public string? AdapterName { get; } = adapterName;
}

/// <summary>
/// Watches for a physical RJ45 link coming up or going down.
///
/// Windows' NetworkChange events are quick but unreliable (they can be missed entirely on
/// some drivers), so a 1-second poll runs alongside them as the source of truth. A state has
/// to hold steady for DebounceMs before it is reported, which filters out the flapping you
/// get during the first moments after plugging a cable in.
/// </summary>
internal sealed class EthernetMonitor : IDisposable
{
    private const int PollIntervalMs = 1000;

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly int _debounceMs;

    private bool? _confirmedState;
    private bool _candidateState;
    private DateTime _candidateSince = DateTime.MinValue;
    private bool _disposed;

    public event EventHandler<EthernetChangedEventArgs>? StateChanged;

    /// <summary>Last reported state. Null until the first reading settles.</summary>
    public bool? IsUp { get { lock (_gate) return _confirmedState; } }

    public EthernetMonitor(int debounceMs = 2000)
    {
        _debounceMs = Math.Max(0, debounceMs);

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;

        _timer = new System.Threading.Timer(_ => Poll(), null, 0, PollIntervalMs);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Poll();

    private void Poll()
    {
        if (_disposed) return;

        bool isUp;
        string? adapter;
        try
        {
            (isUp, adapter) = ReadLinkState();
        }
        catch
        {
            return; // transient WMI/driver hiccup — the next tick will retry
        }

        bool shouldRaise = false;
        lock (_gate)
        {
            if (_candidateState != isUp || _candidateSince == DateTime.MinValue)
            {
                _candidateState = isUp;
                _candidateSince = DateTime.UtcNow;
            }

            bool settled = (DateTime.UtcNow - _candidateSince).TotalMilliseconds >= _debounceMs;

            if (settled && _confirmedState != isUp)
            {
                _confirmedState = isUp;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
            StateChanged?.Invoke(this, new EthernetChangedEventArgs(isUp, adapter));
    }

    /// <summary>Forces an immediate reading, bypassing the debounce window.</summary>
    public (bool IsUp, string? AdapterName) ReadLinkState()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (!IsPhysicalEthernet(nic)) continue;
            return (true, nic.Name);
        }
        return (false, null);
    }

    private static bool IsPhysicalEthernet(NetworkInterface nic)
    {
        bool typeMatches = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit;

        if (!typeMatches) return false;

        // Virtual adapters (Hyper-V, VPN clients, VMware, Docker) also report as Ethernet and
        // sit permanently "Up", which would make the app think a cable is always plugged in.
        string text = (nic.Description + " " + nic.Name).ToLowerInvariant();

        string[] virtualMarkers =
        [
            "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "loopback",
            "tap-", "tap adapter", "tunnel", "pseudo", "docker", "wan miniport",
            "bluetooth", "vpn", "wireguard", "zerotier", "tailscale", "npcap", "teredo"
        ];

        return !virtualMarkers.Any(marker => text.Contains(marker));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _timer.Dispose();
    }
}
