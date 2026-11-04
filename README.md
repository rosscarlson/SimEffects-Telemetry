# SimEffects Telemetry

Real-time telemetry, ultrawide/triple-screen rendering and live FOV/seat adjustment for emulated racing games:
**Gran Turismo 4 Spec II** (PCSX2), **Gran Turismo 5 and 6** (RPCS3), and **Forza Horizon 2 and 3** (Azimuth). The game
is picked up automatically from whichever emulator is running it. See [REQUESTS.md](REQUESTS.md) for the brief.

## Status

| Game | Telemetry | Camera | Display |
|---|---|---|---|
| GT4 Spec II (PCSX2) | yes | FOV and seat | 16:9 to 32:9 (Game profile card) |
| GT5 (RPCS3) | yes | not yet | 16:9, 21:9 and 32:9 (Game profile card) |
| GT6 (RPCS3) | yes | not yet | 16:9, 21:9 and 32:9 (Game profile card) |
| FH2, FH3 (Azimuth) | yes | FOV and seat | any render size (Render Aspect Ratio) |

GT6 telemetry works on 1.00 and 1.05. RPCS3 advises not updating GT6 past 1.05, and each game update moves the data, so
newer versions aren't supported. GT5 telemetry works on 2.11 (and the earlier version it was first mapped on).

GT6's display modes widen the 3D view and keep the HUD 16:9 in the middle. Apply writes a GT6-only RPCS3 config
(config\custom_configs\config_BCUS98296.yml, backed up first): a copy of RPCS3's own settings with Stretch To Display Area
on, plus RPCS3's database fixes for GT6, which RPCS3 stops applying once a game has its own config (GT6 flickers without
them). The width itself is set in the game's memory each time GT6 starts, so the app has to be open.

GT5's display modes use RPCS3's community Aspect Ratio patch for GT5 2.11 (download it in RPCS3 with Manage > Game
Patches > Download Latest Patches). Apply turns the patch on with the chosen width in config\patch_config.yml and writes a
GT5-only RPCS3 config the same way as for GT6. Both are read when GT5 starts, so the app doesn't need to be open. The HUD
stays 16:9 in the middle.

Shared by every game: the telemetry output settings and live readout, the start/stop hotkey (a key or a controller
button, from anywhere), and one set of camera keys and buttons.

For GT4 Spec II:

| Feature | What it does |
|---|---|
| Telemetry | Forza Data Out over UDP (Horizon or Dash format) for SimHub: engine, pedals, gear, body motion, suspension, slip and kerbs. Pausing sends "not driving" |
| Display modes | 16:9, 21:9 and 32:9 with the HUD rescaled, through PCSX2's per-game settings and cheat file (backed up first). 48:9 and 64:9 aren't offered any more, but keep working if already set |
| Camera | Live FOV and seat position for the cockpit and hood views. Keys or controller buttons (wheel, button box, gamepad) work while PCSX2 has focus. Default keys are Ctrl+Alt+arrows, PgUp/PgDn, = and −, and Home to reset; hold Shift for bigger steps. A read-out shows over the game while adjusting, and settings are saved per car and view |

For Forza Horizon 2 and 3 on Azimuth (merged in from Azimuth Telemetry, which it replaces):

| Feature | What it does |
|---|---|
| Telemetry | Finds the player's car among AI and traffic by itself (**Next car** if it picks the wrong one) and sends RPM, speed, pedals, gear, wheel speeds, suspension and slip. Supports `forza_durango_release_final.exe` 1.0.0.64 (FH2) and FH3 1.0.125.0 on Azimuth 1.0.x |
| Controls tab | Azimuth's racing wheel bindings, rotation and force feedback, with a live picture of the wheel, pedals, buttons and shifter. Saves to `data\azimuth.ini` with Azimuth closed (the original is kept as `azimuth.ini.before-azimuth-telemetry`) |
| Camera | FOV in every driving view and the seat in the in-car views, each view its own, with the same camera keys as GT4. 1° and 1 cm a step, five with Shift. The only thing written to the game |
| Render Aspect Ratio | Any render size, e.g. 10240 × 1440 for 64:9 triple screens, through a small `dinput8.dll` shim next to `azimuth.exe` (source in `AzimuthSpan/`, built with the VS 2022 C++ Build Tools). **Clear** removes it |

