# How the telemetry fields were found

Target: `forza_durango_release_final.exe` 1.0.0.64 (PE timestamp `565501ED`), the build fh2wheel
targets. Azimuth maps it at its preferred base `0x140000000`, so every address below is static.
All found by static analysis with `tools/pe_an.py` (Python + capstone + pefile); no Ghidra needed.

## The way in: the game's own Telemetry HUD

FH2 ships a telemetry overlay (strings `TelemetryViewModel`, `TireViewModel`,
`SuspensionViewModel`, `IDS_HudOptionValue_TelemetryOn`). Its properties are registered by name
with the game's reflection system (`RPM`, `SteeringAngle`, `BrakeMeter`, `HandBrakeMeter`,
`AccelMeter`, `ClutchMeter`, `EngineTorque`, `EnginePower`, `ClutchState`, `Boost`, `GForce`; per
corner `Wear`, `Friction`, `Camber`, `Pressure`, `Temperature*`, `Travel`).

- The `RPM` property getter (`0x140c2c170`) reads `TelemetryViewModel+0x1D8`.
- The function that fills `+0x1D8..+0x224` is `0x140c3e5c0`. It takes the car with
  `vm[0xC8]` (a `ForzaCar`) `+0x20 -> vfunc 0x70`, then calls that interface's virtual getters:
  `0x1A0` RPM, `0x910` steering, `0x158` inputs, `0x8D8` clutch, `0x3A0` torque, `0x3E0` power,
  `0x8D0` clutch state, `0x410/0x420/0x3B0` boost.
- That interface is **`ICarDynamics`**, the sub-object at `+0x3A0` of **`CCarDynamics`** (MSVC
  RTTI). Its vtable `0x142EB4BB8` has 381 entries; the getters are mostly one-liners, which gives
  the field layout directly (`tools/icardyn_vtable.txt` lists them all).

## Finding the object at runtime

`CCarDynamics` lives on the heap, but its vtables are static: primary `0x142EB4B08` at `+0`, and
`0x142EB4BB8` at `+0x3A0` (or `0x142EB5D68` / `0x142EB5E18` for the
`TRefCountedObjectThreadSafe<CCarDynamics>` variant). Scanning writable memory for that pair finds
every car, each session, with no pointer chains. Fingerprint check: vtable slot `0x142EB4BB8+0x1A0`
must be `0x1412C81A0`.

## Field map (offsets from the ICarDynamics pointer = CCarDynamics + 0x3A0)

