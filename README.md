# Ethernet Wi-Fi Switcher

A small Windows 11 tray app. Plug in the LAN cable and it remembers the Wi-Fi network you
were on, then takes Wi-Fi down. Pull the cable out and it puts you straight back on that
network.

## Build

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd EthernetWifiSwitcher
dotnet restore
dotnet build -c Release
```

Run it:

```powershell
dotnet run -c Release
```

Make a single `.exe` you can drop anywhere:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The result lands in `bin\Release\net8.0-windows\win-x64\publish\EthernetWifiSwitcher.exe`.
Add `--self-contained true` if you want it to run on machines without the .NET runtime
(bigger file, no prerequisites).

## First run

1. Start the app with the **cable unplugged**, while connected to your Wi-Fi.
2. Click **Capture current Wi-Fi**. The SSID is recorded straight away.
3. Plug the cable in — Wi-Fi drops. Pull it out — Wi-Fi comes back.

Closing the window hides it. Exit from the tray icon's right-click menu.

## Administrator rights

Everything works unelevated **except** two things:

| Feature | Needs admin |
|---|---|
| Detecting cable plug/unplug | No |
| Saving the SSID | No |
| Reconnecting to Wi-Fi | No |
| Reading the Wi-Fi **password** | Yes |
| "Switch the Wi-Fi adapter off" mode | Yes |

Reconnecting does not need the password — Windows already holds the key inside the saved
profile, and `netsh wlan connect` uses it. The password is only captured because it is
useful to have; run the app as administrator once if you want it stored.

To always run elevated, change `asInvoker` to `requireAdministrator` in `app.manifest`.
Be aware that a `requireAdministrator` app cannot be launched by the normal sign-in
startup entry without a UAC prompt each boot.

## How the pieces fit

| File | Job |
|---|---|
| `Core/EthernetMonitor.cs` | Watches the RJ45 link. Windows' network events plus a 1s poll, with a debounce so cable bounce is ignored. Filters out virtual adapters (Hyper-V, VPNs, Docker) that permanently report "up". |
| `Core/WifiManager.cs` | Wraps `netsh wlan`. Reads the current SSID, exports the profile to get the key, connects and disconnects. |
| `Core/SavedNetworkStore.cs` | Writes the remembered network to `%LOCALAPPDATA%\EthernetWifiSwitcher\network.dat`, encrypted with DPAPI under your user account. |
| `Core/SwitchController.cs` | The actual rules. One semaphore serialises transitions so a fast unplug/replug can't run two sequences at once. |
| `Ui/TrayApplicationContext.cs` | Tray icon and app lifetime. |
| `Ui/MainForm.cs` | Status, settings and log. |

## Why disconnecting needs a trick

`netsh wlan disconnect` on its own doesn't hold: if the profile is set to auto-join,
Windows reconnects within seconds. So the app also flips that one profile to
`connectionmode=manual` while the cable is in, and back to `auto` when it comes out.

That change is persistent, so the app restores it on exit and on reconnect. If the process
is ever killed outright (Task Manager, power loss) while the cable is plugged in, the
profile can be left on manual. Fix with:

```powershell
netsh wlan set profileparameter name="YourNetwork" connectionmode=auto
```

## Known limits

- **Only the saved profile is set to manual.** If you have other saved networks in range,
  Windows may still join one of them. Use the "switch the adapter off" mode if you need
  Wi-Fi genuinely gone.
- **Enterprise / 802.1X networks** have no stored pre-shared key, so nothing is captured.
  Reconnection still works through the profile.
- **Non-English Windows.** The SSID is parsed from `netsh` output; the `SSID` label is
  unchanged across Windows languages, and the adapter name comes from the network stack
  rather than from text, so this should hold. Not tested outside English.
- Docked laptops where the dock's Ethernet stays enumerated while unplugged report link
  state correctly on most drivers, but not all.

## Files it writes

All under `%LOCALAPPDATA%\EthernetWifiSwitcher\`:

- `settings.json` — plain JSON, no secrets
- `network.dat` — DPAPI-encrypted SSID and key
- `activity.log` — plain text log

DPAPI ties the file to your Windows account, so it is not readable by other users or on
another machine. It is still recoverable by anything running as you — treat it as
convenience, not as a vault.
