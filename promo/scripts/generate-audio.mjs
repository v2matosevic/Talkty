// Generates the music bed + UI sound effects for the Talkty promo as WAV files.
// Zero dependencies. Output: promo/public/audio/*.wav
import {writeFileSync, mkdirSync} from 'node:fs';
import {dirname, join} from 'node:path';
import {fileURLToPath} from 'node:url';

const SR = 44100;
const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', 'public', 'audio');
mkdirSync(OUT, {recursive: true});

const midi = (m) => 440 * Math.pow(2, (m - 69) / 12);
const clamp = (x) => Math.max(-1, Math.min(1, x));
const TAU = Math.PI * 2;

// ---- WAV writer (16-bit PCM stereo) ----
function writeWav(name, L, R) {
  const n = L.length;
  const buf = Buffer.alloc(44 + n * 4);
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 4, 4); buf.write('WAVE', 8);
  buf.write('fmt ', 12); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20);
  buf.writeUInt16LE(2, 22); buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 4, 28);
  buf.writeUInt16LE(4, 32); buf.writeUInt16LE(16, 34);
  buf.write('data', 36); buf.writeUInt32LE(n * 4, 40);
  let o = 44;
  for (let i = 0; i < n; i++) {
    buf.writeInt16LE((clamp(L[i]) * 32767) | 0, o); o += 2;
    buf.writeInt16LE((clamp(R[i]) * 32767) | 0, o); o += 2;
  }
  writeFileSync(join(OUT, name), buf);
  console.log('  ' + name + '  ' + (n / SR).toFixed(1) + 's');
}

const mkStereo = (sec) => [new Float32Array((sec * SR) | 0), new Float32Array((sec * SR) | 0)];
function normalize(L, R, target = 0.9) {
  let peak = 0;
  for (let i = 0; i < L.length; i++) peak = Math.max(peak, Math.abs(L[i]), Math.abs(R[i]));
  if (peak < 1e-6) return;
  const g = target / peak;
  for (let i = 0; i < L.length; i++) { L[i] *= g; R[i] *= g; }
}
// soft clip for glue
const sat = (x) => Math.tanh(x * 1.1);

// add a voice: type in {sine,tri,saw,soft}; exp/AR envelope
function addVoice(L, R, start, dur, freq, amp, type, panL = 0.5, panR = 0.5, attack = 0.01, release = 0.1, detune = 0) {
  const s0 = (start * SR) | 0;
  const total = ((dur + release) * SR) | 0;
  for (let i = 0; i < total; i++) {
    const idx = s0 + i;
    if (idx < 0 || idx >= L.length) continue;
    const t = i / SR;
    // envelope: linear attack, sustain, exp release
    let e;
    if (t < attack) e = t / attack;
    else if (t < dur) e = 1;
    else e = Math.exp(-(t - dur) / (release * 0.5));
    const ph = TAU * freq * t;
    const ph2 = TAU * (freq * (1 + detune)) * t;
    let s;
    if (type === 'sine') s = Math.sin(ph);
    else if (type === 'tri') s = (2 / Math.PI) * Math.asin(Math.sin(ph));
    else if (type === 'saw') s = 2 * (t * freq - Math.floor(0.5 + t * freq));
    else if (type === 'soft') s = 0.7 * Math.sin(ph) + 0.3 * Math.sin(ph2) + 0.12 * Math.sin(ph * 2);
    else s = Math.sin(ph);
    const v = s * e * amp;
    L[idx] += v * panL; R[idx] += v * panR;
  }
}

function noiseBurst(L, R, start, dur, amp, decay, bright = 1) {
  const s0 = (start * SR) | 0; const total = (dur * SR) | 0;
  let last = 0;
  for (let i = 0; i < total; i++) {
    const idx = s0 + i; if (idx < 0 || idx >= L.length) continue;
    const t = i / SR;
    const e = Math.exp(-t / decay);
    let n = Math.random() * 2 - 1;
    n = last + bright * (n - last); last = n; // simple LP for darker noise when bright<1
    const v = n * e * amp;
    L[idx] += v; R[idx] += v;
  }
}