| Offset | Field | Source getter (vtable entry) |
|---|---|---|
| `+0x008` / `+0x058` | steering angle / max (HUD divides) | 290 (`0x910`) |
| `+0x148` | engine speed, rad/s (x9.5493 = RPM) | 52 (`0x1A0`), HUD RPM |
| `+0x18C` | engine torque | 116 (`0x3A0`) |
| `+0x194` | engine power | 124 (`0x3E0`) |
| `+0x1CC` | engine max speed, rad/s (HUD clamps RPM to it) | 102/103 |
| `+0x5C0` | engine idle, rad/s (`(cur-idle)/(max-idle)` = normalised RPM) | 104/105 |
| `+0xA8C` | clutch 0-1 | 283 (`0x8D8`) |
| `+0xAA0` | gear (byte), indexes the ratio table | 51, 245 |
| `+0xABC` | number of gears | 222, 328 |
| `+0xAC0` | gear ratios, 12-byte stride | 244 |
| `+0xB90` | final drive | 286 |
| `+0xDF8` | inputs: `+0` throttle, `+4` brake, `+0x10` handbrake | 43 (`0x158`) |
| `+0xEF0` | world position (vec4) | 144 |
| `+0x11B0 + i*0x4B0` | per-wheel blocks (4 wheels) | 48, 59, 68, 140-218 |
| wheel `+0x0C` (`+0x11BC`) | wheel spin, rad/s | 152/153 |
| wheel `+0x490` (`+0x1640`) | pointer to tyre data; `+8` = diameter in inches | 143 |
| `+0xE28` | speed, m/s (always positive) | 84 (`0x2A0`) |
| `+0x4EE7` | byte, 1 = player's car (0 on all 44 AI/traffic cars in the probe) | 377 |
| `+0xFBC` | int, 1 = AI-controlled (0 on the player's car) | setter 20 |

## Labelled from the probe drive (2026-10-29)

`dist/probes/probe-20261029-111935.bin`, analysed with `tools/extract_probe.py` + `tools/pa.py`.
Wheel order is FL, FR, RL, RR (rears spin up on the RWD test car; the outer wheel spins faster
in a turn). Offsets within each `0x4B0` wheel block:

| Wheel offset | Field | Evidence |
|---|---|---|
| `+0x000` | suspension position, m (more negative = extended) | fronts rise, rears drop under braking; range inside droop..bump |
| `+0x00C` | wheel spin, rad/s | negative in reverse |
| `+0x024` | slip angle, rad | 0 in straight braking, ~0.007 cruising, 0.3-0.5 on the rears in a handbrake slide, sign follows the slide |
| `+0x2C4` | contact-patch longitudinal velocity, m/s | equals spin x radius within 0.4 % at cruise |
| `+0x3C0` | tyre radius, m | 0.3216 front, 0.3179 rear |
| `+0x3F4` | suspension position at full droop | -0.181 |
| `+0x3FC` | suspension position at the bump stop | -0.0744 |
| `+0x484` | suspension position at rest | -0.1355 |

Gear: raw `0` is reverse (ratio table entry 0 is negative; the game auto-selects it when the brake
is held at a stop), `1..n` forward, `+0xABC` counts reverse too. Steering: -1 full left, +1 full
right. Not fields: `+0x2C8` looks like slip-velocity magnitude (it spikes in straight braking);
`+0x388..+0x394` exist only on the front wheels (steering related).

Derived in `CarReader`: slip ratio = `(spin x radius - v_long) / max(|v_long|, 2)`, and both slips
are divided by nominal peaks (0.10, 0.14 rad) so 1 is roughly the limit of grip.

## Still open

- Surface rumble: no per-wheel field found yet (SimHub's road effects work from suspension).
- Torque/power units, boost, tyre temperatures (the HUD has them).

# Forza Horizon 3 (Xbox One build in Azimuth)

Target: Xbox One `forza_durango_release_final.exe`, Opus 1.0.125.0 (PE timestamp `5B16ECCA`),
also mapped at `0x140000000`. Same engine, so the same way in, but much faster: the class names
survive, and `CCarDynamics` still has an `ICarDynamics` sub-object, now at `+0x4B0` with 474
vtable entries (`tools/fh3_icardyn_vtable.txt`, from `tools/vtdump.py`).
`tools/vtalign.py` lines FH2's 381 getters up with FH3's by instruction shape (difflib
alignment of normalised disassembly); most one-line getters match exactly, so each FH2 field's
FH3 offset can be read straight off the matching getter. `tools/vtwheel.py` does the same for
the per-wheel getters.

- Vtables: `0x144680EA0` / `0x144680F30`, and `0x144682578` / `0x144682608` for
  `TRefCountedObjectThreadSafeWithDeletionInfo<CCarDynamics>`.
- Fingerprint: ICarDynamics vtable `+0x1B8` (the clamped RPM getter, FH2's `+0x1A0`) =
  `0x141D3FE10`. The HUD's own properties are now observable objects, so the getter chain used
  for FH2 doesn't apply; the vtable match does the job.

| Field | FH2 | FH3 | FH3 getter (vtable entry) |
|---|---|---|---|
| steering / max | `+0x008` / `+0x058` | same | 359 |
| engine rad/s | `+0x148` | same | 55 |
| torque / power | `+0x18C` / `+0x194` | same | 143 / 151 |
| max engine rad/s | `+0x1CC` | `+0x1D4` | 55, 130, 132 |
| idle rad/s | `+0x5C0` | `+0x5C8` | 131, 132 |
| clutch | `+0xA8C` | `+0xA94` | 352 |
| gear (byte) / count | `+0xAA0` / `+0xABC` | `+0xAA8` / `+0xAC4` | 54 / 291 |
| gear ratios | `+0xAC0`, 12-byte stride | `+0xAC8`, 20-byte stride | 313, 314 |
| inputs | `+0xDF8` | `+0x1298` | 45 |
| speed, m/s | `+0xE28` | `+0x12D4` | 104 |
| position | `+0xEF0` | `+0x13A0` | 172 |
| player flag (byte) | `+0x4EE7` | `+0xCB89` | 468 |
| AI-controlled (int) | `+0xFBC` | `+0x1534` | 19, 47 |
| wheel blocks | `+0x11B0`, stride `0x4B0` | `+0x1750`, stride `0x1490` | |

Within a wheel block (FH2 -> FH3): suspension position `0x000` (entry 244), spin `0x00C` (180/181),
slip angle `0x024 -> 0x048` (179), contact longitudinal velocity `0x2C4 -> 0x2FC` (from the
`+0x38` shift of its neighbours), tyre radius `0x3C0 -> 0x414` (168), droop `0x3F4 -> 0x458`
(neighbours `0x3F8/0x3FC -> 0x45C/0x460`, entries 254/255), bump `0x3FC -> 0x460`, rest
`0x484 -> 0x524` (entry 245, `pos - [w+0x524]`, FH2's 205).

Checked read-only against the running game (intro drive, 2026-10-29, `tools/live.py`): 30 cars;
exactly one with the player flag and AI = 0; on it RPM 6912 of 10000, gear 6 of 8 (count includes
reverse), throttle 0.75, speed 73 m/s, wheel spin x radius = contact velocity = speed (221.5 x
0.331 = 73.3), suspension between droop and bump, clutch dips and gears drop under braking, slip
angle rises in corners. Then through the app's own engine: car found about 1 s after start,
60 packets/s.

Scanning: FH3 has ~11 GB of writable memory, 4.6 GB of it write-combined (GPU) memory that takes
tens of seconds to read. The scanner now skips `PAGE_WRITECOMBINE`/`PAGE_NOCACHE` pages and
searches the private heap first (mapped memory only if no car turns up).

Probe drive (`probe-fh3-20261029-165614.bin`, `tools/fh3_probe.py` + `tools/fh3_pa.py`; the
probe keeps the first 0x7000 bytes, so the player is the car with AI = 0):

- Wheel order FL, FR, RL, RR: on full left lock (steer -1) the right-hand wheels spin faster, and
  the other way round.
- Steering -1 left, +1 right; reaches -0.98..1.00.
- Gear: raw 0 is reverse (wheels spin backwards, ratio -2.93), forward 1..n, count 8 includes
  reverse.
- Suspension: fronts compress and rears extend under braking; positions stay between droop and
  bump (hitting both off road); rest 0.0267 front, 0.0517 rear; tyre radius 0.3305 / 0.3554.
- Handbrake reaches 1 in the slide phase; pause freezes the block.
- Slip spikes: contact velocity is exactly 0 while a wheel is in the air (32-69 frames per wheel
  with the car moving). The reader now reports no slip for such a wheel. Real wheelspin (launch,
  surface 15.6 m/s against 2.2 m/s) is still reported.

# In-car camera (FOV and seat position)

Both games register their camera tuning values by name with the address of a static variable
(`tools/tunables.py` lists them; FH3 shown, FH2 uses other code so the script finds none there):
`Camera\FileData\Driver\FOV` (`0x144639F58`, 48 at run time), `Driver\DefaultOffsetFromSteeringWheelX/Y/Z`
(`0x144E72B70`), `Driver\TripleScreenAdditionalZOffset` (`0x144E72964`, -0.2), `CockpitCam\FOVRatio`,
plus Hood, BumperHigh, Wheel and Free cameras.

Those are only read when a view starts. `Camera::CCamDriver` (the in-car view) copies them into
the object: vtable slot 85 sets `+0x1F0` = FOV in radians, slot 86 sets the seat offset `+0x1B0`
(x, y, z) from the car's own cockpit data, falling back to the defaults, then adds the triple-screen
Z. Every frame the FOV getter (slot 15) reads `+0x1F0` and the update (slots 2 and 4) read
`+0x1B0`. So the app writes those two fields in the live object and writes them again whenever the
game sets the view up afresh.

| | FH2 | FH3 |
|---|---|---|
| `CCamDriver` vtable | `0x142E78120` (78 entries) | `0x14463ACB0` (89) |
| FOV, radians | `+0x1D0` (getter slot 14, setup slot 74) | `+0x1F0` (slots 15, 85) |
| seat offset x, y, z, metres | `+0x190` (setup slot 75) | `+0x1B0` (slot 86) |

Checked live in FH3 (2026-10-29): two `CCamDriver` objects, one in use at 48 degrees with a
per-car offset such as (-0.02, 0.03, 0.02), the other all zero. Written values stay put (the
game doesn't rewrite them each frame); a new car sets new values. The app's `CameraAdjuster`
moved FOV 48 -> 49 and seat z +0.01 and back. FH2's fields come from the matching functions;
not yet tried live. Axis directions (z forward, y up, x right) are assumed from the hood
camera's offset (0, 0.2, -0.5) and the triple-screen z of -0.2 and need confirming in game.

## Every driving view (2026-10-29)

`CCamDriver` (cockpit), `CCamCockpit` (dashboard), `CCamHood` and `CCamBumperHigh` share one FOV
getter, so FOV and seat offset sit at the same place in all four (FH2 `+0x1D0`/`+0x190`, FH3
`+0x1F0`/`+0x1B0`). The chase views `CCamFollow`, `CCamFollowLow` (near), `CCamFollowHigh` (far)
read FOV in degrees from the car's view data: FH2 `[obj+0x180]+0x1C`, FH3 `[obj+0x1A0]+0x1C`
(48 near, 58 far in FH3).

| View | FH2 vtable | FH3 vtable |
|---|---|---|
| Cockpit (`CCamDriver`) | `0x142E78120` | `0x14463ACB0` |
| Dashboard (`CCamCockpit`) | `0x142E77C30` | `0x14463A710` |
| Hood | `0x142E77EA8` | `0x14463A9E0` |
| Bumper (`CCamBumperHigh`) | `0x142E703B0` | `0x144630430` |
| Chase (`CCamFollow`) | `0x142E783A0` | `0x14463AF88` |
| Chase near (`CCamFollowLow`) | `0x142E78610` | `0x14463B250` |
| Chase far (`CCamFollowHigh`) | `0x142E78880` | `0x14463B518` |

All views exist at once, so the one in use has to be found. No flag or manager pointer was found
quickly; a cued recording (voice asking for each view, FH3) showed the view in use is the one whose
first 0x300 bytes keep changing: bumper only BumperHigh, chase near only FollowLow, chase far only
FollowHigh, cockpit Driver plus Hood, hood Hood plus Bumper. So the adjuster takes the changing view
with the best priority (cockpit, dashboard, chase, hood, bumper) and keeps the last one while nothing
changes (paused). Its own FOV/seat writes are masked out of the comparison.