How the Forza fields were found: [docs/azimuth/FINDINGS.md](docs/azimuth/FINDINGS.md). The first run carries Azimuth
Telemetry's settings over (folder, render size, camera changes, start/stop hotkey).

The address-finding tools are in the app with `--dev`, and in the CLI:

| Piece | What it does |
|---|---|
| PCSX2 connection | PINE (primary) plus fast full-RAM snapshots from PCSX2's exported `EEmem` (~4 ms per 32 MB) |
| Guided capture | Spoken, scripted driving sessions that narrow candidates automatically (`sessions/*.json`) |
| Scanner | First/next scan (unknown, equal, range, changed, increased …) over u8–u32, s8–s32 and f32, plus pointer search |
| Watch list | Live values at 20 Hz with min/max; save to the address map; write values |
| Memory view | Live hex/typed view of any 256-byte page with change highlighting |
| CLI (`gtt`) | The same core from a terminal: info, read/watch/write, snapshot, run sessions, pointers, mark, selftest |

## Layout

```
src/GtTelemetry.Core   game-agnostic core: memory sources (PINE, PCSX2 process, RPCS3), scanning, sessions, adapters,
                       voice; Azimuth/ is the Forza Horizon engine, camera and probe from Azimuth Telemetry
src/GtTelemetry.App    WPF app (SimEffectsTelemetry.exe), styled per ..\design.md
src/GtTelemetry.Cli    gtt.exe, the terminal companion used during discovery
src/GtTelemetry.Tests  xunit tests (the Forza packet, readers, car scanner and Azimuth controls)
AzimuthSpan/           the Render Aspect Ratio shim (C), embedded in the app
sessions/              guided capture scripts (JSON, editable without rebuilding)
data/<game>/           address maps (addresses.json), the machine-readable source of truth
docs/                  architecture, PINE notes, scanning method, per-game memory maps
captures/              session results and raw snapshots (git-ignored)
installer/             Inno Setup script for the installer (built by tools/release.ps1)
tools/                 icon generator, window screenshot helper, release script; tools/azimuth/ has the Forza probe analysis scripts
```

## Installing and updates

Download `SimEffectsTelemetry-Setup-X.Y.Z.exe` from [Releases](https://github.com/rosscarlson/SimEffects-Telemetry/releases)
and run it; it installs to `C:\Program Files\SimEffects\Telemetry`. Settings live in `%APPDATA%\SimEffectsTelemetry`,
so updating or reinstalling keeps them.

The **Updates** card (bottom left) checks the releases when the app opens and every six hours. It shows this version,
the latest one and how many releases behind you are; **Update** downloads the new installer, runs it and restarts the app.

To release: bump `<Version>` in `src/GtTelemetry.App/GtTelemetry.App.csproj`, commit and push, then
`powershell -File tools\release.ps1 -Publish -Notes "What changed"` (needs Inno Setup 6 and the `gh` CLI). That builds
`publish\installer\SimEffectsTelemetry-Setup-X.Y.Z.exe` and makes the `vX.Y.Z` release with it attached.

## Running

1. PCSX2 → Settings → Advanced → PINE Settings → **Enable** (slot 28011). Start the game.
2. `dotnet build`, then run `src\GtTelemetry.App\bin\Debug\net8.0-windows\SimEffectsTelemetry.exe`, or use the CLI:

```
gtt info
gtt sessions
gtt run throttle --save-snapshots
gtt watch 0x00123450:f32 0x00123454:u8 --hz 20
gtt mark throttle 0x00123450 f32 confirmed "raw pedal" --units "0-1"
gtt camera          # current camera view, seat and FOV
gtt selftest        # synthetic checks of sessions, packets, display modes and the camera; no emulator needed
```

`dotnet test` runs the Forza tests. Building the app needs the Visual Studio 2022 Build Tools (C++) for the shim.

PCSX2's PINE serves one client at a time, so close the app before using `gtt` against the game.

## Docs

- [Architecture](docs/architecture.md)
- [PINE protocol notes](docs/pine.md)
- [How scanning and guided capture work](docs/scanning.md)
- [GT4 Spec II memory map](docs/gt4-spec2/memory-map.md)
- [Forza Horizon 2 and 3 findings](docs/azimuth/FINDINGS.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md) (the Forza packet encoder is ported from Pinyon Shift, BSD 3-Clause)
