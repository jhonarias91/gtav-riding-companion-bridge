**EN** | [ES](ANDROID_BRIDGE_V1_ES.md)

# Android bridge v1 for Windows

The `android-v1` branch brings together the bridge, adapter and integration package from phases P2 through P8. The Android app is in the same branch of the sibling repository, `gtav-riding-companion-android`. The app queries `GET /api/v1/capabilities` to decide what to display or allow. The `apiMinor` value identifies the bridge contract revision. During phases P2 through P8, it ranged from 0 to 6. Capabilities describe the features available in each session.

## Compatibility and status

| Component | Verified version | Evidence |
| --- | --- | --- |
| GTBike V | Assembly version 0.8.0.4, profile `gtbikev-0804` | Map and leaderboard signatures resolved through reflection |
| ScriptHookVDotNet3 | Assembly version 3.9.0.0 from the GTA V Enhanced installation | x64 adapter compiled with the .NET Framework C# compiler |
| Node.js | 26.13.0 | HTTP and calibration tests over loopback |
| GTA V Enhanced and phone | Pending | Complete the acceptance checks with the game and phone |

The package identifies the commit, the reference DLL and the SHA-256 hash of each file. The integration channel requires Node.js 22.2 or later. Automated tests ran on Node.js 26.13.0. The bridge uses HTTPS by default. The `--http-dev` option enables HTTP for debug builds of the app. Android distribution requires verifying the PC's identity over HTTPS (increment 2b) and completing the in-game acceptance checks for P2 through P8.

## Package, install and start the bridge

Close GTA V and run these commands from a clean checkout of the `android-v1` branch.

```powershell
npm ci
$gta = 'G:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced'
.\bridge\Pack-BridgeV1.ps1 -GtaDirectory $gta
```

Extract the ZIP from `outputs\release` and open PowerShell in the extracted folder. Compare the ZIP's SHA-256 hash with the one printed by the packaging script. Install using the same Windows account you will use to run GTA V.

```powershell
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta -WhatIf
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta
node .\bridge-v1.mjs --host 127.0.0.1
```

Check the loopback port before connecting the phone. To test on a private network, open port 8788 by running `.\Enable-LanAccess.ps1 -Port 8788` in PowerShell as an administrator. Start the bridge on a private IPv4 address that the console can advertise and pair the phone. Run the bridge under the same Windows account as GTA V so both can exchange snapshots and commands in `%LOCALAPPDATA%\GTBikeVCompanion\v1`.

Android v1 uses port 8788 and its own pairing flow. The bridge stores Android tokens in the `bridge-v1` directory. Use the current PIN shown in the console to pair a new phone. Note the backup filename printed by the installer.

## Restore a previous version

Stop the bridge and close GTA V. Replace the example backup path with the exact path printed by the installer.

```powershell
$scripts = Join-Path $gta 'scripts'
$target = Join-Path $scripts 'GTBikeVCompanionV1.dll'
Copy-Item -LiteralPath 'EXACT-PATH-TO-BACKUP.bak' -Destination $target -Force
```

If this was the first v1 installation, move `GTBikeVCompanionV1.dll` out of `scripts`. Start your previous package if you had one. To remove the v1 firewall rule, run `.\Enable-LanAccess.ps1 -Port 8788 -Remove` as an administrator.

## P8a · Game map

Prepare a local directory with a `map.png` that you have permission to share on your LAN and a `calibration.json` file.

```json
{
  "version": 1,
  "source": "Source of your image",
  "rights": "Applicable permission",
  "sha256": "<lowercase SHA-256 of map.png>",
  "anchors": [
    { "x": 10, "y": 10, "px": 10, "py": 90 },
    { "x": 90, "y": 10, "px": 90, "py": 90 },
    { "x": 10, "y": 90, "px": 10, "py": 10 }
  ],
  "checkpoints": [
    { "x": 50, "y": 50, "px": 50, "py": 50 },
    { "x": 80, "y": 80, "px": 80, "py": 20 }
  ]
}
```

The numbers are a synthetic example for a 100 × 100 image. Replace them with three anchors spread across the map and at least two independent checkpoints. `x/y` are GTA world coordinates, and `px/py` are measured from the top-left corner of the PNG. Calculate the hash with `(Get-FileHash -LiteralPath .\map.png).Hash.ToLowerInvariant()`. Validation requires each checkpoint to be within 4 px of its projected position.

Start the bridge with `--map-directory PATH`. `GET /api/v1/map` requires a token and returns the position and active route points in normalized coordinates. A null point breaks the line or indicates that the current position is outside the map's coverage. Line segments connect waypoints from the mod. The client downloads the PNG with the same token from `GET /api/v1/assets/{assetId}`. Restart the bridge after changing the image or calibration. The in-game map still needs an authorized PNG and verification of the reference points in GTA.

## P8b/P8c · Leaderboard and advanced settings

Use `--enable-riders` to share the leaderboard for the current course or, during a free ride, the course suggested by the mod. `GET /api/v1/riders` reports the PC's multiplayer connection separately and distinguishes the local rider, other people and bots using the game's row types. Responses include display names and leaderboard readings. The `busiestRoute` suggestion requires at least one remote human rider on the course selected by the mod.

