# SimEffects Telemetry

Real-time telemetry, ultrawide/triple-screen rendering and live FOV/seat adjustment for emulated Gran Turismo games.
Phase 1 targets **Gran Turismo 4 Spec II** on **PCSX2**. GT5/GT6 (RPCS3) and the existing Forza work follow later.
See [REQUESTS.md](REQUESTS.md) for the brief.

## Status

For GT4 Spec II, all of these work today:

| Feature | What it does |
|---|---|
| Telemetry | Forza Data Out over UDP (Horizon or Dash format) for SimHub: engine, pedals, gear, body motion, suspension, slip and kerbs. Pausing sends "not driving" |
| Display modes | 16:9, 21:9, 32:9, 48:9 and 64:9 with the HUD rescaled, through PCSX2's per-game settings and cheat file (backed up first) |
| Camera | Live FOV and seat position for the cockpit and hood views. Hotkeys work while PCSX2 has focus (default Ctrl+Alt+arrows, PageUp/PageDown, = and −, and Home to reset; hold Shift for bigger steps). A read-out shows over the game while adjusting, and settings are saved per car and view |

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
src/GtTelemetry.Core   game-agnostic core: memory sources (PINE, PCSX2 process), scanning, sessions, adapters, voice
src/GtTelemetry.App    WPF app (SimEffectsTelemetry.exe), styled per ..\design.md
src/GtTelemetry.Cli    gtt.exe, the terminal companion used during discovery
sessions/              guided capture scripts (JSON, editable without rebuilding)
data/<game>/           address maps (addresses.json), the machine-readable source of truth
docs/                  architecture, PINE notes, scanning method, per-game memory maps
captures/              session results and raw snapshots (git-ignored)
tools/                 icon generator, window screenshot helper
```

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

PCSX2's PINE serves one client at a time, so close the app before using `gtt` against the game.

## Docs

- [Architecture](docs/architecture.md)
- [PINE protocol notes](docs/pine.md)
- [How scanning and guided capture work](docs/scanning.md)
- [GT4 Spec II memory map](docs/gt4-spec2/memory-map.md)
