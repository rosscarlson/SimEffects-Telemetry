// SimEffects Telemetry for NFSU2: the relay (isolated world). Passes page.js's messages to the extension's service
// worker (bridge.js) and back. Both sides are JSON strings (docs/nfsu2/bridge.md).
(() => {
  'use strict';
  let port = null, dead = false;

  function connect() {
    if (dead) return;
    try { port = chrome.runtime.connect({ name: 'nfsu2' }); }
    catch { port = null; dead = true; return; }      // the extension was reloaded or removed: this page is done with it
    port.onMessage.addListener((s) => {
      document.dispatchEvent(new CustomEvent('simfx:in', { detail: s }));
    });
    port.onDisconnect.addListener(() => {
      port = null;
      setTimeout(connect, 1000);
    });
  }

  document.addEventListener('simfx:out', (e) => {
    if (typeof e.detail !== 'string') return;
    if (!port) connect();
    try { port?.postMessage(e.detail); } catch { port = null; }
  });

  connect();
})();
