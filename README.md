# Liftoff FPV Goggles

Turn a VR headset into a pair of FPV goggles for **Liftoff: Micro Drones**.

A BepInEx plugin that sits alongside [UUVR](https://github.com/Raicuparta/uuvr). UUVR gets the
game into a headset; this plugin makes it behave like goggles rather than like VR.

## Why

In VR, the game camera follows your head. Real FPV goggles do not: they are a screen strapped
to your face showing whatever the drone's camera sees. Turning your head changes nothing.

That difference matters. Head tracking gives you a way to look around that you will not have
on the field, and it quietly makes the sim easier than the thing it is simulating.

## What it does

* **No head tracking.** Rotation and position are ignored. The picture is locked to the drone.
* **Menus stay flat.** VR is held off until you are in a flight, because Liftoff's menu is
  unusable in VR (see [Findings](#findings)). Everything up to the start of the flight behaves
  as if no mod were installed.
* **Resizable HUD.** In a VR flight the game's HUD moves onto a plane you can size and
  position, instead of being smeared across the whole field of view.
* **Betaflight-style artificial horizon.** A rolling bar with a fixed centre mark, with
  compensation for the FPV camera's upward tilt — the way a real OSD behaves.
* **Hide HUD elements.** Drop the crosshair, the stick indicators, the recording icon, or the
  entire HUD, by name.

Optional, off by default:

* **Goggle mask** — a black border cutting the picture down to a real goggle's field of view.
* **Virtual screen** — renders the drone camera at a real lens FOV (120°+) onto a small flat
  screen, so a wide angle image is squeezed onto a goggle-sized display. The most faithful
  option, and the least comfortable.

## Requirements

| | |
|---|---|
| Game | Liftoff: Micro Drones (tested against build 1.1.1, Unity 2022.3) |
| Mod loader | BepInEx 5 (x64, Mono) |
| Dependency | UUVR 0.4.0 (`raicuparta.uuvr-modern`) — hard dependency, the plugin will not load without it |
| Easiest setup | [Rai Pal](https://github.com/Raicuparta/rai-pal) installs both for you |

Tested with a Meta Quest 2 over Link and SteamVR (UUVR set to OpenVR).

## Installation

1. Install UUVR for Liftoff, most easily through Rai Pal.
2. Download the release ZIP and extract it into the `BepInEx` folder Rai Pal created:
   `%APPDATA%\raicuparta\rai-pal\data\installed-mods\<id>\bepinex\BepInEx\`
3. Start the game once so the config file is written.

You should end up with `BepInEx\plugins\LiftoffFpvGoggles\LiftoffFpvGoggles.dll`.

> Rai Pal overwrites the plugins folder when it reinstalls UUVR. If the plugin disappears,
> just extract it again.

## Flying

1. **Start the game.** It runs flat, VR is off. Menus behave normally.
2. **Pick a track and start the flight.**
3. **Press `F3`.** VR switches on with the view locked to the drone.

`F3` is UUVR's own key, not this plugin's. Leaving a flight switches VR back off so the menus
stay usable.

## Hotkeys

Keys are read through `GetAsyncKeyState`, the same way UUVR does it, so they work regardless of
the game's input system and with the headset on.

**The game receives the same key press.** Anything Liftoff binds itself will do both things at
once — `F4`, `F5`, `F11` and `F12` toggle Liftoff's HUD and are deliberately left free here.

| Key | Action |
|---|---|
| `F3` | VR on/off (UUVR's key) |
| `F7` / `F8` | HUD plane smaller / larger |
| `Home` / `End` | Move HUD left / right |
| `Page Up` / `Page Down` | Move HUD up / down |
| `Insert` / `Delete` | Horizon indicator larger / smaller |
| `F9` | Head tracking on/off, for comparison |
| `F10` | Goggle mask / virtual screen on/off |

`F9` and `F10` apply to the running session only and are never written to the config file — a
quick A/B comparison during one flight should not silently become the permanent setting.

All goggle keys are ignored in menus, where you could not see their effect anyway.

## Configuration

`BepInEx\config\maxwo.liftoff.fpvgoggles.cfg`, written on first start. Every key can be
rebound there; arrow keys, function keys, the numeric keypad and the navigation block are all
available.

### General

| Setting | Default | Meaning |
|---|---|---|
| `Keep VR Off In Menus` | `true` | Holds VR off in menus so they behave as if unmodded. |
| `Menu Scene Names` | `menu,splash,lobby,loading` | Comma separated. A scene counts as a menu if its name contains any of these. Everything else is a flight. |

Scene detection is the hinge everything hangs off: VR on/off, the HUD capture mode, and whether
the goggle keys do anything. The active scene name is logged on every change, so unusual game
modes can be added to the list.

### Head Tracking

| Setting | Default | Meaning |
|---|---|---|
| `Disable Head Rotation` | `true` | The core setting. |
| `Disable Head Position` | `true` | Leaning no longer shifts the camera. |

### HUD

| Setting | Default | Meaning |
|---|---|---|
| `HUD On VR Plane In Flight` | `true` | Puts the HUD on UUVR's UI plane so it can be resized. |
| `Hide Elements` | *(empty)* | Comma separated object names to hide. Exact match, case insensitive; add `*` for substring matching. |
| `Dump HUD Tree Key` | `None` | Writes the HUD hierarchy to the log so you can find names. Bind a key if you need it. |

Useful names in Liftoff: `Center` (the crosshair), `ArmedDisplay` (crosshair plus drone name),
`AxisVisualizer` (the stick crosses), `RecordingIndicator`, `XSDroneHUD` (everything).

> Elements that only appear once the drone is armed are only in the dump if you dump **while
> armed**. The crosshair is one of them.

### Horizon

| Setting | Default | Meaning |
|---|---|---|
| `Enable Horizon` | `true` | Betaflight-style artificial horizon. |
| `Camera Tilt` | `0` | Upward tilt of the FPV camera in degrees, as set in Liftoff. |
| `Pitch Range` | `30` | Degrees of pitch from centre to the top edge. Larger = calmer. |
| `Scale` | `1` | Master size. Also scales how far the bar travels. |
| `Bar Width`, `Centre Gap`, `Line Thickness` | | Shape of the indicator. |

`Camera Tilt` deserves a note. A real Betaflight OSD draws the **craft's** attitude and knows
nothing about how the camera is angled. Set this to your in-game camera angle and the bar
behaves the same way: centred when the drone is level, and therefore *not* sitting on the
visible horizon. Leave it at `0` if you would rather have it line up with what you see.

### Goggle Mask *(optional, off)*

A black border restricting the picture to a real goggle's field of view. Default 46° diagonal
at 4:3 — a Skyzone SKY04O Pro on analog video.

Be clear about what this does: it cuts a hole into a picture still rendered at **headset**
field of view. You see less of the world, at the same scale. It does not reproduce the way a
wide angle lens is squeezed onto a small screen.

### FPV Screen *(optional, off — needs `Enable Mask` as well)*

That squeeze, for real. A separate camera renders the world from the drone camera's pose at a
configurable lens FOV into a 4:3 texture, shown on a flat head-fixed screen at goggle FOV. The
VR camera then renders nothing but that screen on black.

| Setting | Default | Meaning |
|---|---|---|
| `Camera Lens FOV` | `120` | Diagonal FOV of the drone camera. Typical FPV cameras are 120–150. |
| `Capture Height` | `720` | Render resolution. Analog video is around 480 lines. |

This is the most faithful mode and the one most people turn back off. Worth trying once.

## Findings

Notes from getting this to work, in case they save someone else the same afternoon.

### Liftoff destroys injected GameObjects

The log says `Custom code injection detected`. The BepInEx manager object a `BaseUnityPlugin`
lives on is not spared, so the whole `Update()` loop dies at startup — while Harmony patches
keep working, because they are static. From the outside it looks like half the mod silently
does nothing.

UUVR solves this in `UuvrCore.OnDestroy()` by recreating itself, and so does this plugin. Two
corollaries, both learned the hard way:

* **Do not use `ConfigFile.SettingChanged`.** The plugin component gets destroyed, its
  `OnDestroy` dutifully unsubscribes, and after that nothing notices config changes. Poll
  instead.
* **Do not cache objects from UUVR.** Its core rebuilds constantly. A held `VrToggler` becomes
  a dead object whose state disagrees with the live one — which made `F3` toggle backwards.

### The VR menu cannot be fixed from here

| | Resolution | Aspect |
|---|---|---|
| Game window | 1920 × 1080 | 1.78 |
| XR eye texture | 2544 × 2564 | 0.99 |

UUVR's capture camera renders into a near-square target while the menu is laid out for 16:9, so
only part of it is captured. **Scaling does not help** — the crop scales with it, because it
happens at capture time. Head movement does not help either: UUVR pins the menu plane to the
camera via `FollowTarget`.

Hence holding VR off in menus entirely. `UuvrCore` reads `KeyCode 114` (`VK_F3`) and calls
`VrTogglerManager.ToggleVr()`; VR is switched back off while the scene is a menu, and left
alone during a flight.

### The HUD capture mode has to switch automatically

| Mode | Effect |
|---|---|
| `None` | No UI on the VR plane |
| `Mirror` | A copy of the **whole screen** on the plane, sitting on top of the actual world |
| `CanvasRedirect` | Only the canvases — the HUD, resizable |

`CanvasRedirect` is right during a flight but must not stay on outside one: it re-parents the
canvases to UUVR's capture camera with `RenderMode.ScreenSpaceCamera`, and when that camera is
not rendering the menu becomes invisible — on the flat screen too. So the plugin sets it
itself: `CanvasRedirect` only in a VR flight, `None` everywhere else.

### Odds and ends

* UUVR's `Camera Position Offset X` can be written but has no effect;
  `VrCameraOffset.OnSettingChanged` never sees the event. Applying it straight to the transform
  did not visibly help either, so shifting the whole view was dropped.
* UUVR forces a software cursor (`Cursor.SetCursor(..., CursorMode.ForceSoftware)`). `Mirror`
  captures it because it copies the screen; `CanvasRedirect` does not, because a software
  cursor is not a canvas.
* Liftoff uses Rewired, so `UnityEngine.Input` is not an option for hotkeys.

## Known limitations

* **Tied to UUVR internals.** Several settings are driven through reflection into UUVR's
  `ModConfiguration`. A UUVR update can break this. Tested against 0.4.0.
* **Liftoff specific.** Scene names and HUD element names come from this game.
* **The desktop window is black while VR is on.** That is UUVR. It does not matter in the flow
  above, since you only need the window when VR is off anyway.
* Only tested on Windows, with SteamVR and a Quest 2.

## Building

```powershell
.\build.ps1
```

No .NET SDK required — it uses `csc.exe` from the .NET Framework that ships with Windows. That
compiler only speaks **C# 5**: no `nameof`, no string interpolation, no `?.`.

Game and BepInEx folders are detected automatically (Steam library folders and Rai Pal's mod
directory). Override them if needed:

```powershell
.\build.ps1 -GameDir "D:\Games\Liftoff Micro Drones" -BepInExDir "C:\...\bepinex\BepInEx"
```

The script also writes `Local.props`, which [LiftoffFpvGoggles.csproj](LiftoffFpvGoggles.csproj)
imports — so `dotnet build` and IntelliSense work without machine specific paths in version
control. With the SDK you get modern C# instead of C# 5.

`.\package.ps1` builds and packs a release ZIP into `dist\`.

### Layout

| File | Contents |
|---|---|
| [src/FpvGogglesPlugin.cs](src/FpvGogglesPlugin.cs) | Settings, Harmony patch, key codes |
| [src/FpvGogglesRunner.cs](src/FpvGogglesRunner.cs) | Everything per frame: VR control, scenes, hotkeys, mask |
| [src/HorizonIndicator.cs](src/HorizonIndicator.cs) | Artificial horizon |
| [src/FpvScreen.cs](src/FpvScreen.cs) | Optional virtual screen |

One build note worth knowing: the plugin is compiled against the **game's** assemblies
(.NET Standard 2.1), not the compiler's framework — hence `/nostdlib+` plus Unity's `mscorlib`
and `netstandard`. Without it the build fails with `CS0012: System.Object ... netstandard 2.1`.
The resulting DLL referencing mscorlib in two versions is normal, not a bug: the 2.0.0.0
references come from Harmony's and BepInEx's signatures, and Mono resolves by simple name.

## Credits

Built on [UUVR](https://github.com/Raicuparta/uuvr) by Raicuparta, which does the actual work of
getting a flat Unity game into a headset. This plugin only bends it towards FPV.

## License

MIT — see [LICENSE](LICENSE).
