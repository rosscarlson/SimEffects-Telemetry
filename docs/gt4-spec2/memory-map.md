# GT4 Spec II memory map

Game: Gran Turismo 4 Spec II v1.10 (GT4 Online Public Beta base, NTSC-U, **SCUS-97436**, CRC **646B2E29**).
Emulator: PCSX2 2.8.2. Addresses are EE addresses. Spec II differs from retail GT4 and other mods, so nothing here is
trusted until it has been verified on this build.

Machine-readable map: [`data/gt4-spec2/addresses.json`](../../data/gt4-spec2/addresses.json).

## Status legend

- **Confirmed**: verified live on this build and survives a save-state reload (car/track changes noted per entry).
- **Candidate**: found by a session or scan, not yet verified.
- **From pnach**: taken from the community cheat file `G:\Tools\PCSX2\cheats\646B2E29.pnach`, still to verify.

## Addresses move between races (2026-11-03)

The fixed addresses below are where things were in the races they were mapped in (car block at 0x01E6E750). In a later
race (different car and track) every one of them read 0: the game allocates the car and its input block per race.
The streamer now finds them each race:

| What | How | Notes |
|---|---|---|
| Car block | u32 at 0x007342B8, minus 0x1AEB0 | Held in all 40 recorded snapshots across three races. Everything in the car block keeps its offset from the car (e.g. speed at car + 0x560). |
| Input block | Search car − 0x6C00 … car − 0x6A00 for u32 0x006DEB50 at throttle − 0x10 and 0x006B4A20 at throttle + 0x30 | Throttle is car − 0x6B18 in one race and car − 0x6B14 in another, so search, don't assume. Several input-block copies exist further away; take the one nearest car − 0x6B16. Steering at throttle − 4 (s16), brake at throttle + 2 (u16). |
| Frame counter | Input block, throttle + 0x20 (u32) | 60 per second while racing, stops in the pause menu. Replaces 0x01E60ED8, which only held in the first race. |
| RPM, gear | Car + 0x654 (f32), car + 0x544 (u8) | The dash copies (0x01E6282C, 0x01E63188) are in a separate block with no stable pointer (0x0073C128 and 0x0073AC70 point to it most of the time). |
| Throttle, brake (assisted) | Car + 0x4AC, car + 0x4B0 (f32 0–1) | Fallback when the input block isn't found. Steering fallback: car + 0x8FC (0 left, 0.5 centre, 1 right). |
| Race time | Dash block + 0x1464 (u32 ms) | Stops when paused. Not used, because the dash block has no stable pointer. |

## Telemetry (milestone 2)

| Field | Address | Type | Units / scale | Status | Notes |
|---|---|---|---|---|---|
| Throttle | 0x01E67C38 | u16 | 0–4096 | Confirmed | Car input block. Pad value: 0x006819F8 u8 0–255 |
| Brake | 0x01E67C3A | u16 | 0–4096 | Confirmed | Pad: 0x006819FC u8. Normalised f32 at 0x01E62770 |
| Steering | 0x01E67C34 | s16 | ±4096, left + | Confirmed | Pad: 0x006819E0 s32 ±8192 (right +). 0x01E6F04C f32: 0 left, 0.5 centre, 1 right |
| Steer angle | 0x01E6E694 | f32 | rad, left + | Confirmed | ±0.6981 (40°) at full lock |
| Speed (HUD) | 0x01E627D0 | f32 | mph | Confirmed | Matched the HUD speedometer |
| Speed | 0x01E6EDC0 | f32 | m/s | Confirmed | Magnitude of the velocity pair 0x01E6ECB8 / 0x01E6ECBC |
| Engine RPM | 0x01E6282C | f32 | rpm | Confirmed | Idle 799, limiter ~7540. Also 0x01E6EDA4 |
| Gear | 0x01E63188 | u8 | gear number | Confirmed | 0x01E63189 matched too. Copy at 0x01E6EC94 |

Confirmed 2026-11-02/03 on two cars and tracks (Ford GT40 at the Nürburgring, Lotus Esprit at Laguna Seca) and after
a PCSX2 restart plus save-state load, checked against the HUD. `speed_mph` is the HUD's displayed speed and lags the
physics speed (`speed`, m/s) under hard braking; prefer `speed` for telemetry.

## Physics (milestone 2, Esprit at Laguna Seca, 2026-11-03)

Found from continuous recordings (`gtt record physics-drive`, `gtt record surfaces`) analysed offline. Status: Candidate
(one car), but each was checked against an independent quantity (r ≥ 0.8).

| What | Address | Type | Notes |
|---|---|---|---|
| Frame counter | 0x01E60ED8 | u32 | 60 per second of game time. Stops whenever the car block freezes (288 of 288 stalled samples in the recording), so the streamer treats 150 ms without a tick as paused |
| Position | 0x01E6E754 | f32 ×3 | World X, Y, Z (Z up), m |
| Forward axis | 0x01E6E760 | f32 ×3 | Unit vector; right axis at 0x01E6E76C, up axis at 0x01E6E778 |
| Speed | 0x01E6ECB0 | f32 | \|v\|, m/s (0x01E6EDC0 is the signed forward speed) |
| World velocity | 0x01E6ECB8 | f32 ×3 | X, Y, Z, m/s |
| Body acceleration | 0x01E6ECC4 | f32 ×3 | Forward, right, up, m/s² |
| Body rates | 0x01E6ECF0 | f32 ×3 | Yaw (left +), pitch (nose up +), roll (right side down +), rad/s |
| Wheel structs | 0x01E6E794 | 4 × 0xF4 bytes | FL, FR, RL, RR |

Wheel struct offsets:

