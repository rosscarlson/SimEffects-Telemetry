# PINE notes

PINE is the IPC protocol exposed by PCSX2 (and RPCS3, DuckStation). On Windows it's a TCP socket on `127.0.0.1:<slot>`.
In PCSX2: Settings → Advanced → PINE Settings → Enable. The default slot is **28011** (stored as `EnablePINE` and
`PINESlot` in `inis/PCSX2.ini`).

## Wire format (little-endian)

```
request = u32 total_size (including these 4 bytes) | command | command | ...
command = u8 opcode | args
reply   = u32 total_size | u8 result (0x00 OK, 0xFF FAIL) | data of each command, back to back
```

One result byte covers the whole batch; any failing command fails the batch.

| Opcode | Name | Args | Reply data |
|---|---|---|---|
| 0x00–0x03 | Read8/16/32/64 | u32 address | value (1/2/4/8 bytes) |
| 0x04–0x07 | Write8/16/32/64 | u32 address, value | — |
| 0x08 | Version | — | u32 length, string |
| 0x09 / 0x0A | SaveState / LoadState | u8 slot | — |
| 0x0B | Title | — | u32 length, string |
| 0x0C | ID (serial) | — | u32 length, string |
| 0x0D | UUID (disc CRC) | — | u32 length, string |
| 0x0E | GameVersion | — | u32 length, string |
| 0x0F | Status | — | u32: 0 running, 1 paused, 2 shutdown |

PCSX2 limits requests to 650,000 bytes and replies to 450,000 bytes. `PineClient` reads bulk memory as batches of
50,000 `Read64` commands (400 KB replies). Addresses are EE addresses as the game sees them: RAM at
0x00000000–0x01FFFFFF, with mirrors at 0x20/0x30/0x80/0xA0 xxxxxx.

## Fast path: `EEmem`

PCSX2 (Qt builds, 1.7+) exports three data symbols from `pcsx2-qt.exe`: `EEmem`, `IOPmem` and `VUmem`. Each is a
pointer to the host buffer backing that memory. PCSX2 2.8.2 (G:\Tools\PCSX2) exports them at RVAs 0x166D568, 0x166D570
and 0x166D578. `Pcsx2ProcessMemory` resolves `EEmem` through the export table at runtime, so it doesn't depend on these
RVAs.
