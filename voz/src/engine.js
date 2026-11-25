// Motor de traducción simultánea de Astro (es→en). Adaptado de gonzalogjuka/voz-en para correr DENTRO de Astro:
//   tu voz (PCM 16 kHz que manda Astro) → detector de pausas → Whisper (voz en español → texto en inglés, local)
//   → Kokoro (voz inglesa, local) → PCM 24 kHz que Astro mete en el micrófono virtual de la reunión.
// Corre en una página interna oculta (https://astro-voz.local/engine.html) y habla con Astro por chrome.webview.
// Mensajes de Astro: {t:'start'} · {t:'mic', tab, pcm} · {t:'config', ...} · {t:'test', audio(base64), ...}
// Mensajes a Astro: status {phase, detail, progress} · ready {asr, tts, backend, loadMs} · play {tab, rate, pcm}
//                   phrase {tab, en, ms…} · error {message} · test-result {...}
import { pipeline, env } from '@huggingface/transformers';
import { KokoroTTS } from 'kokoro-js';

env.allowLocalModels = false;            // los modelos vienen de Hugging Face la primera vez y quedan en caché
env.backends.onnx.wasm.wasmPaths = './ort/';

const wv = window.chrome && window.chrome.webview;
const post = m => { try { wv && wv.postMessage(m); } catch (e) { /* Astro se está cerrando */ } };
const params = new URLSearchParams(location.search);

const cfg = {
  asrModel: params.get('asr') || 'onnx-community/whisper-base',
  ttsModel: 'onnx-community/Kokoro-82M-v1.0-ONNX',
  voice: 'af_heart',
  speed: 1.0,
  pauseMs: 450,      // silencio que corta una frase (voz-en: 450 ms daba mejor traducción que cortar por palabras)
  maxPhraseMs: 8000, // frase más larga que esto se corta igual, para no acumular demora
  minSpeechMs: 300,  // menos que esto es ruido, no una frase
  // Afinación (se elige midiendo en una PC común; ver docs/PLAN.md 7.4):
  mode: params.get('mode') || 'translate',   // translate = Whisper da inglés directo · mt = Whisper en español + opus-mt
  asrDtype: params.get('asrDtype') || '',    // '' = automático · 'fp32' · 'fp16' · 'q8' · 'enc,dec' (p. ej. 'fp16,q4')
  ttsDevice: params.get('ttsDevice') || '',  // '' = GPU si hay · 'wasm'
  ttsDtype: params.get('ttsDtype') || '',
  // Voz inglesa: 'host' = la dice Astro con la voz de Windows (instantánea: ~10 ms por frase contra ~2 s de Kokoro en
  // una GPU integrada) · 'kokoro' = voz más natural, local, pero lenta sin una GPU dedicada.
  tts: params.get('tts') || 'kokoro',
};
let mt = null;

const RATE_IN = 16000;
let asr = null, tts = null, backend = 'wasm', loading = null;

// ---------- Carga de modelos (una vez; después salen de la caché) ----------

function progress(label) {
  const files = new Map();
  return p => {
    if (p.status === 'progress' && p.total) {
      files.set(p.file, [p.loaded, p.total]);
      let loaded = 0, total = 0;
      for (const [l, t] of files.values()) { loaded += l; total += t; }
      post({ t: 'status', phase: 'downloading', detail: label, progress: total ? loaded / total : 0, mb: Math.round(total / 1e6) });
    }
  };
}

async function load() {
  if (loading) return loading;
  loading = (async () => {
    const t0 = performance.now();
    let gpu = false;
    try { gpu = !!(navigator.gpu && await navigator.gpu.requestAdapter()); } catch (e) { gpu = false; }
    backend = params.get('device') || (gpu ? 'webgpu' : 'wasm'); // 'wasm' fuerza el procesador (medición de RAM)
    post({ t: 'status', phase: 'loading', detail: 'reconocimiento de voz', backend });
    const dt = cfg.asrDtype.split(',');
    const asrDtype = !cfg.asrDtype ? (gpu ? { encoder_model: 'fp32', decoder_model_merged: 'q4' } : 'q8')
      : dt.length === 2 ? { encoder_model: dt[0], decoder_model_merged: dt[1] } : dt[0];
    asr = await pipeline('automatic-speech-recognition', cfg.asrModel, {
      device: backend, dtype: asrDtype, progress_callback: progress('reconocimiento de voz'),
    });
    if (cfg.mode === 'mt') {
      post({ t: 'status', phase: 'loading', detail: 'traductor', backend });
      mt = await pipeline('translation', 'Xenova/opus-mt-es-en', { device: 'wasm', dtype: 'q8', progress_callback: progress('traductor') });
    }
    const ttsDevice = cfg.ttsDevice || backend;
    if (cfg.tts === 'kokoro') {
      post({ t: 'status', phase: 'loading', detail: 'voz inglesa', backend });
      tts = await KokoroTTS.from_pretrained(cfg.ttsModel, {
        device: ttsDevice, dtype: cfg.ttsDtype || (ttsDevice === 'webgpu' ? 'fp32' : 'q8'), progress_callback: progress('voz inglesa'),
      });
    }
    // Precalentar: la primera pasada compila los sombreadores de la GPU y tarda mucho más que las siguientes.
    await asr(new Float32Array(RATE_IN), { language: 'spanish', task: cfg.mode === 'mt' ? 'transcribe' : 'translate' });
    if (mt) await mt('Hola.');
    if (tts) await tts.generate('Ready.', { voice: cfg.voice });
    const loadMs = Math.round(performance.now() - t0);
    post({ t: 'ready', asr: `${cfg.asrModel} ${cfg.mode} ${cfg.asrDtype || 'auto'}`, tts: cfg.tts === 'host' ? 'host' : `kokoro:${ttsDevice}`, backend, loadMs });
  })().catch(e => { loading = null; post({ t: 'error', message: 'No se pudo preparar la traducción: ' + (e && e.message || e) }); throw e; });
  return loading;
}

