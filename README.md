**EN** | [ES](README_ES.md)

# GTAV Riding Companion — PC bridge

A local Windows bridge that lets an Android phone see and control the [GTBike V](https://www.gtbikev.com/) mod for GTA V over your home Wi-Fi: live metrics, routes, profile and ride settings, sensor sources, workout selection and progress, saved activities (FIT), a game map and the leaderboard.

This repository holds everything that runs on the PC. The phone app is in a separate repository: **[gtav-riding-companion-android](https://github.com/jhonarias91/gtav-riding-companion-android)**. See the [Android bridge integration guide](bridge/ANDROID_BRIDGE_V1.md) for packaging and acceptance checks.

> Unofficial community project. Not affiliated with GTBike V, Rockstar Games, Zwift or Garmin. It does **not** include, modify or redistribute `GTBikeV.dll`; it adds a separate ScriptHookVDotNet script that talks to the mod already installed on your PC.

## Install the Android app

Download the APK on your phone (Android 8.0 or later). [Download APK](https://github.com/jhonarias91/gtav-riding-companion-android/releases/download/v0.6.0-p13/gtav-riding-companion.apk) · [View release v0.6.0-p13](https://github.com/jhonarias91/gtav-riding-companion-android/releases/tag/v0.6.0-p13)

1. Open `gtav-riding-companion.apk` from your downloads.
2. If Android asks for permission to install unknown apps, tap **Settings** and enable **Allow from this source** for the browser or file manager that opened the APK.
3. Return to the installer and tap **Install**.

To build the APK yourself, follow the instructions in the Android repository. [Build from the release ZIP](https://github.com/jhonarias91/gtav-riding-companion-android/blob/HEAD/README.md#build-from-the-release-zip)

## How it works

```text
Android app (gtav-riding-companion-android)
        │  private LAN, port 8788 · automatic HTTPS with a pinned key (http-dev only with --http-dev) · QR pairing + bearer token
        ▼
Node.js bridge v1 (bridge/start-v1.mjs)
        │  local JSON files in %LOCALAPPDATA%\GTBikeVCompanion\v1 · commands with expiry and acknowledgement
        ▼
GTBikeVCompanionV1.dll — ScriptHookVDotNet 3 script (bridge/game-adapter-v1)
        │  reflection on the members of GTBike V 0.8.0.4 that are known to be safe
        ▼
GTBikeV.dll 0.8.0.4 → ANT+ / Bluetooth sensors, the trainer and the game
```

- The **adapter** runs inside GTA V on the script thread. Every 250 ms it writes a snapshot, and it applies commands through the mod's own handlers (menu items, native events). It never creates a second trainer or a second copy of the mod.
- The **bridge** validates every command against a versioned contract (`bridge/contracts/v1`). It checks that the change really happened before it reports `applied`, and it never runs the same request twice.
- **Sensors stay connected to the PC.** The phone can scan for Bluetooth sensors and ask the PC to lock one by address, but it never connects to them itself, so riding data never passes through the phone.
- **Manual ERG control, target power and workout pause, skip or end are deliberately excluded.** They are blocked in the contract, the bridge and the adapter (`bridge/contracts/v1/policy.json`). Selecting a workout uses the mod's own Select path, and the mod turns ERG on by itself, as it does from its menu.

## Requirements

| Component | Version |
| --- | --- |
| Windows | 10 or 11, 64-bit |
| GTA V | Tested on GTA V Enhanced, with ScriptHookV and **ScriptHookVDotNet 3** (built and tested against 3.9.0.0) |
| [GTBike V](https://www.gtbikev.com/) | **0.8.0.4** must be installed before setting up the bridge. Follow the [official GTBike V installation guide](https://www.gtbikev.com/content/installation-guide/). Other versions are detected and all writes are disabled. |
| .NET Framework | 4.8 or later. The adapter uses it at runtime, and building from source uses its 64-bit `csc.exe` compiler. Windows 11 and Windows 10 22H2 already include a compatible version. [Versions included with Windows](https://learn.microsoft.com/en-us/dotnet/framework/install/versions-and-dependencies) |
| Node.js | 22.2 or later (tested on 26.13). Select the Windows x64 installer (`.msi`) on the [official Node.js download page](https://nodejs.org/en/download). |
| Phone | Android 8.0+ with the [GTAV Riding Companion app](#install-the-android-app), on the same local network as the PC (Ethernet on the PC is fine). Scanning the connection QR uses Google Play services; without them, type the address and key fingerprint by hand. |

## Quick start

Run these in PowerShell from the repository folder, **with GTA V closed**. Replace the GTA path with your own installation folder (the one that contains `ScriptHookVDotNet3.dll`).

1. Install dependencies and run the tests:

   ```powershell
   npm ci
   npm test
   ```

2. Build the game adapter and copy it into `GTA V\scripts`. If the file already exists, the script keeps a dated backup first:

   ```powershell
   $gta = 'C:\Program Files\Rockstar Games\Grand Theft Auto V Enhanced'
   .\bridge\Build-GameAdapterV1.ps1 -GtaDirectory $gta -Install
   ```

3. Optional: check that your copy of GTBikeV is compatible with the adapter.

   GTBikeV has no public API, so the adapter reads the mod's internal classes, fields and methods by their names. The adapter was written against GTBikeV 0.8.0.4. If your `GTBikeV.dll` is a different version, some of those names may have changed, and the features that use them won't work in the game. This check opens `GTBikeV.dll` from disk, looks up every name the adapter needs and reports any it can't find. You don't need GTA V running, and the check doesn't change anything in the game folder.

   The last line is `RESOLVE PASS` when the adapter matches your mod. If it's `RESOLVE FAIL`, your mod version doesn't match: look for the `Missing ... members` lines in the output to see which names weren't found.

   ```powershell
   .\bridge\Test-BindingResolve.ps1 -ModDll "$gta\scripts\GTBikeV.dll" -GtaDirectory $gta
   ```

4. Allow the Android app to reach the bridge through Windows Firewall. Run this once, in PowerShell **as administrator**:

   ```powershell
   .\bridge\Enable-LanAccess.ps1 -Port 8788
   ```

   <details>
   <summary>Why do I need this? More about it</summary>

   The app connects to the bridge over your Wi-Fi. By default, Windows Firewall blocks connections that come into the PC from other devices, so without this rule the app can't find the bridge, even when both are on the same network.

   **What the rule allows, and nothing more:**
   - Incoming TCP connections on port 8788 only.
   - Only to `node.exe`, the program that runs the bridge. Other programs on that port stay blocked.
   - Only from devices on your local network (the *local subnet*), never from the Internet.
   - Only while Windows treats the network as **Private**. On a Public network, such as a café or hotel Wi-Fi, the rule doesn't apply.

   The script doesn't turn off the firewall and doesn't open anything on your router. The rule appears in *Windows Defender Firewall with Advanced Security → Inbound Rules* as **GTAV Riding Companion Wi-Fi (8788)**. You can delete it at any time with `-Remove` (see [Uninstall](#uninstall)).

   It prints `Firewall rule created: Node.js on TCP 8788, Private profile, local subnet only.` when the rule is created, or `The bridge firewall rule already exists.` if it was already there. Either message means you're done.

   **Opening PowerShell as administrator:** Start → right-click *Terminal* or *Windows PowerShell* → *Run as administrator*, then go to this repository's folder.

   **Set your home network to Private.** Open *Settings → Network & internet*, choose your Wi-Fi or Ethernet connection, and set **Network profile type** to **Private**. Only do this for networks you trust, such as your own home network. To check from PowerShell:

   ```powershell
   Get-NetConnectionProfile   # NetworkCategory must be Private
   ```

   **"My network is Public and the app still connects."** Then it isn't this rule letting the phone in. When Node.js first listens on a network, Windows asks whether to allow it. If you accepted on a Public network, Windows created rules named **Node.js JavaScript Runtime** that allow Node.js on Public networks, on **any port and from any address**. That works, but it opens every Node.js program on your PC on any Public network you join. Check with:

   ```powershell
   Get-NetFirewallApplicationFilter -Program (Get-Command node).Source | Get-NetFirewallRule | Select-Object DisplayName, Action, Profile
   ```

   Safer setup: set your home network to Private, run the script above, then disable the Public *Node.js JavaScript Runtime* rules (as administrator, `... | Where-Object Profile -eq Public | Disable-NetFirewallRule`). Disabling keeps the rules, so you can turn them back on with `Enable-NetFirewallRule`.

   **If it fails:**

   | Message | What to do |
   | --- | --- |
   | `Access is denied` / `Acceso denegado` | PowerShell isn't running as administrator. Open it as administrator and try again. |
   | `node` is not recognized | Node.js isn't installed or isn't on `PATH`. Install Node.js, open a new PowerShell window and try again. |
   | `running scripts is disabled on this system` | Allow scripts for this window only: `Set-ExecutionPolicy -Scope Process Bypass`, then run the script again. |

   **Things to keep in mind:**
   - If you start the bridge with a different `--port`, create the rule with that same `-Port`.
   - The rule points to the path `node.exe` had when you created it. If you reinstall Node.js in a different folder, run the script with `-Remove`, then run it again.
   - The first time the bridge starts, Windows may ask whether to allow Node.js on networks. Tick only **Private networks** (not Public) and choose **Allow**. If you choose *Cancel*, Windows adds a rule that **blocks** Node.js, and block rules win over this one. To find those block rules, run this as administrator:

     ```powershell
     Get-NetFirewallApplicationFilter -Program (Get-Command node).Source | Get-NetFirewallRule | Where-Object Action -eq Block
     ```

     If it lists any, check them, then delete them by adding `| Remove-NetFirewallRule` to the end of the same command.

   </details>

5. Start the bridge and keep the window open:

   ```powershell
   node bridge/start-v1.mjs
   ```

   The console prints the bridge identity, the key fingerprint, the PC address and port, the paired phones with their expiry date, and always a **QR code** for the app. Each QR pairs one phone; after that, press Enter to show a new one. Enter also shows a six-digit code, only needed if you type the address in the app by hand (it expires after 5 minutes).

   The bridge always uses HTTPS. The first time it starts, it creates its key in `%LOCALAPPDATA%\GTBikeVCompanion\tls\bridge-key.pem`. Keep that file: the app trusts that key, and if it is deleted every phone has to scan the QR again.

   You pair a phone once every 30 days. When the pairing expires, the app asks you to scan the QR again, and the console shows a fresh QR by itself as soon as the phone tries to connect.

6. Start GTA V in Story Mode with GTBike V. In the app, tap **Scan QR code** and point the camera at the console. That's all: the QR carries the address, port, key fingerprint, bridge identity and a one-time pairing key, so the phone checks the bridge and pairs without typing anything.

   If you want GTA V to launch directly into Story Mode, download and extract the mod. With GTA V closed, copy `StraightToStoryMode.asi` to the game's root folder, where `ScriptHookVDotNet3.dll` is. [Download Straight To Story Mode](https://www.gta5-mods.com/scripts/straight-to-story-mode)

### Bridge options

Optional flags for `start-v1.mjs`:

```powershell
node bridge/start-v1.mjs --port 8788 --host 0.0.0.0 --map-directory D:\gta-map --enable-riders
```

| Flag | Effect |
| --- | --- |
| `--port` | Listening port (default 8788) |
| `--host` | `0.0.0.0` (default), `127.0.0.1`, or one of the PC's private IPv4 addresses |
| `--map-directory` | Folder with your own `map.png` and `calibration.json` for the game map (format in the [Android bridge guide](bridge/ANDROID_BRIDGE_V1.md#p8a--game-map)) |
| `--enable-riders` | Shares the current course leaderboard with paired phones |
| `--http-dev` | Plain HTTP instead of HTTPS, for development with debug builds of the app only |

### Additional steps

<details>
<summary>Use your own certificate instead of the automatic one (optional)</summary>

By default the bridge needs nothing: it keeps one key and rebuilds a self-signed certificate on every start with the PC's current addresses, so a new IP from the router doesn't break the connection. The app pins the key, not the certificate.

To use your own PEM certificate and key instead, set both variables before starting the bridge. The certificate's SAN must include the PC's LAN IPv4 address, and you should keep the same key when you renew it, because the phone pins its fingerprint:

```powershell
$env:GTBIKE_BRIDGE_TLS_CERT = "C:\path\to\bridge.pem"
$env:GTBIKE_BRIDGE_TLS_KEY  = "C:\path\to\bridge-key.pem"
node bridge/start-v1.mjs
```

The QR then carries the fingerprint of your key.

</details>

### Troubleshooting

```powershell
.\bridge\Diagnose-BridgeV1.ps1
```

This reports the Node version, whether the port is listening, and the age and state of the adapter snapshot (mod version, binding, game control). It never prints the PIN or tokens.

- **The phone cannot reach the PC:** both devices must be on the same network, the Windows network profile must be *Private*, and the firewall rule from [step 4](#quick-start) must exist with no Node.js block rule overriding it. Guest networks with client isolation block local traffic. A quick test is to open `https://<PC address>:8788/api/v1/hello` in the phone's browser: it warns about the certificate (expected, it is self-signed); if you continue and see JSON, the network and firewall are fine.
- **"Adapter absent":** GTA V is not running, or `scripts\GTBikeVCompanionV1.dll` is not installed. The bridge and GTA V must run under the same Windows user.
- **The PC's IP changed:** restart the bridge and scan the QR again. The key stays the same, so the phone keeps trusting the bridge.
- **"The QR scanner is not available":** the phone has no Google Play services. Type the address, port and the key fingerprint the console prints.
- **"Port 8788 is already in use", or the bridge window was closed but the phone still connects:** a previous bridge process is still running and holds the port. Find its process ID (PID), the number in the last column:

  ```powershell
  netstat -ano | findstr :8788
  ```

  ```text
  TCP    0.0.0.0:8788           0.0.0.0:0              LISTENING       58480
  TCP    192.168.1.27:8788      192.168.1.181:48330    ESTABLISHED     58480
  ```

  Check that the PID belongs to `node.exe`, then stop it. Use your own PID, not the one in this example:

  ```powershell
  tasklist /FI "PID eq 58480"
  taskkill /F /PID 58480
  ```

  Then start the bridge again. If the PID is not `node.exe`, another program is using the port: start the bridge with a different `--port` and create the firewall rule for that port.

### Uninstall

Close GTA V and the bridge, then:

```powershell
Remove-Item "$gta\scripts\GTBikeVCompanionV1.dll"
.\bridge\Enable-LanAccess.ps1 -Port 8788 -Remove   # as administrator
```

To restore an earlier version instead, copy back the backup the installer printed. Pairing data is stored in `%LOCALAPPDATA%\GTBikeVCompanion\bridge-v1`.

## Packaged install (no Git or npm on the gaming PC)

From a **clean** checkout, build a ZIP. It contains the adapter, the bridge bundle, a manifest with the commit, and the SHA-256 of every file:

```powershell
npm ci
.\bridge\Pack-BridgeV1.ps1 -GtaDirectory $gta
```

Extract the ZIP from `outputs\release`, then from the extracted folder, with GTA V closed:

```powershell
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta -WhatIf
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta
node .\bridge-v1.mjs
```

The installer refuses to run if GTA V is open or if `ScriptHookVDotNet3.dll` differs from the one the package was built against.

## Features

- **Live ride:** metrics and the state of the mod, the game and the sensors; turn the mod on and off.
- **Routes:** browse the route catalog, preview a route on the PC, and start a route or a free ride.
- **Profile and ride settings:** weight, FTP, max heart rate, trainer difficulty, road feel, autopilot, bike and character.
- **Sensors:** choose the source for each function (Auto, Lock or Skip), as in the mod's Device Selection, or pick a Bluetooth sensor from the phone and let the PC connect to it.
- **Workouts:** browse the workout library, select a workout like the mod's menu does (it replaces a running one, after a warning on the phone) and follow your progress. Pause, skip and end stay on the PC.
- **Activities:** save the ride like the mod's Save item; list, summarise and download FIT files.
- **Game map and leaderboard:** your position on a calibrated game map, and the current course leaderboard (opt-in with `--enable-riders`).
- **Windows package and diagnostics:** a ZIP install for PCs without Git or npm, and a read-only diagnostic script.

## API overview

All routes except `hello` and `pair` require `Authorization: Bearer <token>`. The phone discovers what is available in each session from `GET /api/v1/capabilities`. Every unavailable capability carries a reason code, and `apiMinor` in `GET /api/v1/hello` identifies the contract revision (currently 10).

| Method and path | Purpose |
| --- | --- |
| `GET /api/v1/hello` | Bridge identity, API version, transport |
| `POST /api/v1/pair`, `POST /api/v1/unpair` | Pairing (QR key or six-digit code) and token revocation |
| `GET /api/v1/capabilities` | What the phone may read or change right now, with reason codes |
| `GET /api/v1/state` | Layers (bridge, adapter, mod, receivers), metrics and sensors |
| `POST /api/v1/commands`, `GET /api/v1/operations/{requestId}` | Typed commands and their lifecycle (`pending → applied / rejected / failed / unknown`) |
| `GET /api/v1/routes`, `GET /api/v1/route-preview` | Route catalog and PC preview panel |
| `GET /api/v1/settings` | Profile, ride settings and appearance |
| `GET /api/v1/device-roles` | Sensor source and rule per function |
| `GET /api/v1/workout-plans`, `GET /api/v1/workouts`, `GET /api/v1/workout-progress` | Workout library and progress (selection is the `workouts.select` command) |
| `GET /api/v1/activities` | Saved FIT activities, summaries and downloads |
| `GET /api/v1/map`, `GET /api/v1/riders` | Game map position and leaderboard (opt-in) |

JSON Schemas live in [bridge/contracts/v1](bridge/contracts/v1), with valid and invalid examples in [bridge/fixtures/v1](bridge/fixtures/v1).

## Development

```powershell
npm test                              # Node: API, contracts (Ajv), operations, policy, FIT
.\bridge\Test-GameAdapterV1.ps1       # builds the adapter against a mock GTBike V and runs its checks
.\bridge\Test-BindingResolve.ps1 -ModDll "$gta\scripts\GTBikeV.dll" -GtaDirectory $gta
```

Tests use temporary folders and a simulated adapter. They do not need GTA V. Behaviour inside the game is verified separately with the checklists of each phase.

### Repository layout

| Path | Contents |
| --- | --- |
| `bridge/api-v1/` | Bridge v1 server: pairing, validation, operations, normalisation, activities, map and riders |
| `bridge/game-adapter-v1/` | C# 5 ScriptHookVDotNet adapter for GTBike V 0.8.0.4 |
| `bridge/contracts/v1/`, `bridge/fixtures/v1/` | Versioned JSON contract, allow/deny policy and examples |
| `bridge/compatibility/` | Binding notes for GTBike V 0.8.0.4: every member the adapter uses, with evidence |
| `bridge/*.ps1` | Build, install, package, firewall and diagnostic scripts |
| `tests/` | Node test suites and C# harnesses |

## Security

<details>
<summary>How the bridge protects your PC, your data and your accounts</summary>

- The bridge serves HTTPS (TLS 1.2+) by default. The phone pins the bridge's public key, learned from the QR on the PC screen, and refuses any other. The QR also holds a one-time pairing key (128-bit random): treat the QR like the code it replaces and don't share a photo of it. The key changes after every pairing and on every bridge start. `--http-dev` serves plain HTTP, limited to private IPv4 addresses and loopback, and only debug builds of the app accept it. Never forward the port to the Internet.
- The bridge's private key (`%LOCALAPPDATA%\GTBikeVCompanion\tls\bridge-key.pem`) stays on the PC. Don't share or commit it.
- Pairing uses the QR's one-time key, or a six-digit code (for typing the address by hand) that lasts 5 minutes. Both rotate after each pairing, with at most 5 attempts per IP per minute. The phone's token lasts 30 days. The bridge stores only token hashes, and a phone can revoke its own token. An expired token never works again; its hash is kept for 90 days only so the console can name the phone that needs a new code.
- Commands are allow-listed and strictly validated, and each request ID runs at most once. Reflection targets a fixed list of members for profile `gtbikev-0804`. On any other mod version, writes are disabled.

</details>

## Roadmap

- **Windows executable (`.exe`) for the bridge.** One file to download and run, without installing Node.js or using npm.

## Credits and sources

- [GTBike V](https://www.gtbikev.com/): [configuration](https://www.gtbikev.com/content/configuration/) and [support](https://www.gtbikev.com/support/) pages.
- [ScriptHookVDotNet](https://github.com/scripthookvdotnet/scripthookvdotnet) and [LemonUI](https://github.com/LemonUIbyLemon/LemonUI).
- [Garmin FIT JavaScript SDK](https://github.com/garmin/fit-javascript-sdk) (npm `@garmin/fitsdk`). Its license ships in the Windows package as `GARMIN-FIT-SDK-LICENSE.txt`.
- [qrcode-generator](https://github.com/kazuhikoarase/qrcode-generator) by Kazuhiko Arase (MIT), for the connection QR. Its license ships in the Windows package as `QRCODE-GENERATOR-LICENSE.txt`. QR Code is a registered trademark of DENSO WAVE INCORPORATED.

## License

No license has been chosen yet. Until a `LICENSE` file is added, all rights are reserved.
