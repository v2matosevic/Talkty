// Generates the UI sound effects for the Talkty promo as WAV files.
// Zero dependencies. Output: promo/public/audio/*.wav
// (Background music is supplied separately as public/audio/music.mp3.)
import {writeFileSync, mkdirSync} from 'node:fs';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const SR = 44100;
const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'audio');
mkdirSync(OUT, {recursive: true});

const TAU = Math.PI * 2;
const clip = (x) => Math.max(-1, Math.min(1, x));
const mkStereo = (sec) => [new Float32Array((sec * SR) | 0), new Float32Array((sec * SR) | 0)];

function writeWav(name, L, R) {
  const n = L.length, buf = Buffer.alloc(44 + n * 4);
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 4, 4); buf.write('WAVE', 8);
  buf.write('fmt ', 12); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20);
  buf.writeUInt16LE(2, 22); buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 4, 28);
  buf.writeUInt16LE(4, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(n * 4, 40);
  let o = 44;
  for (let i = 0; i < n; i++) { buf.writeInt16LE((clip(L[i]) * 32767) | 0, o); buf.writeInt16LE((clip(R[i]) * 32767) | 0, o + 2); o += 4; }
  writeFileSync(join(OUT, name), buf);
  console.log('  ' + name + '  ' + (n / SR).toFixed(2) + 's');
}

function normalize(L, R, target = 0.85) {
  let peak = 0;
  for (let i = 0; i < L.length; i++) peak = Math.max(peak, Math.abs(L[i]), Math.abs(R[i]));
  if (peak < 1e-6) return;
  const g = target / peak;
  for (let i = 0; i < L.length; i++) { L[i] *= g; R[i] *= g; }
}

// ---- synth primitives ----
// envelope: soft attack, then exponential decay (percussive)
function tone(buf, start, freq, amp, decay, attack = 0.004, type = 'sine') {
  const s0 = (start * SR) | 0, total = ((attack + decay) * SR) | 0;
  for (let i = 0; i < total; i++) {
    const idx = s0 + i; if (idx < 0 || idx >= buf.length) continue;
    const t = i / SR;
    const e = t < attack ? t / attack : Math.exp(-(t - attack) / (decay * 0.34));
    const ph = TAU * freq * t;
    let s = type === 'tri' ? (2 / Math.PI) * Math.asin(Math.sin(ph)) : Math.sin(ph);
    buf[idx] += s * e * amp;
  }
}
// glide tone: frequency ramps f0 -> f1 over the decay
function glide(buf, start, f0, f1, amp, decay, attack = 0.005) {
  const s0 = (start * SR) | 0, total = ((attack + decay) * SR) | 0;
  let ph = 0;
  for (let i = 0; i < total; i++) {
    const idx = s0 + i; if (idx < 0 || idx >= buf.length) continue;
    const t = i / SR, k = Math.min(1, t / (attack + decay));
    const f = f0 + (f1 - f0) * k;
    ph += TAU * f / SR;
    const e = t < attack ? t / attack : Math.exp(-(t - attack) / (decay * 0.4));
    buf[idx] += Math.sin(ph) * e * amp;
  }
}
// bell: fundamental + inharmonic partials -> a soft, clean chime
function bell(L, R, start, freq, amp, decay, pan = 0.5) {
  const parts = [[1, 1, 1], [2.01, 0.5, 0.82], [2.76, 0.26, 0.62], [3.84, 0.12, 0.45], [5.4, 0.06, 0.32]];
  for (const [r, a, d] of parts) {
    tone(L, start, freq * r, amp * a * (0.6 + 0.8 * pan) * 0.5, decay * d);
    tone(R, start, freq * r, amp * a * (0.6 + 0.8 * (1 - pan)) * 0.5, decay * d);
  }
}
// warm filtered noise (1-pole low-pass), with an amplitude envelope fn(t in 0..dur)
function noise(buf, start, dur, amp, cutoff, envFn) {
  const s0 = (start * SR) | 0, total = (dur * SR) | 0;
  let lp = 0;
  for (let i = 0; i < total; i++) {
    const idx = s0 + i; if (idx < 0 || idx >= buf.length) continue;
    const t = i / SR;
    const a = typeof cutoff === 'function' ? cutoff(t / dur) : cutoff;
    lp += a * ((Math.random() * 2 - 1) - lp);
    buf[idx] += lp * amp * envFn(t / dur);
  }
}