Remote joining is disabled. `JoinMultiplayerHandler` calls `LoadCourse`, which can wait for a download. Its event defers initialization to a later tick. A guard still needs testing to prevent a session change if a workout starts on the PC during that interval. Select the course from the GTA V menu.

Bot and traffic editing are also disabled. Settings are read from the mod's cache. `BotGang.CreateBotGang` reads the definition when it creates a group, and `ChangeCarsForBikes` captures its settings when constructed. Enabling P8c requires testing how changes take effect after a reload, with a review step, a backup and a distinction between the saved value and the one in use.

## P15 · Game options (GTBikeVConfig.ini)

`GET /api/v1/mod-config` returns an allowlisted subset of GTBike V's configuration file ([main] gameplay keys, [network] telemetry and multiplayer on/off), with `revision`, `gameRunning` and `editable` (contract: `contracts/v1/mod-config.schema.json`). `POST /api/v1/mod-config` with `{expectedRevision, options:{...changed fields}}` writes only those lines, keeps every other line as it was and stores a backup (last 10) in `bridge-v1/mod-config-backups`. It is refused with `GAME_RUNNING` while GTA V runs (process check or a fresh adapter), `REVISION_CONFLICT` if the file changed, `INVALID_REQUEST`/`VALUE_OUT_OF_RANGE` for anything outside the allowlist. Not a command and never through the adapter; changes apply when the game starts. The bridge finds the file from `--mod-config PATH` (the PC window passes the real Documents folder), else next to the Activities folder the adapter reported, else `%USERPROFILE%DocumentsRockstar GamesGTA VModSettings`. Arbitrary INI writes (`config.write`) stay forbidden.

The PC window authenticates with a per-run desktop token that only works from 127.0.0.1 and also reads `GET /api/v1/desktop/world` (raw GTA coordinates and heading for its own map, plus `teleport.reasonCode` and `destination` {reasonCode, point}).

## P16 · Teleport from the PC map

`player.teleport` `{x, y}` (GTA world metres, |x|,|y| <= 10000, `expectedRevision: null`) is accepted only with the PC window's desktop token; a paired phone gets `FORBIDDEN_SCOPE` (and sees the capability as unavailable with that reason). Allowed with the mod active, the game in control and no course loaded (`VALUE_NOT_ALLOWED` otherwise); one at a time (`BRIDGE_BUSY`). The adapter (`GtbikeV1Teleport.cs`, GTA natives only, nothing of GTBike V) freezes the rider or the bike they ride above the target, streams collision, puts them on the ground and on the nearest road within 60 m when there is one. Open water fails with `VALUE_NOT_ALLOWED` and no ground after about 12 s fails with `INTERNAL`; both put the rider back where they were. Evidence: the snapshot's `teleport` {requestId, status, x, y, z, snappedToRoad, reasonCode}; `applied` observes the final point (timeout 15 s).

## P17 · GPS destination from the PC map

`player.waypoint` `{x, y}` (same bounds as teleport, `expectedRevision: null`) sets GTA's own map waypoint (`SET_NEW_WAYPOINT`), so the game draws its GPS route on the minimap exactly as when a waypoint is chosen in the pause map; `player.clearWaypoint` `{}` removes it (`SET_WAYPOINT_OFF`; none active is an immediate no-op). Both are PC window only (`FORBIDDEN_SCOPE` for phones), need the game in control and are refused during a course (`VALUE_NOT_ALLOWED`: the course has its own guidance). The snapshot's `waypoint` {x, y, routeFound, routeLengthM} reports the active waypoint whoever set it; `GET /api/v1/desktop/world` exposes it as `destination.point`. Evidence: the waypoint within 5 m of the requested point, or no waypoint after a clear (timeout 5 s). The PC map draws the destination pin and a straight dashed guide; the road route itself is the game's GPS line.

## Diagnostics and acceptance checks

- Run `Diagnose-BridgeV1.ps1` to check the port, detected version and snapshot age. Omit the PIN and token when sharing the output. A listening port shows that a process opened it. Check that `/api/v1/hello` identifies the expected bridge.
- On the phone, verify the PC's identity, pairing, expiration, revocation, reconnection and IPv4 address changes. Record the Android version and the package's SHA-256 hash. For distribution, run these tests over HTTPS and verify that the app rejects HTTP and a certificate that presents a different key.
- With GTA V closed, check that the app reports the adapter as absent. With the game running, verify the state, metrics, routes and settings. Continue with the P3 through P8 checklists (`planning/android/P*_*.md`).
- If you enable P8, compare several map points with GTA and the leaderboard with the game's HUD. Test the reverse route, riding outside map coverage, losing the multiplayer connection and receiving a stale snapshot.
- Record a continuous 60-minute session with Wi-Fi changes, backgrounding and reconnection. Check that changes apply once and that the app identifies stale readings. Closing P9 also requires the signed APK and a review of both Android languages.
