# Architecture

## Goals that shape it

- Many games, one app: GT4 (PCSX2) now; GT5/GT6 (RPCS3), Forza Horizon 1–3 (Pinyon Shift recomp, native process) and
  Forza Motorsport 4 (Azimuth helper app) later.
- Features a game can't do are **shown as unavailable**, not hidden.
- Discovery is a first-class workflow: the scanning tools live in the product, not in throwaway scripts.

## Layers

```
GtTelemetry.App (WPF)                         GtTelemetry.Cli (gtt)
  game selector, connection, capture,           same operations, scriptable
  scanner, watch, memory view                   from a terminal
  (later: UDP, hotkeys, profiles, overlay)
            \                                   /
             GtTelemetry.Core
               Adapters   GameAdapter: id, platform, memory source kind, game IDs, feature support
               Memory     IMemorySource, ISnapshotSource, PineClient, Pcsx2ProcessMemory, Pcsx2Connection
               Scanning   ValueKind, BitSet, Kernels (specialised filters), ManualScanner, pointer search
               Sessions   SessionScript (JSON), SessionRunner (voice-guided narrowing and ranking)
               Addresses  AddressBook (data/<game>/addresses.json)
               Speech     IAnnouncer, SpeechAnnouncer (SAPI voice and cue tones)
               (M2+)      telemetry model, output formats (Forza Data Out first), UDP sender, camera control
```

### Memory sources

| Source | Used for | Notes |
|---|---|---|
| `PineClient` | Game info (title, ID, status), small reads, **writes**, telemetry polling | Batched: one round trip reads every telemetry field. The same protocol serves RPCS3 (GT5/6). |
| `Pcsx2ProcessMemory` | Full 32 MB snapshots for scans | Resolves the `EEmem` export in `pcsx2-qt.exe` and uses `ReadProcessMemory`: ~4 ms per snapshot. Optional. |
| `Pcsx2Connection` | Facade over both | Either half alone is enough to scan; PINE is needed for game details and preferred for writes. |

Future sources (RPCS3 PINE, a native process for the Forza recomp, a helper-app bridge for FM4) implement the same
`IMemorySource` / `ISnapshotSource` interfaces, so scanning, sessions and the watch list work unchanged.

### Adapters

`AdapterCatalog` declares every game with a support level per feature (`Available`, `Planned`, `Unavailable`). Each
adapter will also own its address and struct map (`data/<game>/addresses.json`), camera controls and display patches.

### Telemetry output (milestone 2, planned shape)

Adapter → `TelemetryFrame` (game-agnostic: RPM, speed, gear, inputs, lap data, motion, per-wheel) → `IOutputFormat`
(first: the Forza "Data Out" Dash packet, which SimHub reads natively) → UDP sender. Formats are pluggable.

## Stack

C#/.NET 8 WPF, matching Ross's other tools and the shared design guide. No third-party UI libraries. The only package is
`System.Speech`, for spoken instructions.
