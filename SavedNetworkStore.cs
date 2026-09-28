using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EthernetWifiSwitcher.Core;

internal sealed class SavedNetwork
{
    public string Ssid { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Null when the app was not elevated at capture time. Not required to reconnect.</summary>
    public string? Password { get; set; }

    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;

    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

/// <summary>
/// Persists the last-used wireless network.
///
/// The file is encrypted with DPAPI under the current user account, so the key is not
/// readable by other users on the machine and is useless if the file is copied elsewhere.
/// It is still recoverable by anything running as you — treat it as convenience, not a vault.
/// </summary>
internal static class SavedNetworkStore
{
    // Ties the ciphertext to this app, so another program's DPAPI blob cannot be swapped in.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EthernetWifiSwitcher.v1");

    public static void Save(SavedNetwork network)
    {
        AppPaths.EnsureCreated();

        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(network);
        byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        Array.Clear(plain);                                  // don't leave the key sitting in memory
        File.WriteAllBytes(AppPaths.SavedNetworkFile, cipher);
    }

    public static SavedNetwork? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SavedNetworkFile)) return null;

            byte[] cipher = File.ReadAllBytes(AppPaths.SavedNetworkFile);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);

            try
            {
                return JsonSerializer.Deserialize<SavedNetwork>(plain);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch
        {
            // Wrong user, moved profile, or corrupt file — behave as if nothing was saved.
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.SavedNetworkFile))
                File.Delete(AppPaths.SavedNetworkFile);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