| Offset | Type | What |
|---|---|---|
| +0x40 | f32 | Tyre surface speed, m/s |
| +0x48 | f32 | Ground speed at the wheel, m/s |
| +0x4C | f32 | Slip angle, rad (same for both wheels of an axle) |
| +0x58 | f32 | Slip ratio, (ground − wheel) / wheel: about −0.95 in wheelspin |
| +0x68 | f32 | Tyre load, N |
| +0x90 | f32 | Drive torque (only driven wheels) |
| +0xA9 | u8 | Surface: 0 circuit tarmac, 2 other tarmac (pit lane; also the strip just before a kerb), 1 kerb, 10 kerb (second track, confirmed by pausing on a left and a right kerb), 3 gravel/sand, 4 grass, 7 rough verge |
| +0xAC | f32 | Ground height under the wheel, m |
| +0xB4 | f32 | Suspension position, m: more negative is more compressed (front about −0.25 to −0.10) |

Not mapped yet: tyre radius, redline, tyre temperatures, lap and position data.

## Display and camera (from the community pnach by Silentwarior112, to verify)

| What | Address | Type | Notes |
|---|---|---|---|
| 3D render aspect | 0x0099C3C8 | f32 | The 21:9 patch writes 1.3145 (0x3FA8427E) and 32:9 writes 2.0. Also adjustable as "Aspect" in the Spec II secret menu. |
| Overscan factor (FOV) | 0x0099C3CC | f32 | The 16:10 patch writes 0.90; the pnach says to edit it for more or less FOV. |
| Chase-camera mode | 0x002BA77C | u16 | 0x3F80 = reset, 0x3F81 / 0x3F7F = "cinematic" variants. Upper half of a float immediate in code. |
| Collision toggle | 0x002941B8 | u16 | 0x3F80 on, 0x3F00 off. Code area. |
| HUD canvas / text width | 0x001C10FC, 0x001C1130, 0x001C519C, … | instruction immediates | Code patches (lui/ori), not data. See the pnach. |
| Deinterlace menus | 0x0059F25C | u32 | Instruction patch. |

The active cheats in `gamesettings/SCUS-97436_646B2E29.ini` were "32:9 Ultrawide Hack" and "Deinterlace menus".

The pnach's "32:9 Ultrawide Hack" only sets the 3D aspect, so the HUD and menus stretch. Its "64:9 Ultrawide Hack" sets
aspect 4.0, but its HUD, text, menu and loading values are the 32:9 set: spinner ±640 = 320 × 32/16, text ×0.375 = 0.75 / 2,
and the HUD canvas (−1280, 748) is on the line through the 21:9 and 48:9 values at aspect 2.0. SimEffects Telemetry's display
modes (Game profile card) add a managed "SimEffects 32:9 Ultrawide (HUD scaled)" group (called "GT Telemetry 32:9 …" before the rename) that combines aspect 2.0 with
those values. It's appended to `cheats/646B2E29.pnach` between marker comments, and the file is backed up first. A separate pnach file isn't
used because PCSX2 2.8 searches `SERIAL_CRC*.pnach` and the CRC-only file may only be a fallback. Each mode also sets
the per-game `[EmuCore/GS] AspectRatio` (`Stretch` for the wide modes so the image fills the window, `16:9` otherwise).

## Camera: FOV and seat (milestone 3, Laguna Seca, 2026-11-03)

Found by diffing snapshots between the cockpit and hood views, then confirmed by live writes and screenshots.

| What | Address | Type | Notes |
|---|---|---|---|
| FOV multiplier | 0x0023E814 | instruction `lui at, imm` (0x3C01xxxx) | `imm` is the upper half of a float, default 0x3F80 = 1.0. Runs every frame, so a write applies live, in all views. Above 1 zooms in, below 1 widens. Same address as Blitznoz's "Adjustable FOV" Spec II cheat. Check the 0x3C01 opcode before writing |
| Camera object | 0x00C670A4 | struct, a second copy at 0x00C67110 | +0x08 f32 (58.1 cockpit, 90 hood), +0x0C eye position in a Y-up frame (x, z, −y), +0x18 ×3 zoom (FOV multiplier, and ×1.8 more in the cockpit), +0x3C current view's seat offset (x, y, z). Rebuilt every frame: writes don't stick |
| Projection matrix | 0x0073B2B0 | f32 4×4 | sx at +0, sy at +0x14 (1.664 cockpit, 2.254 hood). Derived every frame |
| Per-car camera table | 0x01C72000, 0x01D08000, 0x01D9E000 | 0x20-byte entries, three identical copies | Each entry: f32, u32 type, f32 x, y, z, … Cockpit entry at +0x2E0 (2.4142, 3, 0.325, −0.985, 0.03, −2), hood entry's x, y, z at +0xA8 (0, −0.9079, −0.2395). x is sideways, y is vertical (negative is up), z is fore/aft (negative is back). Writing all three copies moves the camera live. The table belongs to the car model, so it moves between races and cars: find it by searching for the camera object's current seat offset |

Not found yet: camera pitch (the cockpit entry's −2 and the field after the hood's x, y, z have no visible effect), and a car ID.

## Session log

| Date | Session | Result |
|---|---|---|
| 2026-11-02 | race-state | ~7,000 survivors; the pause flag needs more cycles (low priority) |
| 2026-11-02 | throttle, brake | Found offline from saved snapshots; the live filter was too strict for pedal noise |
| 2026-11-02 | steering | Found live |
| 2026-11-02 | speed | Found via the car-struct region plus a HUD screenshot |
| 2026-11-02 | rpm | Found live |
| 2026-11-02 | gear | Found live, exact match on the first run |
| 2026-11-03 | verification | All addresses held after a car/track change and a PCSX2 restart plus state load |
