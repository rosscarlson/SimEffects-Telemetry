// SimEffects Telemetry for NFSU2: the bridge (service worker). Holds the WebSocket to SimEffects Telemetry on this
// computer and passes messages between it and the game's page. The extension's own context, so the page's content
// security policy and network rules don't apply, and nothing goes to nfsu2.xyz.
'use strict';

const APP_URL = 'ws://127.0.0.1:28020/nfsu2';
const RETRY_MS = 2000;

const ports = new Map();       // port -> { mem: bytes of game memory that frame holds }
let ws = null, open = false, retry = null;

// the frame the app talks to: the one holding the game's memory, else the newest
function target() {
  let best = null, last = null;
  for (const [p, s] of ports) { last = p; if (s.mem > 0) best = p; }
  return best ?? last;
}

function tellPages(up) {
  const s = JSON.stringify({ t: 'app', up });
  for (const p of ports.keys()) { try { p.postMessage(s); } catch { } }
}

function ensureSocket() {
  if (ws || ports.size === 0) return;
  clearTimeout(retry); retry = null;
  let s;
  try { s = new WebSocket(APP_URL); } catch { retry = setTimeout(ensureSocket, RETRY_MS); return; }
  ws = s;
  s.onopen = () => { open = true; tellPages(true); };
  s.onmessage = (e) => {
    if (typeof e.data !== 'string') return;
    const p = target();
    try { p?.postMessage(e.data); } catch { }
  };
  s.onerror = () => { };
  s.onclose = () => {
    const was = open;
    ws = null; open = false;
    if (was) tellPages(false);
    if (ports.size) retry = setTimeout(ensureSocket, RETRY_MS);
  };
}

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== 'nfsu2') return;
  ports.set(port, { mem: 0 });
  port.onMessage.addListener((s) => {
    if (typeof s !== 'string') return;
    if (s.startsWith('{"t":"state"') || s.startsWith('{"t":"hello"')) {
      try { const m = JSON.parse(s); ports.get(port).mem = m.mem > 0 ? m.mem : 0; } catch { }
    }
    if (open) { try { ws.send(s); } catch { } }
    else ensureSocket();
  });
  port.onDisconnect.addListener(() => {
    ports.delete(port);
    if (ports.size === 0 && ws) { try { ws.close(); } catch { } }
  });
  if (open) { try { port.postMessage(JSON.stringify({ t: 'app', up: true })); } catch { } }
  ensureSocket();
});
