**EN** | [ES](CAMERA_CONTROL_ES.md)

# P19: Cameras, Menu, and Help in Android/PWA

Implementation from November 8, 2026. API v1, minor **15**. Android and PWA share the same endpoints, capabilities, command queue, and Bridge operations. No additional transport or generic keyboard API was added.

## Evidence and Technical Decisions

- The [official GTBike V configuration](https://www.gtbikev.com/content/configuration/) documents `MenuKey` (F5), `NextCamKey` (`ZP_Left_Joy_Btn(U)`), and `LookBehindKey`. The decompiled `GTBikeVScript.cs` 0.8.0.4 snippet shows that `NextCamKey` executes `Game.SetControlValueNormalized(0, 1f)`. **Next view** reuses this exact control pulse for a single frame.
- The same snippet demonstrates that F5 toggles `Menus.Visible` and sets `MenuAutoOpenStatus = false`. When transitioning from inactive to active, the adapter triggers the native closure `Menus.CloseMenus()` and resets that flag to `false`. It performs no unnecessary actions when receiving an already matched state or upon deactivation. **This implements the F5 hide effect; it does not inject a physical OS-level F5 keystroke.** No generic key-sending mechanism existed to reuse across the inspected repositories. Explicit closure prevents accidentally opening an already hidden menu and properly closes submenus.
- Mod source analysis shows that `cameraRelativeHeading != 0` forces the mod to impose camera yaw on every frame. This field is read, without modification, from the same script instance already resolved by native menu actions. When `LookBehindKey` is active, Companion camera rotation is disabled or cancelled. If the field cannot be inspected, camera look rotation is not enabled.
- GTA exposes [five camera view values](https://github.com/scripthookvdotnet/scripthookvdotnet/blob/main/source/scripting_v3/GTA/Camera/CamViewMode.cs) and a [dedicated OnBike context](https://github.com/scripthookvdotnet/scripthookvdotnet/blob/main/source/scripting_v3/GTA/Camera/CamViewModeContext.cs). Direct selection uses [GET_CAM_VIEW_MODE_FOR_CONTEXT](https://github.com/citizenfx/natives/blob/master/CAM/GetCamViewModeForContext.md) and [SET_CAM_VIEW_MODE_FOR_CONTEXT](https://github.com/citizenfx/natives/blob/master/CAM/SetCamViewModeForContext.md) with context `2`, rather than the default automobile context. Cinematic mode uses [SET_CINEMATIC_MODE_ACTIVE](https://github.com/citizenfx/natives/blob/master/CAM/SetCinematicModeActive.md).
- Orientation uses [GameplayCamera.RelativeHeading and RelativePitch](https://github.com/scripthookvdotnet/scripthookvdotnet/blob/main/source/scripting_v3/GTA/Camera/GameplayCamera.cs). Angles interpolate over 450 ms along the shortest angular arc; each rotation job finishes or expires within two seconds. It reuses the adapter per-frame host. It does not hold keys down, does not create script cameras, and does not alter steering, braking, power, or resistance.

## New Contract

`GET /api/v1/camera`, authenticated with existing bearer token:

```json
{
  "available": true,
  "reasonCode": null,
  "modes": ["near", "medium", "far", "firstPerson", "cinematic"],
  "currentMode": "medium",
  "headingDeg": 30,
  "pitchDeg": 0,
  "canLook": true,
  "lookReasonCode": null,
  "busy": false
}
```

All three commands use the standard `POST /api/v1/commands` envelope: fresh UUID `requestId`, observed `adapterInstanceId`, and `expectedRevision: null`.

| Type | `args` | Effect |
| --- | --- | --- |
| `camera.next` | `{}` | One-frame pulse of the same control as NextCamKey. |
| `camera.setMode` | `{"mode":"far"}` | Direct selection of a view mode advertised by the PC. |
| `camera.look` | `{"headingDeg":90,"pitchDeg":-15}` | Smooth camera look; horizontal between -180° and 180°, vertical between -45° and 45°. |

New capabilities: `camera.read`, `camera.next`, `camera.setMode`, `camera.look`. New error code: `CAMERA_CONTROL_IN_USE`.

The `applied` status requires an adapter acknowledgment tagged with the same `requestId` and matching observed mode/angles read back; a simple setter call or basic ACK is insufficient. Repeated requests retain existing deduplication behavior. Client apps never automatically retry commands after a disconnection.

Prior clients remain fully backward-compatible: `/state` payload structure and pairing protocols were not altered. A newer client connecting to an older bridge indicates that the PC component needs an update. View lists and selections originate from the PC; unrecognised mode identifiers are rejected.

## UI and Preferences

- On the Ride screen: observed view mode, Next view button, view mode picker, left/right look buttons, orientation/elevation sliders, and Center camera button. Sliders dispatch when released. Touch buttons adhere to minimum 48 dp/px targets and wrap across multiple rows on narrower viewports.
- Near, Medium, and Far views support look rotation. First-person and Cinematic views preserve their native orientation; unverified orbit controls are not advertised for those views.
- Contextual help icons start hidden by default. In More → Help, a toggle **Show help icons** is available. Android persists `show_help_icons` via DataStore; PWA persists `showHelpIcons` alongside existing local settings. Both values remain local to their respective client.
- All help topics remain accessible by tapping the topic title in the Help section. Re-enabling help icons restores the inline question-mark buttons and dialogs.

## Validation and Boundaries

The protocol was verified against a mock adapter, the C# controller against simulated camera surfaces, menu hiding against the GTBike V runtime model, and the PWA in responsive browser viewports down to 390 px width. The adapter was built against the installed `ScriptHookVDotNet3.dll`, and updated APK and installer builds were verified.

**In-game GTA V boundaries:** Specific view behaviors in individual modded environments (especially Cinematic view), rotation direction, and physical camera occlusion limits imposed by GTA V. If another script camera takes exclusive control, camera context changes, character control is lost, or the smart trainer triggers `LookBehindKey`, Companion camera controls yield or cancel the pending interpolation. External script cameras are not forcefully overridden.

## Update and Manual Verification

1. Close GTA V and update the PC with the generated installer. Restart the Bridge so it advertises API minor 15. The PWA runs from its own repository; restart its server and refresh the browser.
2. Install the debug APK generated with the same signing key as the existing build.
3. Activate the mod from either app: verify that in-game menus close and that re-asserting the active state does not toggle them open.
4. On a bicycle, compare Next view with the physical controller `NextCamKey`; switch views using the in-game key and verify that both companion apps mirror the change.
5. Test Near, Medium, and Far views, rotation, elevation, and centering. Trigger `LookBehindKey` during rotation: Companion must yield control cleanly. Verify First Person and Cinematic views: rotation must be disabled there.
6. Disconnect the phone or pause GTA V: no input must remain latched. Reconnecting must not repeat the previous command.

To compile and install the adapter only, with GTA V closed:

```powershell
Set-Location 'G:\develop2\games\GTBikeV\gtav-riding-companion-bridge'
.\bridge\Build-GameAdapterV1.ps1 -GtaDirectory 'G:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced' -Install
```

Reproducible test commands:

```powershell
# From gtav-riding-companion-bridge:
npm test
.\bridge\Test-Camera.ps1
.\bridge\Test-GameAdapterV1.ps1

# From gtav-riding-companion-android:
.\gradlew.bat :core:test :bridge-client:test :app:testDebugUnitTest :app:assembleDebug :app:lintDebug :app:compileDebugAndroidTestKotlin

# From gtav-riding-companion-pwa:
npm test
npm run build
```