// =================== MUSIC BED ===================
function music() {
  const BPM = 96, beat = 60 / BPM, bar = beat * 4, eighth = beat / 2;
  const BARS = 16;
  const dur = bar * BARS + 1.2;
  const [L, R] = mkStereo(dur);

  // A minor: i - VI - III - VII  => Am, F, C, G
  const chords = [
    {pad: [57, 60, 64], bass: 45, arp: [69, 72, 76, 72, 69, 72, 76, 79]}, // Am
    {pad: [53, 57, 60], bass: 41, arp: [65, 69, 72, 69, 65, 69, 72, 77]}, // F
    {pad: [60, 64, 67], bass: 48, arp: [72, 76, 79, 76, 72, 76, 79, 84]}, // C
    {pad: [55, 59, 62], bass: 43, arp: [67, 71, 74, 71, 67, 71, 74, 79]}, // G
  ];

  for (let b = 0; b < BARS; b++) {
    const c = chords[b % 4];
    const t0 = b * bar;
    const fade = b >= BARS - 1 ? 0.55 : 1; // gentle outro on last bar
    // Pads (always) - warm soft stack
    c.pad.forEach((m, k) => {
      addVoice(L, R, t0, bar, midi(m), 0.11 * fade, 'soft',
        0.6 - k * 0.08, 0.4 + k * 0.08, 0.5, 0.7, 0.004);
    });
    // Bass from bar 2
    if (b >= 2) {
      addVoice(L, R, t0, beat * 1.8, midi(c.bass), 0.20 * fade, 'sine', 0.5, 0.5, 0.01, 0.25);
      addVoice(L, R, t0 + beat * 2, beat * 1.8, midi(c.bass), 0.18 * fade, 'sine', 0.5, 0.5, 0.01, 0.25);
    }
    // Arp from bar 4
    if (b >= 4) {
      for (let e = 0; e < 8; e++) {
        const t = t0 + e * eighth;
        const pan = e % 2 ? [0.35, 0.65] : [0.65, 0.35];
        addVoice(L, R, t, eighth * 0.9, midi(c.arp[e]), 0.085 * fade, 'tri', pan[0], pan[1], 0.005, 0.18);
        // soft delay tap
        addVoice(L, R, t + eighth * 1.5, eighth * 0.6, midi(c.arp[e]), 0.035 * fade, 'tri', pan[1], pan[0], 0.005, 0.15);
      }
    }
    // Drums from bar 8
    if (b >= 8 && b < BARS - 1) {
      for (let bt = 0; bt < 4; bt++) {
        const t = t0 + bt * beat;
        // kick on 1 and 3
        if (bt === 0 || bt === 2) {
          const ks = (t * SR) | 0;
          for (let i = 0; i < (0.16 * SR) | 0; i++) {
            const idx = ks + i; if (idx >= L.length) break;
            const tt = i / SR;
            const f = 110 * Math.exp(-tt / 0.03) + 44;
            const e = Math.exp(-tt / 0.10);
            const v = Math.sin(TAU * f * tt) * e * 0.5;
            L[idx] += v; R[idx] += v;
          }
        }
        // soft hat on offbeats
        noiseBurst(L, R, t + beat * 0.5, 0.05, 0.05, 0.018, 0.85);
      }
    }
  }

  for (let i = 0; i < L.length; i++) { L[i] = sat(L[i]); R[i] = sat(R[i]); }
  normalize(L, R, 0.82);
  writeWav('music.wav', L, R);
}

// =================== SOUND EFFECTS ===================
function sfxPop() { // pill appears
  const [L, R] = mkStereo(0.5);
  noiseBurst(L, R, 0, 0.18, 0.18, 0.05, 0.5);
  addVoice(L, R, 0.0, 0.18, 520, 0.22, 'sine', 0.5, 0.5, 0.005, 0.16);
  addVoice(L, R, 0.02, 0.2, 780, 0.16, 'sine', 0.5, 0.5, 0.005, 0.18);
  normalize(L, R, 0.8); writeWav('pop.wav', L, R);
}
function sfxClick() { // key press / paste
  const [L, R] = mkStereo(0.18);
  noiseBurst(L, R, 0, 0.05, 0.5, 0.012, 0.9);
  addVoice(L, R, 0, 0.05, 1400, 0.25, 'sine', 0.5, 0.5, 0.001, 0.03);
  normalize(L, R, 0.85); writeWav('click.wav', L, R);
}
function sfxDing() { // copied / success — bell major third
  const [L, R] = mkStereo(1.2);
  addVoice(L, R, 0, 0.9, midi(84), 0.30, 'sine', 0.5, 0.5, 0.002, 0.7);   // C6
  addVoice(L, R, 0.0, 0.9, midi(88), 0.20, 'sine', 0.5, 0.5, 0.002, 0.7); // E6
  addVoice(L, R, 0.0, 0.9, midi(91), 0.12, 'sine', 0.5, 0.5, 0.002, 0.7); // G6
  addVoice(L, R, 0.005, 0.6, midi(96), 0.08, 'sine', 0.5, 0.5, 0.002, 0.5);
  normalize(L, R, 0.78); writeWav('ding.wav', L, R);
}
function sfxShimmer() { // prompting sparkle — ascending pentatonic
  const [L, R] = mkStereo(1.0);
  const notes = [84, 88, 91, 96, 98];
  notes.forEach((m, i) => {
    addVoice(L, R, i * 0.045, 0.5, midi(m), 0.18, 'sine', 0.4 + i * 0.05, 0.6 - i * 0.05, 0.003, 0.45);
  });
  normalize(L, R, 0.7); writeWav('shimmer.wav', L, R);
}
function sfxWhoosh() { // transitions
  const [L, R] = mkStereo(0.7);
  const total = (0.6 * SR) | 0; let last = 0;
  for (let i = 0; i < total; i++) {
    const t = i / SR;
    const e = Math.sin(Math.PI * (t / 0.6)); // swell in-out
    let n = Math.random() * 2 - 1; n = last + 0.25 * (n - last); last = n;
    const v = n * e * 0.3;
    const pan = t / 0.6;
    L[i] += v * (1 - pan); R[i] += v * pan;
  }
  normalize(L, R, 0.6); writeWav('whoosh.wav', L, R);
}
function sfxRiser() { // outro
  const [L, R] = mkStereo(1.4);
  const total = (1.2 * SR) | 0; let last = 0;
  for (let i = 0; i < total; i++) {
    const t = i / SR; const e = Math.min(1, t / 1.0) * Math.exp(-Math.max(0, t - 1.0) / 0.15);
    let n = Math.random() * 2 - 1; n = last + (0.1 + 0.6 * (t / 1.2)) * (n - last); last = n;
    const sweep = Math.sin(TAU * (200 + 900 * (t / 1.2)) * t) * 0.15;
    const v = (n * 0.18 + sweep) * e;
    L[i] += v; R[i] += v;
  }
  // resolve note
  addVoice(L, R, 1.0, 0.4, midi(81), 0.25, 'soft', 0.5, 0.5, 0.005, 0.35);
  normalize(L, R, 0.75); writeWav('riser.wav', L, R);
}

console.log('Generating audio...');
music();
sfxPop(); sfxClick(); sfxDing(); sfxShimmer(); sfxWhoosh(); sfxRiser();
console.log('Done -> ' + OUT);
