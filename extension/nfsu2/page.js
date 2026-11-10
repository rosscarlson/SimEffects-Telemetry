// SimEffects Telemetry for NFSU2: the page half (MAIN world, document_start).
//
// What it does on nfsu2.xyz, and nothing else:
//   1. Keeps a reference to the game's shared WebAssembly memory as the page makes it, so SimEffects Telemetry can
//      read it (guest addresses are offsets into that memory).
//   2. Gives every controller without one a vibrationActuator, so the page tells the game the controller can do force
//      feedback (IM_PAD_RUMBLE = 1) and the game makes its wheel effects. playEffect does nothing: the app plays the
//      game's effects on the wheel itself.
//   3. Answers the app's read requests, which come through relay.js and bridge.js (docs/nfsu2/bridge.md).
// It never writes to the game's memory and never touches the page's network traffic. It logs nothing.
(() => {
  'use strict';
  if (window.__simfxNfsu2) return;
  window.__simfxNfsu2 = true;

  const VERSION = '0.1.1';
  const PROTOCOL = 1;
  const MIN_BYTES = 256 * 1024 * 1024;       // the game's memory is ~1 GB and more; the page's checks make 64 KB ones
  const MIN_SEEN = 1024 * 1024;              // smaller shared memories are the page's checks, not the game's

  // ---- the game's memory --------------------------------------------------------------------------------------

  const NativeMemory = WebAssembly.Memory;
  // Every shared memory and buffer the page or its workers make or pass, held weakly; the game's is the largest. A
  // memory can start small and grow, so the choice is made each time it's needed, not when the memory is made.
  const seen = [];                           // WeakRefs to WebAssembly.Memory or SharedArrayBuffer
  let seenCount = 0;
  let chosen = null;                         // the last choice, kept strongly while it's the game's

  function bytesOf(x) {
    try { return x instanceof NativeMemory ? x.buffer.byteLength : x.byteLength; } catch { return 0; }
  }
  function choose() {
    let best = null, bestBytes = 0;
    for (let i = seen.length - 1; i >= 0; i--) {
      const x = seen[i].deref();
      if (!x) { seen.splice(i, 1); continue; }
      const n = bytesOf(x);
      // a Memory beats its own buffer or a copy of it of the same size
      if (n > bestBytes || (n === bestBytes && x instanceof NativeMemory)) { best = x; bestBytes = n; }
    }
    chosen = bestBytes >= MIN_BYTES ? best : null;
    return chosen;
  }
  const buffer = () => { const x = choose(); return !x ? null : x instanceof NativeMemory ? x.buffer : x; };
  const memBytes = () => { const b = buffer(); return b ? b.byteLength : 0; };

  function consider(x) {
    try {
      const shared = x instanceof NativeMemory ? x.buffer instanceof SharedArrayBuffer : x instanceof SharedArrayBuffer;
      if (!shared || bytesOf(x) < MIN_SEEN) return;
      for (const r of seen) if (r.deref() === x) return;
      seen.push(new WeakRef(x));
      seenCount++;
      if (bytesOf(x) >= MIN_BYTES) state();
    } catch { /* never in the page's way */ }
  }
  // what a worker message may carry: the memory, its buffer, or either one level down
  function considerData(d) {
    if (!d || typeof d !== 'object') return;
    consider(d);
    for (const k of ['memory', 'buf', 'buffer', 'mem']) {
      try { if (k in d) consider(d[k]); } catch { }
    }
  }

  WebAssembly.Memory = new Proxy(NativeMemory, {
    construct(target, args, newTarget) {
      const m = Reflect.construct(target, args, newTarget);
      try { consider(m); } catch { }
      return m;
    },
  });

  const NativeWorker = window.Worker;
  if (NativeWorker) {
    window.Worker = new Proxy(NativeWorker, {
      construct(target, args, newTarget) {
        const w = Reflect.construct(target, args, newTarget);
        try { w.addEventListener('message', e => considerData(e.data)); } catch { }
        return w;
      },
    });
    const post = NativeWorker.prototype.postMessage;
    NativeWorker.prototype.postMessage = function (data, ...rest) {
      considerData(data);
      return post.call(this, data, ...rest);
    };
  }

  // ---- force feedback capability -------------------------------------------------------------------------------

  let rumbleCalls = 0, rumbleLast = null, rumbleSentAt = 0;
  function noteRumble(type, p) {
    rumbleCalls++;
    const s = p && typeof p.strongMagnitude === 'number' ? p.strongMagnitude : 0;
    const w = p && typeof p.weakMagnitude === 'number' ? p.weakMagnitude : 0;
    const now = performance.now();
    if (rumbleLast && Math.abs(rumbleLast[0] - s) < 0.02 && Math.abs(rumbleLast[1] - w) < 0.02 && now - rumbleSentAt < 1000) return;
    rumbleLast = [s, w]; rumbleSentAt = now;
    send({ t: 'rumble', s, w, n: rumbleCalls });
  }
  const actuators = new Map();
  function actuatorFor(index) {
    let a = actuators.get(index);
    if (!a) {
      a = {
        type: 'dual-rumble',
        effects: ['dual-rumble'],
        playEffect(type, params) { noteRumble(type, params); return Promise.resolve('complete'); },
        reset() { noteRumble('reset', null); return Promise.resolve('complete'); },
      };
      actuators.set(index, a);
    }
    return a;
  }
  const spoofed = new WeakMap();
  function wrapPad(g) {
    if (!g) return g;
    let real = null;
    try { real = g.vibrationActuator; } catch { }
    if (real) return g;
    let p = spoofed.get(g);
    if (!p) {
      p = new Proxy(g, {
        get(t, k) {
          if (k === 'vibrationActuator') return actuatorFor(t.index);
          const v = Reflect.get(t, k);
          return typeof v === 'function' ? v.bind(t) : v;
        },
        has(t, k) { return k === 'vibrationActuator' || Reflect.has(t, k); },
      });
      spoofed.set(g, p);
    }
    return p;
  }
  let lastPads = [];
  const nativeGetGamepads = Navigator.prototype.getGamepads;
  if (nativeGetGamepads) {
    Navigator.prototype.getGamepads = function () {
      const list = nativeGetGamepads.call(this);
      lastPads = list;
      return Array.from(list, wrapPad);
    };
  }
  function padList() {
    const out = [];
    for (const g of lastPads) {
      if (!g) continue;
      let real = false;
      try { real = !!g.vibrationActuator; } catch { }
      out.push({ index: g.index, id: g.id, mapping: g.mapping, axes: g.axes.length, buttons: g.buttons.length, spoofed: !real });
    }
    return out;
  }

  // ---- talking to the app (through relay.js) -------------------------------------------------------------------

  function send(o) {
    try { document.dispatchEvent(new CustomEvent('simfx:out', { detail: JSON.stringify(o) })); } catch { }
  }
  function state() {
    send({ t: 'state', mem: memBytes(), seen: seenCount, pads: padList(), visible: document.visibilityState === 'visible', rumbleCalls });
  }
  function hello() {
    send({ t: 'hello', v: PROTOCOL, ext: VERSION, page: location.href, top: window === window.top, mem: memBytes() });
    state();
  }

  const toB64 = (u8) => {
    if (typeof u8.toBase64 === 'function') return u8.toBase64();
    let s = '';
    for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
    return btoa(s);
  };
  const MAX_READ = 16 * 1024 * 1024;
  function read(m) {
    const b = buffer();
    if (!b) return send({ t: 'r', id: m.id, err: 'no game memory yet' });
    const out = [];
    for (const [a, n] of m.r) {
      if (!(a >= 0 && n >= 0 && n <= MAX_READ && a + n <= b.byteLength)) return send({ t: 'r', id: m.id, err: `out of range: 0x${(a >>> 0).toString(16)}+${n} (memory ${b.byteLength})` });
      out.push(toB64(new Uint8Array(b, a, n).slice()));
    }
    send({ t: 'r', id: m.id, d: out });
  }

  document.addEventListener('simfx:in', (e) => {
    let m;
    try { m = JSON.parse(e.detail); } catch { return; }
    if (!m || typeof m !== 'object') return;
    try {
      if (m.t === 'read') read(m);
      else if (m.t === 'app') { if (m.up) hello(); }
      else if (m.t === 'ping') send({ t: 'pong', id: m.id });
      else if (m.t === 'state?') state();
    } catch (err) {
      if (m.t === 'read') send({ t: 'r', id: m.id, err: String(err) });
    }
  });

  setInterval(state, 1000);
  document.addEventListener('visibilitychange', state);
  hello();
})();