// ---------- Frase: voz → inglés → voz inglesa ----------

async function translatePhrase(samples) {
  const t0 = performance.now();
  // Tope de largo: Whisper a veces entra en bucle ("I'm going to say I'm going to say…"); ~6 palabras por segundo de voz alcanza.
  const maxTokens = Math.min(160, Math.ceil(samples.length / RATE_IN * 6) + 10);
  const out = await asr(samples, { language: 'spanish', task: cfg.mode === 'mt' ? 'transcribe' : 'translate', max_new_tokens: maxTokens });
  let en = (out.text || '').trim();
  if (mt && en) en = ((await mt(en))[0].translation_text || '').trim();
  const t1 = performance.now();
  // Whisper inventa frases típicas sobre silencio o ruido: se descartan.
  if (!en || /^\W*$/.test(en) || /^(thank you|thanks for watching|you)\W*$/i.test(en)) return { en: '', asrMs: Math.round(t1 - t0), ttsMs: 0 };
  if (!tts) return { en, asrMs: Math.round(t1 - t0), ttsMs: 0 }; // la voz la pone Astro
  const audio = await tts.generate(en, { voice: cfg.voice, speed: cfg.speed });
  const t2 = performance.now();
  return { en, audio: audio.audio, rate: audio.sampling_rate, asrMs: Math.round(t1 - t0), ttsMs: Math.round(t2 - t1) };
}

function toBase64(f32) {
  const i16 = new Int16Array(f32.length);
  for (let i = 0; i < f32.length; i++) { const v = Math.max(-1, Math.min(1, f32[i])); i16[i] = v < 0 ? v * 0x8000 : v * 0x7fff; }
  const bytes = new Uint8Array(i16.buffer);
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(bin);
}

function fromBase64(b64) {
  const bin = atob(b64);
  const n = bin.length >> 1, f = new Float32Array(n);
  for (let i = 0; i < n; i++) { let v = bin.charCodeAt(2 * i) | (bin.charCodeAt(2 * i + 1) << 8); if (v >= 0x8000) v -= 0x10000; f[i] = v / 0x8000; }
  return f;
}

// ---------- Detector de pausas (por pestaña) ----------
// Nivel por bloque de 20 ms contra un piso de ruido que se adapta. Hay voz si supera el piso ×3 (y un mínimo fijo).

class Segmenter {
  constructor(onPhrase) { this.onPhrase = onPhrase; this.buf = []; this.len = 0; this.speechMs = 0; this.silenceMs = 0; this.floor = 0.005; this.active = false; this.lastVoiceAt = 0; }
  push(f32) {
    const block = RATE_IN / 50; // 20 ms
    for (let i = 0; i < f32.length; i += block) {
      const part = f32.subarray(i, Math.min(f32.length, i + block));
      let s = 0; for (const v of part) s += v * v;
      const rms = Math.sqrt(s / part.length);
      const voice = rms > Math.max(0.012, this.floor * 3);
      if (!voice) this.floor = this.floor * 0.98 + rms * 0.02;
      const ms = part.length / RATE_IN * 1000;
      if (voice) {
        if (!this.active) { this.active = true; this.speechMs = 0; }
        this.speechMs += ms; this.silenceMs = 0; this.lastVoiceAt = performance.now();
      } else if (this.active) {
        this.silenceMs += ms;
      }
      if (this.active || this.len > 0) { this.buf.push(part.slice()); this.len += part.length; }
      const phraseMs = this.len / RATE_IN * 1000;
      if (this.active && (this.silenceMs >= cfg.pauseMs || phraseMs >= cfg.maxPhraseMs)) this.flush(this.silenceMs >= cfg.pauseMs ? 'pausa' : 'larga');
      else if (!this.active && this.len > RATE_IN * 0.3) { this.buf = []; this.len = 0; } // colita sin voz: afuera
    }
  }
  flush(reason) {
    const speech = this.speechMs;
    const all = new Float32Array(this.len);
    let o = 0; for (const b of this.buf) { all.set(b, o); o += b.length; }
    this.buf = []; this.len = 0; this.active = false; this.silenceMs = 0; this.speechMs = 0;
    if (speech >= cfg.minSpeechMs) this.onPhrase(all, reason, this.lastVoiceAt);
  }
}

