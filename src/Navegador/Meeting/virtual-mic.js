// Modo reunión: micrófono virtual.
// Se inyecta SÓLO en la pestaña con modo reunión (documento principal), que es la única con chrome.webview.
// La página pide el micrófono como siempre (getUserMedia, con el permiso de siempre) y recibe una pista armada acá:
//   voz original (ganancia `original`) + voz traducida que manda el navegador (ganancia `translated`).
// Si hay un traductor escuchando (`capture`), la voz original va al navegador en PCM 16 kHz mono, 16 bits, base64.
// Mensajes del navegador: {t:'mode', original, translated, capture} · {t:'play', rate, pcm} · {t:'state'}.
// Mensajes al navegador: ready · mic {rate, pcm} · mic-started {label} · mic-stopped · state · error.
(() => {
  'use strict';
  if (window.top !== window) return;
  const wv = window.chrome && window.chrome.webview;
  const md = navigator.mediaDevices;
  if (!wv || !md || typeof md.getUserMedia !== 'function') return;
  const KEY = Symbol.for('navegador.reunion');
  if (window[KEY]) return;
  Object.defineProperty(window, KEY, { value: true });

  const CAPTURE_RATE = 16000;
  const MAX_QUEUE_S = 1.0;      // voz traducida atrasada más que esto: se descarta el pedazo en vez de acumular retraso
  const TAP_FRAMES = 1024;      // ~21 ms a 48 kHz: lo que tarda en salir cada pedazo de voz hacia el traductor
  // Colchón al arrancar a reproducir la traducida (absorbe la variación de llegada). Empieza chico (menos demora) y
  // crece de a 20 ms con cada corte, hasta 150 ms: con la página cargada (Meet) se acomoda solo para no cortar.
  const JITTER_MIN_S = 0.04, JITTER_STEP_S = 0.02, JITTER_MAX_S = 0.15;
  let jitter = JITTER_MIN_S;
  const realGetUserMedia = md.getUserMedia.bind(md);
  const post = m => { try { wv.postMessage(m); } catch (e) { /* la pestaña se está cerrando */ } };

  let ctx = null;
  let translatedBus = null;
  let mode = { original: 1, translated: 0, capture: false };
  let playAt = 0;
  let dropped = 0;
  // Ida y vuelta (sólo tiene sentido con un traductor que contesta un pedazo por pedazo, como el de prueba).
  const sentAt = [];
  const rtts = [];
  let gaps = 0;                 // la voz traducida se cortó porque el pedazo siguiente llegó tarde (se oye como un clic)
  const sessions = new Set();

  function context() {
    if (ctx) return ctx;
    ctx = new AudioContext({ latencyHint: 'interactive' });
    translatedBus = ctx.createGain();
    translatedBus.gain.value = mode.translated;
    // Sin un gesto de la persona el contexto puede arrancar suspendido; en una reunión siempre hay un clic ("Unirse").
    const resume = () => { if (ctx.state !== 'running') ctx.resume().catch(() => {}); };
    for (const ev of ['pointerdown', 'keydown']) addEventListener(ev, resume, true);
    resume();
    return ctx;
  }

  function applyMode() {
    if (!ctx) return;
    const t = ctx.currentTime;
    translatedBus.gain.setTargetAtTime(mode.translated, t, 0.02);
    for (const s of sessions) s.originalGain.gain.setTargetAtTime(mode.original, t, 0.02);
  }

  // Remuestreo lineal a 16 kHz, entero de 16 bits little-endian, en base64.
  function encode(input, fromRate) {
    const ratio = fromRate / CAPTURE_RATE;
    const n = Math.floor(input.length / ratio);
    const out = new Int16Array(n);
    for (let i = 0; i < n; i++) {
      const x = i * ratio, i0 = Math.floor(x), f = x - i0;
      const a = input[i0], b = i0 + 1 < input.length ? input[i0 + 1] : a;
      const v = Math.max(-1, Math.min(1, a + (b - a) * f));
      out[i] = v < 0 ? v * 0x8000 : v * 0x7fff;
    }
    const bytes = new Uint8Array(out.buffer);
    let bin = '';
    for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }

  function decode(b64) {
    const bin = atob(b64);
    const n = bin.length >> 1;
    const f = new Float32Array(n);
    for (let i = 0; i < n; i++) {
      let v = bin.charCodeAt(2 * i) | (bin.charCodeAt(2 * i + 1) << 8);
      if (v >= 0x8000) v -= 0x10000;
      f[i] = v / 0x8000;
    }
    return f;
  }

  function play(b64, rate) {
    if (!ctx) return;
    const data = decode(b64);
    if (!data.length) return;
    const now = ctx.currentTime;
    if (sentAt.length) { rtts.push(performance.now() - sentAt.shift()); if (rtts.length > 50) rtts.shift(); }
    if (playAt < now + 0.005) {                            // el flujo se cortó: arranca con un margen chico
      if (playAt > 0 && now - playAt < 0.5) {               // corte corto en medio de una frase (no una pausa)
        gaps++;
        jitter = Math.min(JITTER_MAX_S, jitter + JITTER_STEP_S);
      }
      playAt = now + jitter;
    }
    if (playAt - now > MAX_QUEUE_S) { dropped++; return; } // demasiado atrasado
    const buf = ctx.createBuffer(1, data.length, rate);
    buf.copyToChannel(data, 0);
    const src = ctx.createBufferSource();
    src.buffer = buf;
    src.connect(translatedBus);
    src.start(playAt);
    playAt += buf.duration;
  }

  function wrap(real) {
    const realAudio = real.getAudioTracks()[0];
    if (!realAudio) return real;
    const c = context();
    const source = c.createMediaStreamSource(new MediaStream([realAudio]));
    const originalGain = c.createGain();
    originalGain.gain.value = mode.original;
    const dest = c.createMediaStreamDestination();
    source.connect(originalGain).connect(dest);
    translatedBus.connect(dest);

    // Toma de la voz original para el traductor (ScriptProcessor: no necesita cargar un módulo, que el CSP del
    // sitio podría bloquear). Su salida va a una ganancia 0: tiene que estar conectado para procesar.
    const tap = c.createScriptProcessor(TAP_FRAMES, 1, 1);
    const sink = c.createGain();
    sink.gain.value = 0;
    source.connect(tap);
    tap.connect(sink).connect(c.destination);
    tap.onaudioprocess = e => {
      if (!mode.capture || !realAudio.enabled || realAudio.readyState !== 'live') return;
      post({ t: 'mic', rate: CAPTURE_RATE, pcm: encode(e.inputBuffer.getChannelData(0), c.sampleRate) });
      sentAt.push(performance.now());
      if (sentAt.length > 200) sentAt.shift();
    };

    // Para la página es el micrófono real: nombre, ajustes y silenciar pasan a la pista real.
    const virtual = dest.stream.getAudioTracks()[0];
    const session = { originalGain, virtual };
    sessions.add(session);
    const enabledDesc = Object.getOwnPropertyDescriptor(MediaStreamTrack.prototype, 'enabled');
    Object.defineProperty(virtual, 'enabled', {
      configurable: true,
      get() { return enabledDesc.get.call(this); },
      set(v) { enabledDesc.set.call(this, v); realAudio.enabled = v; },
    });
    Object.defineProperty(virtual, 'label', { configurable: true, get: () => realAudio.label });
    virtual.getSettings = () => realAudio.getSettings();
    virtual.getCapabilities = () => (realAudio.getCapabilities ? realAudio.getCapabilities() : {});
    virtual.getConstraints = () => realAudio.getConstraints();
    virtual.applyConstraints = cs => realAudio.applyConstraints(cs);
    const stopVirtual = virtual.stop.bind(virtual);
    let stopped = false;
    virtual.stop = () => {
      if (stopped) return;
      stopped = true;
      stopVirtual();
      realAudio.stop();
      tap.onaudioprocess = null;
      try { source.disconnect(); tap.disconnect(); translatedBus.disconnect(dest); } catch (e) { /* ya desconectado */ }
      sessions.delete(session);
      post({ t: 'mic-stopped' });
    };
    realAudio.addEventListener('ended', () => virtual.stop());

    post({ t: 'mic-started', label: realAudio.label });
    return new MediaStream([...real.getVideoTracks(), virtual]);
  }

  const getUserMedia = async function getUserMedia(constraints) {
    const real = await realGetUserMedia(constraints);
    if (!constraints || !constraints.audio) return real;
    try { return wrap(real); }
    catch (e) { post({ t: 'error', message: String(e && e.message || e) }); return real; }
  };
  Object.defineProperty(md, 'getUserMedia', { value: getUserMedia, configurable: true, writable: true });

  wv.addEventListener('message', e => {
    const m = e.data;
    if (!m || typeof m !== 'object') return;
    if (m.t === 'mode') {
      mode = { original: +m.original || 0, translated: +m.translated || 0, capture: !!m.capture };
      sentAt.length = 0; // otro traductor u otro modo: la ida y vuelta y los cortes se empiezan a medir de nuevo
      rtts.length = 0;
      gaps = 0;
      applyMode();
    } else if (m.t === 'play' && typeof m.pcm === 'string') {
      play(m.pcm, m.rate > 3000 ? m.rate : CAPTURE_RATE);
    } else if (m.t === 'state') {
      post({
        t: 'state', sessions: sessions.size, audio: ctx ? ctx.state : 'none', mode, dropped,
        rttMs: rtts.length ? Math.round([...rtts].sort((a, b) => a - b)[rtts.length >> 1]) : null, // mediana de los últimos 50
        rttMaxMs: rtts.length ? Math.round(Math.max(...rtts)) : null, gaps,
        tapMs: ctx ? Math.round(TAP_FRAMES / ctx.sampleRate * 1000) : null, jitterMs: Math.round(jitter * 1000),
        outputLatencyMs: ctx ? Math.round(((ctx.baseLatency || 0) + (ctx.outputLatency || 0)) * 1000) : null,
      });
    }
  });
  post({ t: 'ready' });
})();
