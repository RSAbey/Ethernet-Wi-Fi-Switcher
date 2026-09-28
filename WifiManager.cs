using System.Net.NetworkInformation;
using System.Xml.Linq;

namespace EthernetWifiSwitcher.Core;

internal sealed record WifiStatus(string? Ssid, string? ProfileName)
{
    public bool IsConnected => !string.IsNullOrWhiteSpace(Ssid);
}

/// <summary>
/// Everything this app does to the wireless adapter. Thin wrapper over "netsh wlan".
/// </summary>
internal sealed class WifiManager
{
    /// <summary>
    /// Adapter name as Windows knows it (usually "Wi-Fi"). Taken from the network stack
    /// rather than from parsed text, so it is correct on any Windows language.
    /// </summary>
    public string? GetAdapterName()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.Name)
            .FirstOrDefault();
    }

    public bool IsAdapterEnabled()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                      && n.OperationalStatus != OperationalStatus.Unknown);
    }

    /// <summary>Which network the wireless adapter is on right now, if any.</summary>
    public async Task<WifiStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ProcessResult r = await ProcessRunner.NetshAsync("wlan show interfaces", ct).ConfigureAwait(false);
        if (!r.Ok) return new WifiStatus(null, null);

        Dictionary<string, string> fields = ParseFields(r.StdOut);

        // "SSID" is kept as-is in Windows localisations; an exact key match also avoids "BSSID".
        fields.TryGetValue("SSID", out string? ssid);
        if (string.IsNullOrWhiteSpace(ssid)) return new WifiStatus(null, null);

        fields.TryGetValue("Profile", out string? profile);
        // On a non-English Windows the "Profile" label differs. Windows names profiles after
        // the SSID by default, so that is a safe fallback.
        if (string.IsNullOrWhiteSpace(profile)) profile = ssid;

        return new WifiStatus(ssid.Trim(), profile.Trim());
    }

    /// <summary>All saved wireless profile names on this machine.</summary>
    public async Task<IReadOnlyList<string>> GetSavedProfilesAsync(CancellationToken ct = default)
    {
        ProcessResult r = await ProcessRunner.NetshAsync("wlan show profiles", ct).ConfigureAwait(false);
        if (!r.Ok) return Array.Empty<string>();

        var names = new List<string>();
        foreach (string raw in r.StdOut.Split('\n'))
        {
            string line = raw.Trim();
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;

            // Profile lines are indented under a group heading and look like
            //   "All User Profile     : MyNetwork"
            string key = line[..idx];
            if (!key.Contains("Profile", StringComparison.OrdinalIgnoreCase)) continue;

            string value = line[(idx + 1)..].Trim();
            if (value.Length > 0 && !names.Contains(value, StringComparer.Ordinal))
                names.Add(value);
        }
        return names;
    }

    /// <summary>
    /// Reads the pre-shared key for a saved profile by exporting it to XML.
    /// Requires an elevated process; returns null otherwise (Windows keeps the key encrypted).
    /// </summary>
    public async Task<string?> TryReadPasswordAsync(string profileName, CancellationToken ct = default)
    {
        if (!IsSafeForCommandLine(profileName)) return null;

        string tempDir = Path.Combine(Path.GetTempPath(), "ews-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ProcessResult r = await ProcessRunner.NetshAsync(
                $"wlan export profile name=\"{profileName}\" key=clear folder=\"{tempDir}\"", ct)
                .ConfigureAwait(false);
            if (!r.Ok) return null;

            string? file = Directory.EnumerateFiles(tempDir, "*.xml").FirstOrDefault();
            if (file is null) return null;

            XDocument doc = XDocument.Load(file);

            // Element names in the exported profile are fixed English, independent of Windows language.
            string? isProtected = Local(doc, "protected");
            if (string.Equals(isProtected, "true", StringComparison.OrdinalIgnoreCase))
                return null; // not elevated — Windows returned the key still encrypted

            string? key = Local(doc, "keyMaterial");
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
        finally
        {
            // The export contains the key in clear text; remove it immediately.
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        static string? Local(XDocument doc, string name) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
    }

    public async Task<ProcessResult> ConnectAsync(string profileName, string ssid, string adapter, CancellationToken ct = default)
    {
        if (!IsSafeForCommandLine(profileName) || !IsSafeForCommandLine(ssid) || !IsSafeForCommandLine(adapter))
            return new ProcessResult(-1, string.Empty, "Network name contains characters this app cannot pass to netsh.");

        return await ProcessRunner.NetshAsync(
            $"wlan connect name=\"{profileName}\" ssid=\"{ssid}\" interface=\"{adapter}\"", ct)
            .ConfigureAwait(false);
    }

    public async Task<ProcessResult> DisconnectAsync(string adapter, CancellationToken ct = default)
    {
        if (!IsSafeForCommandLine(adapter))
            return new ProcessResult(-1, string.Empty, "Adapter name contains unsupported characters.");

        return await ProcessRunner.NetshAsync($"wlan disconnect interface=\"{adapter}\"", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Switches a profile between auto-join and manual. Setting it to manual is how the app
    /// stops Windows from silently re-joining while the cable is in, without touching the adapter.
    /// </summary>
    public async Task<ProcessResult> SetAutoConnectAsync(string profileName, bool autoConnect, CancellationToken ct = default)
    {
        if (!IsSafeForCommandLine(profileName))
            return new ProcessResult(-1, string.Empty, "Profile name contains unsupported characters.");

        string mode = autoConnect ? "auto" : "manual";
        return await ProcessRunner.NetshAsync(
            $"wlan set profileparameter name=\"{profileName}\" connectionmode={mode}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Enables or disables the wireless adapter itself. Requires elevation.</summary>
    public async Task<ProcessResult> SetAdapterEnabledAsync(string adapter, bool enabled, CancellationToken ct = default)
    {
        if (!IsSafeForCommandLine(adapter))
            return new ProcessResult(-1, string.Empty, "Adapter name contains unsupported characters.");

        string state = enabled ? "enable" : "disable";
        return await ProcessRunner.NetshAsync(
            $"interface set interface name=\"{adapter}\" admin={state}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Splits "Key : Value" output into a lookup. First occurrence of a key wins.</summary>
    private static Dictionary<string, string> ParseFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;

            string key = line[..idx].Trim();
            string value = line[(idx + 1)..].Trim();   // split on the FIRST colon, so MAC values survive
            if (key.Length == 0) continue;

            fields.TryAdd(key, value);
        }

        return fields;
    }

    /// <summary>
    /// netsh takes quoted arguments, so a name containing a double quote or a newline could
    /// break out of the quoting. Those are rejected rather than escaped.
    /// </summary>
    private static bool IsSafeForCommandLine(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Contains('"')
           && !value.Contains('\r')
           && !value.Contains('\n')
           && !value.Contains('%');
}