// Una frase a la vez por la GPU; si se atrasan, se juntan para no acumular demora.
const queue = [];
let busy = false;
async function pump() {
  if (busy) return;
  busy = true;
  try {
    while (queue.length) {
      let job = queue.shift();
      while (queue.length && queue[0].tab === job.tab) { // atraso: se unen las frases pendientes de la misma pestaña
        const next = queue.shift();
        const joined = new Float32Array(job.samples.length + next.samples.length);
        joined.set(job.samples); joined.set(next.samples, job.samples.length);
        job = { ...next, samples: joined, merged: (job.merged || 1) + 1 };
      }
      const r = await translatePhrase(job.samples);
      if (!r.en) continue;
      if (r.audio) post({ t: 'play', tab: job.tab, rate: r.rate, pcm: toBase64(r.audio) });
      else post({ t: 'speak', tab: job.tab, en: r.en });
      post({
        t: 'phrase', tab: job.tab, en: r.en, reason: job.reason, merged: job.merged || 1,
        audioMs: Math.round(job.samples.length / RATE_IN * 1000), asrMs: r.asrMs, ttsMs: r.ttsMs,
        totalMs: Math.round(performance.now() - job.lastVoiceAt), // de tu última palabra a que sale la voz inglesa
      });
    }
  } catch (e) {
    post({ t: 'error', message: String(e && e.message || e) });
  } finally {
    busy = false;
  }
}

const segmenters = new Map();
function onMic(tab, pcm) {
  if (!asr || (!tts && cfg.tts === 'kokoro')) return; // todavía cargando: la reunión escucha tu voz original (Astro decide la mezcla)
  let s = segmenters.get(tab);
  if (!s) segmenters.set(tab, s = new Segmenter((samples, reason, lastVoiceAt) => { queue.push({ tab, samples, reason, lastVoiceAt }); pump(); }));
  s.push(fromBase64(pcm));
}

// ---------- Prueba: un audio completo, como si llegara del micrófono en tiempo real ----------

async function test(b64) {
  const bin = atob(b64), bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  const ctx = new OfflineAudioContext(1, 1, RATE_IN);
  const decoded = await ctx.decodeAudioData(bytes.buffer);
  const off = new OfflineAudioContext(1, Math.ceil(decoded.duration * RATE_IN), RATE_IN);
  const src = off.createBufferSource(); src.buffer = decoded; src.connect(off.destination); src.start();
  const samples = (await off.startRendering()).getChannelData(0);
  const phrases = [];
  const seg = new Segmenter((s, reason, lastVoiceAt) => phrases.push({ s, reason }));
  for (let i = 0; i < samples.length; i += 320) seg.push(samples.subarray(i, i + 320));
  if (seg.len) seg.flush('fin');
  const results = [];
  for (const p of phrases) {
    const r = await translatePhrase(p.s);
    results.push({ audioMs: Math.round(p.s.length / RATE_IN * 1000), reason: p.reason, en: r.en, asrMs: r.asrMs, ttsMs: r.ttsMs,
      ttsAudioMs: r.audio ? Math.round(r.audio.length / r.rate * 1000) : 0 });
  }
  post({ t: 'test-result', durationMs: Math.round(decoded.duration * 1000), backend, asr: cfg.asrModel, phrases: results });
}

wv && wv.addEventListener('message', async e => {
  const m = e.data;
  if (!m || typeof m !== 'object') return;
  try {
    if (m.t === 'start') await load();
    else if (m.t === 'mic' && typeof m.pcm === 'string') onMic(m.tab, m.pcm);
    else if (m.t === 'config') Object.assign(cfg, m.config || {});
    else if (m.t === 'test' && typeof m.audio === 'string') { await load(); await test(m.audio); }
  } catch (err) {
    post({ t: 'error', message: String(err && err.message || err) });
  }
});
post({ t: 'hello', asr: cfg.asrModel });
