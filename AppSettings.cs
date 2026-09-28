using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace EthernetWifiSwitcher.Core;

internal enum CableAction
{
    /// <summary>Leave the wireless adapter alone; only handle reconnection.</summary>
    LeaveWifiOn = 0,

    /// <summary>Disconnect and mark the profile manual so Windows does not re-join. No admin needed.</summary>
    DisconnectWifi = 1,

    /// <summary>Switch the whole adapter off. Thorough, but needs an elevated process.</summary>
    DisableAdapter = 2
}

internal static class AppPaths
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EthernetWifiSwitcher");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string SavedNetworkFile => Path.Combine(DataFolder, "network.dat");
    public static string LogFile => Path.Combine(DataFolder, "activity.log");

    public static void EnsureCreated() => Directory.CreateDirectory(DataFolder);
}

internal sealed class AppSettings
{
    public CableAction OnCablePluggedIn { get; set; } = CableAction.DisconnectWifi;

    /// <summary>Seconds to wait after the cable is pulled before re-joining Wi-Fi.</summary>
    public int ReconnectDelaySeconds { get; set; } = 3;

    /// <summary>How long a link state must hold before it counts as a real change.</summary>
    public int DebounceMs { get; set; } = 2000;

    /// <summary>Capture the pre-shared key alongside the SSID. Only works when elevated.</summary>
    public bool CapturePassword { get; set; } = true;

    /// <summary>Apply the rules once at launch, not only on later plug/unplug events.</summary>
    public bool ActOnStartupState { get; set; } = true;

    [JsonIgnore]
    public TimeSpan ReconnectDelay => TimeSpan.FromSeconds(Math.Clamp(ReconnectDelaySeconds, 0, 120));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable settings should never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Non-fatal.
        }
    }
}

/// <summary>Per-user "Run at sign-in" entry. No admin rights required.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EthernetWifiSwitcher";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{exe}\" --tray");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
