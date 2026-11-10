# NFSU2 (nfsu2.xyz): Phase 0 findings

Target: Need for Speed: Underground 2 as nfsu2.xyz serves it (Underground 2 Revived, a newer private build of
th0mk/nfsu2-web), in Chrome, played online on the public servers, on 2026-11-09. A Fanatec CSL DD and SimJack
pedals were the controls.
Everything was found live, read-only, through the extension (`extension/nfsu2/`) and `gtt nfsu2 serve`, with the
scripts in `tools/nfsu2/`. The game's x86 code is mapped in the page's memory at its original addresses, so the
effect code was disassembled from there with capstone; `SPEED2.EXE` wasn't needed. The disassembly stays in
`captures/` (git-ignored) and isn't committed.

## Summary

| Question | Answer |
|---|---|
| Does the extension get the game's memory? | **Yes.** In Chrome the page makes the shared `WebAssembly.Memory` (2048 MB here); the wrapped constructor catches it |
| Does the spoof turn the game's FFB on? | **Yes.** The page sets `IM_PAD_RUMBLE = 1`, and rumble levels flow back through the fake `playEffect` |
| Wheel effects or a pad's rumble? | **Wheel effects:** spring, damper, constant force, square and sine |
| FFB option 1: the game's own `DIEFFECT`/`DICONDITION` structs | **No-go as structs:** they're built on the stack for each call (`0x5c0200`: `sub esp, 0x5c`, `dwSize 0x38`, `CreateEffect` = vtable `+0x48`). **Go** for the game's own parameter cache (below), which has the condition values |
| FFB option 2: the runtime's `g_eff[64]` | **Go:** found by signature every time; stride 80 as in the archive. Gives kind, playing, magnitude, period, gain, duration, envelope |
| Left/right | **Signed constant-force magnitude**, −10000…+10000, changed in place (`SetParameters`) on the same effect. Which sign is left is to be checked on the wheel in milestone 3 |
| Read speed over the bridge | ~0.44 ms a round trip (2277 reads/s) |
| Player table and car offsets | **Confirmed** on the live build (below) |
| Chrome still reads the wheel while the app holds it for FFB | Not tested yet: needs the FFB player (milestone 3). Chrome reads controllers through Raw Input/HID, which DirectInput's exclusive mode doesn't block, so it's expected to work |

Go for Phase 1: telemetry from the known addresses, FFB from `g_eff` (what is playing, kinds, periodic and constant
values) plus the game's parameter cache (spring and damper conditions).

## How the extension reaches the game

- Guest addresses are offsets into the page's memory. The runtime's own data starts at `0x28000000`
  (`runtime/guest_span.h`).
- Every controller without a `vibrationActuator` gets a fake one (all 8 of Ross's devices showed as spoofed). The
  game decides force feedback once, when it first asks with motors present, so the extension has to be loaded before
  Play.

## The effects the game makes

`g_eff` was at `0x286dfa50` in this session (runtime data, so it can move between builds; `find_effects.py` finds it:
an entry's `obj` points to the guest effect object, whose `+4` holds the slot + 1).

| Slot | Kind | Seen |
|---|---|---|
| 0 | spring | Playing while stopped or paused, stopped while driving |
| 1 | damper | Playing while driving, condition coefficient 0 in this race |
| 3 | constant | Always playing; the steering force, −10000…+10000, about 10 changes a second at a steady corner and every frame in quick changes |
| 2, 5 … | square, period 50 ms (20 Hz) | Magnitude ~570–1570; the game changes it by `Unload` + a new `CreateEffect`, so dozens of unloaded entries pile up |
| 4, 6 … | sine, period 50 ms | As square, alternating with it |

Each entry (80 bytes): `+0` obj, `+4` kind (0x20 constant … 0x27 spring, 0x28 damper), `+8` duration µs
(0xFFFFFFFF infinite), `+12` gain, `+16` start delay, `+20` magnitude, `+24` offset, `+28` period µs, `+32` envelope
on, `+36..+48` attack level/time and fade level/time, `+52` playing, `+56` iterations, `+64` start time (double),
`+72` unloaded.

## The game's own parameter cache

The game's FFB manager skips a call when nothing changed, so it keeps the last values of every effect. The manager
is on the heap; a static pointer to it is at **`0x00870760`** (`0x08655560` this session). Per device slot `s` (0 for
the wheel):

| Offset | What | Set in | This race |
|---|---|---|---|
| `+0x2498` | effect pointers: spring `[+0x10]`, damper `[+0x14]` | | |
| `+0x24e8` | effect pointers: constant `[+0x10]` | | |
| `+0x2538` | effect pointers: periodic | | |
| `+0x2ba0 + 12s` | spring offset | `0x5c9150` | 0 |
| `+0x2ba4 + 12s` | spring coefficient | `0x5c9150` | 10000 |
| `+0x2ba8 + 12s` | spring saturation | `0x5c9150` | 10000 |
| `+0x2bb8 + 8s` | constant force magnitude | `0x5c9380` | follows `g_eff` exactly |
| `+0x2bc8 + 4s` | damper coefficient | `0x5c9590` | 0 |
| `+0x2b90`, `+0x2b98` | periodic (to decode) | `0x5c9770` | |
| `+0x2be0`, `+0x2be8`, `+0x2bf0`, `+0x2bf8` | periodic effects' levels (to decode) | `0x5c9810` … `0x5c9d9c` | 0 |
| `[0x0086ffb4 + 4s]` | the game's DirectInput device | | |

Still open: how the spring and damper coefficients change with speed (only one race on a walled track so far), and
which periodic cache is which effect.

## Telemetry addresses (confirmed)

| What | Address | Seen |
|---|---|---|
| Game flow | `0x008654A4` | 6 in the race |
| World step | `0x00865154` | 0.0083 s (120 fps) driving, 0 paused |
| Player 1 | `[0x00D00000]` | `0x0a0b2e20` |
| Car | `[player + 4]` | `0x093f1aa0` |
| Velocity | car `+0x70/+0x74/+0x78` | (−14.8, −2.8, 0.3) m/s |
| Forward speed, speed | car `+0x42C`, `+0x430` | 15.09, 15.10 m/s |
| Gear | car `+0x434` | 3 |
| RPM | car `+0x440` | 3449 |
