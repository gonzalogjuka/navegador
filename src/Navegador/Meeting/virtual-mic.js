// Modo reunión: micrófono virtual.
// Se inyecta SÓLO en la pestaña con modo reunión (documento principal), que es la única con chrome.webview.
// La página pide el micrófono como siempre (getUserMedia, con el permiso de siempre) y recibe una pista armada acá:
//   voz original (ganancia `original`) + voz traducida que manda el navegador (ganancia `translated`).
// Si hay un traductor escuchando (`capture`), la voz original va al navegador en PCM 16 kHz mono, 16 bits, base64.
// Si además el traductor quiere el contexto de la charla (`listen`), también va lo que dicen los demás: el audio de
// las pistas remotas que la página puso a sonar en sus <audio>/<video> (sin tocar cómo suenan).
// Mensajes del navegador: {t:'mode', original, translated, capture, listen} · {t:'play', rate, pcm} · {t:'state'}.
// Mensajes al navegador: ready · mic {rate, pcm} · remote {rate, pcm} · mic-started {label} · mic-stopped · state · error.
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
  // Voz traducida en cola: llega de a frases enteras (varios segundos de una vez), así que se encola; sólo si la cola
  // pasa de 8 s se descarta lo nuevo, para no quedar hablando un minuto atrás de la persona.
  const MAX_QUEUE_S = 8.0;
  const TAP_FRAMES = 1024;      // ~21 ms a 48 kHz: lo que tarda en salir cada pedazo de voz hacia el traductor
  // Colchón al arrancar a reproducir la traducida (absorbe la variación de llegada). Empieza chico (menos demora) y
  // crece de a 20 ms con cada corte, hasta 150 ms: con la página cargada (Meet) se acomoda solo para no cortar.
  const JITTER_MIN_S = 0.04, JITTER_STEP_S = 0.02, JITTER_MAX_S = 0.15;
  let jitter = JITTER_MIN_S;
  const realGetUserMedia = md.getUserMedia.bind(md);
  const post = m => { try { wv.postMessage(m); } catch (e) { /* la pestaña se está cerrando */ } };

  let ctx = null;
  let translatedBus = null;
  let mode = { original: 1, translated: 0, capture: false, listen: false };
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

  // Remuestreo a 16 kHz con filtro pasa bajos y sin cortes entre pedazos (el estado sigue de un pedazo al otro).
  // Antes era lineal y arrancaba de cero en cada pedazo: con el contexto a 96 kHz (placa de sonido a 96 kHz) tomaba
  // una muestra de cada 6 sin filtrar y dejaba un salto cada 10 ms.
  function resampler(fromRate) {
    const ratio = fromRate / CAPTURE_RATE;
    const taps = ratio > 1 ? Math.round(ratio * 8) | 1 : 1, half = taps >> 1;
    const h = new Float32Array(taps);
    let sum = 0;
    for (let i = 0; i < taps; i++) {
      const n = i - half, fc = 0.45 / ratio;                 // corte un poco por debajo de 8 kHz
      const sinc = n === 0 ? 2 * fc : Math.sin(2 * Math.PI * fc * n) / (Math.PI * n);
      h[i] = taps === 1 ? 1 : sinc * (0.5 + 0.5 * Math.cos(Math.PI * n / (half + 1)));
      sum += h[i];
    }
    for (let i = 0; i < taps; i++) h[i] /= sum;
    let buf = new Float32Array(0), filtered = new Float32Array(0);
    let last = 0;   // última muestra filtrada del pedazo anterior
    let pos = 0;    // dónde cae la próxima muestra de salida, en muestras de entrada desde el comienzo del pedazo
    return input => {
      const n = input.length;
      if (buf.length !== n + taps - 1) { buf = new Float32Array(n + taps - 1); filtered = new Float32Array(n); }
      else buf.copyWithin(0, n);                              // la cola del pedazo anterior queda al principio
      buf.set(input, taps - 1);
      for (let i = 0; i < n; i++) {
        let acc = 0;
        for (let k = 0; k < taps; k++) acc += h[k] * buf[i + k];
        filtered[i] = acc;
      }
      const out = new Int16Array(Math.max(0, Math.ceil((n - 1 - pos) / ratio)));
      let count = 0;
      for (; pos < n - 1 && count < out.length; pos += ratio) {
        const i0 = Math.floor(pos), f = pos - i0;
        const a = i0 < 0 ? last : filtered[i0];
        const v = Math.max(-1, Math.min(1, a + (filtered[i0 + 1] - a) * f));
        out[count++] = v < 0 ? v * 0x8000 : v * 0x7fff;
      }
      pos -= n;
      last = filtered[n - 1];
      return count === out.length ? out : out.subarray(0, count);
    };
  }

  // Entero de 16 bits little-endian, en base64.
  function encode(out) {
    const bytes = new Uint8Array(out.buffer, out.byteOffset, out.byteLength);
    let bin = '';
    for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }

  // La voz que va al traductor sale de UNA sola captura: la más nueva que esté viva y sin silenciar. Los sitios de
  // reuniones piden el micrófono más de una vez (vista previa, entrar, cambiar de dispositivo) y dejan varias vivas;
  // si todas mandan, al traductor le llega el audio duplicado e intercalado y no entiende nada (medido el 26/11:
  // 1,98 s de audio por segundo real).
  function capturing() {
    let chosen = null;
    for (const s of sessions) if (s.real.enabled && s.real.readyState === 'live') chosen = s;
    return chosen;
  }

  // ---------- Lo que dicen los demás ----------
  // Cada 2 s se miran los <audio>/<video> de la página que tienen puesta una pista remota (un MediaStream que no es
  // el micrófono) y no están silenciados (la vista propia siempre lo está). Esas pistas se mezclan y, mientras haya
  // sonido, van al traductor igual que la voz propia. No se toca ningún objeto de la página salvo `srcObject`, que
  // sólo se mira para enterarse de los elementos que no están en el documento.
  const REMOTE_SCAN_MS = 2000;
  const REMOTE_HANGOVER_MS = 1200;   // se sigue mandando un rato de silencio: el traductor lo necesita para cortar la frase
  const REMOTE_FLOOR = 0.003;
  const remotes = new Map();         // id de pista → { source, el, track }
  const mediaEls = new Set();
  let remoteBus = null, remoteResample = null, remoteLoudUntil = 0, remoteChunks = 0;

  function isOurs(track) {
    for (const s of sessions) if (track === s.virtual || track === s.real) return true;
    return false;
  }

  function remoteTap(c) {
    if (remoteBus) return;
    remoteBus = c.createGain();
    const tap = c.createScriptProcessor(TAP_FRAMES, 1, 1);
    const sink = c.createGain();
    sink.gain.value = 0;
    remoteBus.connect(tap);
    tap.connect(sink).connect(c.destination);
    tap.onaudioprocess = e => {
      if (!mode.capture || !mode.listen || !remotes.size) { remoteResample = null; return; }
      const input = e.inputBuffer.getChannelData(0);
      let peak = 0;
      for (let i = 0; i < input.length; i += 8) { const v = Math.abs(input[i]); if (v > peak) peak = v; }
      const now = performance.now();
      if (peak > REMOTE_FLOOR) remoteLoudUntil = now + REMOTE_HANGOVER_MS;
      if (now > remoteLoudUntil) { remoteResample = null; return; }
      remoteResample = remoteResample || resampler(c.sampleRate);
      const pcm = remoteResample(input);
      if (!pcm.length) return;
      post({ t: 'remote', rate: CAPTURE_RATE, pcm: encode(pcm) });
      remoteChunks++;
    };
  }

  function scanRemotes() {
    if (!ctx || !mode.capture || !mode.listen) return;   // sin micrófono pedido todavía no hay reunión
    try {
      for (const el of document.querySelectorAll('audio, video')) mediaEls.add(el);
      for (const el of mediaEls) {
        const stream = el.srcObject;
        if (!stream || typeof stream.getAudioTracks !== 'function' || el.muted || el.paused) continue;
        for (const track of stream.getAudioTracks()) {
          if (track.readyState !== 'live' || remotes.has(track.id) || isOurs(track)) continue;
          remoteTap(ctx);
          const source = ctx.createMediaStreamSource(new MediaStream([track]));
          source.connect(remoteBus);
          remotes.set(track.id, { source, el, track });
        }
      }
      for (const [id, r] of remotes) {
        const stream = r.el.srcObject;
        if (r.track.readyState === 'live' && !r.el.muted && stream && stream.getAudioTracks().includes(r.track)) continue;
        try { r.source.disconnect(); } catch (e) { /* ya desconectada */ }
        remotes.delete(id);
      }
      for (const el of mediaEls) if (!el.isConnected && !el.srcObject) mediaEls.delete(el);
    } catch (e) { post({ t: 'error', message: 'remoto: ' + String(e && e.message || e) }); }
  }
  setInterval(scanRemotes, REMOTE_SCAN_MS);

  const srcObjectDesc = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'srcObject');
  if (srcObjectDesc && srcObjectDesc.set && srcObjectDesc.configurable) {
    Object.defineProperty(HTMLMediaElement.prototype, 'srcObject', {
      configurable: true, enumerable: srcObjectDesc.enumerable, get: srcObjectDesc.get,
      set(value) {
        srcObjectDesc.set.call(this, value);
        if (value) mediaEls.add(this);
      },
    });
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
    let resample = null;
    tap.onaudioprocess = e => {
      if (!mode.capture || capturing() !== session) { resample = null; return; }
      resample = resample || resampler(c.sampleRate);
      const pcm = resample(e.inputBuffer.getChannelData(0));
      if (!pcm.length) return;
      post({ t: 'mic', rate: CAPTURE_RATE, pcm: encode(pcm) });
      sentAt.push(performance.now());
      if (sentAt.length > 200) sentAt.shift();
    };

    // Para la página es el micrófono real: nombre, ajustes y silenciar pasan a la pista real.
    const virtual = dest.stream.getAudioTracks()[0];
    const session = { originalGain, virtual, real: realAudio };
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
      mode = { original: +m.original || 0, translated: +m.translated || 0, capture: !!m.capture, listen: !!m.listen };
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
        remotes: remotes.size, remoteChunks,
      });
    }
  });
  post({ t: 'ready' });
})();
