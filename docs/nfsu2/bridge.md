# NFSU2 browser bridge

How SimEffects Telemetry reads Need for Speed: Underground 2 on [nfsu2.xyz](https://nfsu2.xyz) through the Chrome
extension in `extension/nfsu2/`.

## Pieces

```
nfsu2.xyz page                                  extension service worker            SimEffects Telemetry / gtt
  page.js (MAIN world, document_start)            bridge.js                           Core/Browser/BrowserBridge
    WebAssembly.Memory  ─ captured                  WebSocket client ──────────────►    127.0.0.1:28020/nfsu2
    getGamepads()       ─ vibrationActuator spoof     (retries every 2 s)                Origin must be chrome-extension://
    read requests       ─ answered from memory
        │  CustomEvent simfx:out / simfx:in (JSON strings)
  relay.js (isolated world) ── chrome.runtime port "nfsu2" ──►
```

- **page.js** keeps the game's shared `WebAssembly.Memory` as the page makes it (in Chrome the page makes it and hands
  it to the game's workers; it also watches worker messages for the memory or its buffer, for builds that make it in a
  worker). Guest addresses are offsets into that memory (`g_mem` is 0 in the wasm build); the runtime's own data is
  above 0x28000000.
- It wraps `navigator.getGamepads()` so every controller without a `vibrationActuator` gets one whose `playEffect`
  does nothing. The page then sets `IM_PAD_RUMBLE = 1`, the game sees a force-feedback device and creates its effects.
  This is the only thing the extension changes, and it changes it in the page, not in the game's memory.
- The WebSocket is opened by the service worker, not the page, so the page's content security policy and Chrome's
  local-network rules for web pages don't get in the way, and nothing about it is visible to nfsu2.xyz.
- Read-only: there is no write message. Nothing is sent to nfsu2.xyz.

## Messages

Text frames, one JSON object each, `t` first.

Page → app:

| `t` | Fields | When |
|---|---|---|
| `hello` | `v` protocol (1), `ext` extension version, `page` URL, `top` top frame, `mem` memory bytes | When the app connects |
| `state` | `mem`, `pads` [{`index`, `id`, `mapping`, `axes`, `buttons`, `spoofed`}], `visible`, `rumbleCalls` | Every second and on visibility changes |
| `r` | `id`, `d` [base64 per range] or `err` | Answer to `read` |
| `rumble` | `s`, `w` (0..1), `n` calls so far | When the game's rumble mix changes (a sign its FFB is on) |
| `pong` | `id` | Answer to `ping` |

App → page:

| `t` | Fields | |
|---|---|---|
| `read` | `id`, `r` [[address, length], …] | Several ranges in one round trip; at most 16 MB a range |
| `ping` | `id` | Every 10 s; keeps the service worker awake |
| `state?` | | Ask for a `state` now |

Service worker → page: `{"t":"app","up":true|false}` when the WebSocket opens or closes; the page answers `up` with
`hello`.

## Discovery

`gtt nfsu2 serve` hosts the bridge and a probe port (127.0.0.1:28021; `R <addr> <len>` → `OK <len>` + bytes,
`S` → state JSON) for the scripts in `tools/nfsu2/` (`nfsu2probe.py`, `car.py`, `find_effects.py`). Close the app
first: only one can listen on 28020.