// ---- Schroeder reverb (Freeverb-lite), per channel ----
function comb(x, d, fb, damp) {
  const y = new Float32Array(x.length), buf = new Float32Array(d);
  let i = 0, store = 0;
  for (let n = 0; n < x.length; n++) { const o = buf[i]; store = o * (1 - damp) + store * damp; buf[i] = x[n] + store * fb; y[n] = o; i = (i + 1) % d; }
  return y;
}
function allpass(x, d, g) {
  const y = new Float32Array(x.length), buf = new Float32Array(d);
  let i = 0;
  for (let n = 0; n < x.length; n++) { const bo = buf[i]; const o = -x[n] + bo; buf[i] = x[n] + bo * g; y[n] = o; i = (i + 1) % d; }
  return y;
}
function reverbCh(x, decay, mix, damp, spread) {
  const ds = [1116, 1188, 1277, 1356, 1422, 1491].map((d) => d + spread);
  const wet = new Float32Array(x.length);
  for (const d of ds) { const c = comb(x, d, decay, damp); for (let n = 0; n < x.length; n++) wet[n] += c[n]; }
  for (let n = 0; n < x.length; n++) wet[n] /= ds.length;
  let w = allpass(wet, 556, 0.5); w = allpass(w, 441, 0.5); w = allpass(w, 341, 0.5);
  const out = new Float32Array(x.length);
  for (let n = 0; n < x.length; n++) out[n] = x[n] * (1 - mix) + w[n] * mix * 1.7;
  return out;
}
function finish(name, L, R, {decay = 0.84, mix = 0.28, damp = 0.28, peak = 0.85} = {}) {
  if (mix > 0) {
    const nl = reverbCh(L, decay, mix, damp, 0);
    const nr = reverbCh(R, decay, mix, damp, 23);
    L.set(nl); R.set(nr);
  }
  // gentle soft-clip for glue
  for (let i = 0; i < L.length; i++) { L[i] = Math.tanh(L[i] * 1.05); R[i] = Math.tanh(R[i] * 1.05); }
  normalize(L, R, peak);
  writeWav(name, L, R);
}

// =================== SOUND EFFECTS ===================

// key press: a soft mechanical "thock", not a beep
function sfxClick() {
  const [L, R] = mkStereo(0.22);
  noise(L, 0, 0.014, 0.5, 0.85, (t) => Math.exp(-t * 6)); noise(R, 0, 0.014, 0.5, 0.85, (t) => Math.exp(-t * 6));
  tone(L, 0.0, 168, 0.5, 0.05); tone(R, 0.0, 168, 0.5, 0.05);
  tone(L, 0.001, 2600, 0.10, 0.012); tone(R, 0.001, 2600, 0.10, 0.012);
  finish('click.wav', L, R, {mix: 0.10, decay: 0.6, peak: 0.8});
}

// pill appears: a soft, bright UI pop that glides up
function sfxPop() {
  const [L, R] = mkStereo(0.6);
  noise(L, 0, 0.02, 0.22, 0.7, (t) => Math.exp(-t * 7)); noise(R, 0, 0.02, 0.22, 0.7, (t) => Math.exp(-t * 7));
  glide(L, 0.0, 440, 820, 0.34, 0.16); glide(R, 0.0, 440, 820, 0.34, 0.16);
  tone(L, 0.02, 1240, 0.12, 0.12); tone(R, 0.02, 1240, 0.12, 0.12);
  finish('pop.wav', L, R, {mix: 0.22, decay: 0.8, peak: 0.82});
}

// copied / success: a warm two-note bell chime (perfect fifth)
function sfxDing() {
  const [L, R] = mkStereo(1.7);
  bell(L, R, 0.0, 1046.5, 0.6, 1.0, 0.42);  // C6
  bell(L, R, 0.05, 1568.0, 0.5, 1.1, 0.58); // G6
  finish('ding.wav', L, R, {mix: 0.32, decay: 0.86, damp: 0.22, peak: 0.8});
}

// prompting sparkle: a glittery ascending pentatonic run
function sfxShimmer() {
  const [L, R] = mkStereo(1.7);
  const notes = [1046.5, 1174.7, 1318.5, 1568.0, 1760.0, 2093.0]; // C6 D6 E6 G6 A6 C7
  notes.forEach((f, i) => bell(L, R, i * 0.04, f, 0.3, 0.7 + i * 0.05, i % 2 ? 0.34 : 0.66));
  finish('shimmer.wav', L, R, {mix: 0.4, decay: 0.87, damp: 0.18, peak: 0.74});
}

// transition swoosh: warm filtered noise that swells and sweeps, panning across
function sfxWhoosh() {
  const [L, R] = mkStereo(0.75);
  const env = (t) => Math.sin(Math.PI * Math.min(1, t)) ** 1.4;
  const cut = (t) => 0.06 + 0.5 * Math.sin(Math.PI * t); // open then close
  noise(L, 0, 0.6, 0.34, cut, (t) => env(t) * (1 - t * 0.4));
  noise(R, 0.03, 0.6, 0.34, cut, (t) => env(t) * (0.6 + t * 0.4));
  finish('whoosh.wav', L, R, {mix: 0.22, decay: 0.7, peak: 0.62});
}

// outro riser: noise rising in pitch + brightness, resolving to a soft chord
function sfxRiser() {
  const [L, R] = mkStereo(1.8);
  const cut = (t) => 0.04 + 0.55 * t * t;
  noise(L, 0, 1.15, 0.3, cut, (t) => Math.min(1, t * 1.2) * (t < 0.95 ? 1 : Math.exp(-(t - 0.95) * 12)));
  noise(R, 0, 1.15, 0.3, cut, (t) => Math.min(1, t * 1.2) * (t < 0.95 ? 1 : Math.exp(-(t - 0.95) * 12)));
  glide(L, 0.0, 220, 1100, 0.16, 1.1); glide(R, 0.0, 220, 1100, 0.16, 1.1);
  // resolve: soft major chord (A major-ish) bells
  [880, 1108.7, 1318.5].forEach((f, i) => bell(L, R, 1.02, f, 0.26, 0.7, i % 2 ? 0.4 : 0.6));
  finish('riser.wav', L, R, {mix: 0.32, decay: 0.85, peak: 0.78});
}

console.log('Generating sound effects...');
sfxClick(); sfxPop(); sfxDing(); sfxShimmer(); sfxWhoosh(); sfxRiser();
console.log('Done -> ' + OUT);
