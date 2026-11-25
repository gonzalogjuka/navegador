// Compila el motor de traducción de Astro y lo deja en src/Navegador/Voz/, que viaja dentro de Astro.
//   cd voz && npm install && node build.mjs
// (Sólo para quien desarrolla: la persona que usa Astro nunca compila nada.)
import { build } from 'esbuild';
import { cpSync, mkdirSync } from 'node:fs';

const out = '../src/Navegador/Voz';
mkdirSync(`${out}/ort`, { recursive: true });
await build({
  entryPoints: ['src/engine.js'],
  outfile: `${out}/engine.js`,
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'chrome120',
  minify: true,
  logLevel: 'info',
});
cpSync('engine.html', `${out}/engine.html`);
for (const f of ['ort-wasm-simd-threaded.jsep.mjs', 'ort-wasm-simd-threaded.jsep.wasm']) {
  cpSync(`node_modules/@huggingface/transformers/dist/${f}`, `${out}/ort/${f}`);
}
