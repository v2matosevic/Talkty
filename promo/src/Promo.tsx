import React from 'react';
import {
  AbsoluteFill, Sequence, Audio, staticFile, useCurrentFrame, useVideoConfig,
  interpolate, spring, Easing,
} from 'remotion';
import {C, FONT, MONO} from './theme';
import {Pill, Wordmark, Spark} from './components';

// ---- scene timing (frames @30fps), total 1200 = 40s ----
const SC = {
  intro: [0, 90],
  record: [90, 540],   // hotkey + speak + transcribe + copied, one continuous pill
  paste: [540, 684],
  prompt: [684, 942],
  privacy: [942, 1062],
  outro: [1062, 1200],
} as const;
const len = (k: keyof typeof SC) => SC[k][1] - SC[k][0];
const OVERLAP = 18;

const ease = {easing: Easing.out(Easing.cubic), extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
const easeIO = {easing: Easing.inOut(Easing.cubic), extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
const clamp = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;

const useU = () => {
  const {width, height} = useVideoConfig();
  return Math.min(width, height) / 1080;
};

// Push transition: each scene rises in from below + fades, holds, then drifts up + fades
// while the next scene rises over it. The closer fades to black at the very end.
const SceneWrap: React.FC<{len: number; closer?: boolean; rise?: number; children: React.ReactNode}> = ({len: L, closer, rise = 44, children}) => {
  const f = useCurrentFrame();
  const u = useU();
  const op = closer
    ? interpolate(f, [0, 18, L - 22, L], [0, 1, 1, 0], clamp)
    : interpolate(f, [0, 16, L, L + OVERLAP], [0, 1, 1, 0], clamp);
  const y = closer
    ? interpolate(f, [0, 18], [rise * u, 0], ease)
    : interpolate(f, [0, 16, L, L + OVERLAP], [rise * u, 0, 0, -32 * u], ease);
  return <AbsoluteFill style={{opacity: op, transform: `translateY(${y}px)`}}>{children}</AbsoluteFill>;
};

const Background: React.FC = () => {
  const f = useCurrentFrame();
  const drift = Math.sin(f / 130) * 9;
  const scale = 1.02 + 0.025 * (0.5 + 0.5 * Math.sin(f / 190));
  return (
    <AbsoluteFill>
      <AbsoluteFill style={{background: 'radial-gradient(130% 90% at 50% -12%, #1c1c23 0%, #0d0d10 50%, #08080a 100%)'}} />
      <AbsoluteFill style={{
        backgroundImage: 'radial-gradient(rgba(255,255,255,0.05) 1px, transparent 1.4px)',
        backgroundSize: '46px 46px', opacity: 0.26,
        transform: `translateY(${drift}px) scale(${scale})`,
        WebkitMaskImage: 'radial-gradient(72% 64% at 50% 46%, #000, transparent 80%)',
      }} />
    </AbsoluteFill>
  );
};

const Center: React.FC<{children: React.ReactNode; style?: React.CSSProperties}> = ({children, style}) => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', flexDirection: 'column', ...style}}>{children}</AbsoluteFill>
);

const Eyebrow: React.FC<{u: number; children: React.ReactNode; o?: number}> = ({u, children, o = 1}) => (
  <div style={{
    position: 'absolute', top: '13.5%', width: '100%', textAlign: 'center', opacity: o,
    fontFamily: FONT, fontSize: 30 * u, color: C.zinc500, letterSpacing: '.07em', textTransform: 'uppercase',
  }}>{children}</div>
);

const Keycap: React.FC<{u: number; label: string; pressed: number}> = ({u, label, pressed}) => (
  <div style={{
    fontFamily: FONT, fontSize: 42 * u, fontWeight: 600, color: C.zinc100,
    background: `linear-gradient(180deg, ${C.zinc800}, ${C.zinc900})`,
    border: `${2 * u}px solid ${C.zinc700}`, borderRadius: 16 * u,
    padding: `${22 * u}px ${30 * u}px`, minWidth: 96 * u, textAlign: 'center',
    boxShadow: `0 ${(8 - pressed * 6) * u}px 0 ${C.zinc950}, 0 ${(12 - pressed * 6) * u}px ${24 * u}px rgba(0,0,0,.5)`,
    transform: `translateY(${pressed * 6 * u}px)`,
  }}>{label}</div>
);

const SPOKEN = 'add rate limiting to the login route, five attempts a minute, then a 429 with a retry-after header';
const RESULT = 'Add rate limiting to the login route: five attempts per minute, then return a 429 with a retry-after header.';

// =================== SCENES ===================

const Intro: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  const s = spring({frame: f, fps, config: {damping: 200, mass: 0.9}});
  const tag = interpolate(f, [24, 46], [0, 1], clamp);
  return (
    <SceneWrap len={len('intro')} rise={0}>
      <Center>
        <div style={{transform: `scale(${0.9 + 0.1 * s})`, filter: `blur(${(1 - s) * 6}px)`}}><Wordmark size={152 * u} /></div>
        <div style={{opacity: tag, marginTop: 46 * u, fontSize: 46 * u, color: C.zinc400, fontFamily: FONT}}>Speak. It types.</div>
      </Center>
    </SceneWrap>
  );
};

const RecordFlow: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  // keycaps
  const pressed = interpolate(f, [16, 22, 28, 34], [0, 1, 1, 0], clamp);
  const keysOp = interpolate(f, [0, 8, 34, 46], [0, 1, 1, 0], clamp);
  // pill (persistent from frame 46 to the end)
  const pillIn = spring({frame: f - 46, fps, config: {damping: 180}});
  const pillOp = interpolate(f, [46, 60], [0, 1], clamp);
  // state machine
  const recording = f < 288;
  const transcribing = f >= 288 && f < 322;
  const state = recording ? 'record' : transcribing ? 'transcribe' : 'copied';
  const sec = Math.max(0, Math.min(7, Math.floor((f - 58) / 30)));
  const time = recording ? `00:0${sec}` : transcribing ? '...' : 'Copied';
  const bv = (ph: number) => 4.5 + 9.5 * (0.5 + 0.5 * Math.sin(f / 3 + ph));
  const bars = (recording ? [bv(0), bv(1.3), bv(2.6)] : [6, 12, 8]) as [number, number, number];
  // captions
  const capOp = interpolate(f, [66, 86, 250, 268], [0, 1, 1, 0], clamp);
  const capChars = Math.floor(interpolate(f, [80, 212], [0, SPOKEN.length], clamp));
  const cardOp = interpolate(f, [324, 348], [0, 1], clamp);
  const cardY = interpolate(f, [324, 348], [26 * u, 0], ease);
  const resChars = Math.floor(interpolate(f, [344, 436], [0, RESULT.length], clamp));
  // eyebrows
  const ebHot = interpolate(f, [0, 8, 38, 50], [0, 1, 1, 0], clamp);
  const ebTalk = interpolate(f, [52, 68, 250, 266], [0, 1, 1, 0], clamp);
  const ebTr = interpolate(f, [272, 286, 316, 328], [0, 1, 1, 0], clamp);
  const ebCop = interpolate(f, [322, 338], [0, 1], clamp);
  return (
    <SceneWrap len={len('record')}>
      <Eyebrow u={u} o={ebHot}>Press your hotkey</Eyebrow>
      <Eyebrow u={u} o={ebTalk}>Just talk</Eyebrow>
      <Eyebrow u={u} o={ebTr}>Transcribing</Eyebrow>
      <Eyebrow u={u} o={ebCop}>Copied to your clipboard</Eyebrow>
      <Center>
        <div style={{position: 'relative', width: '100%', height: 130 * u, display: 'flex', alignItems: 'center', justifyContent: 'center'}}>
          <div style={{position: 'absolute', opacity: keysOp, display: 'flex', gap: 22 * u, alignItems: 'center', transform: `scale(${1 - 0.05 * (1 - keysOp)})`}}>
            <Keycap u={u} label="Alt" pressed={pressed} />
            <div style={{fontSize: 44 * u, color: C.zinc600, fontFamily: FONT}}>+</div>
            <Keycap u={u} label="Q" pressed={pressed} />
          </div>
          <div style={{position: 'absolute', opacity: pillOp, transform: `scale(${0.72 + 0.28 * pillIn})`}}>
            <Pill s={6 * u} state={state} time={time} bars={bars} />
          </div>
        </div>
        <div style={{height: 50 * u}} />
        <div style={{position: 'relative', width: 1000 * u, height: 210 * u}}>
          <div style={{position: 'absolute', top: 0, width: '100%', textAlign: 'center', opacity: capOp, fontFamily: FONT, fontSize: 37 * u, lineHeight: 1.42, color: C.zinc500}}>
            {SPOKEN.slice(0, capChars)}<span style={{opacity: f % 30 < 15 ? 0.55 : 0}}>|</span>
          </div>
          <div style={{position: 'absolute', top: 0, width: '100%', opacity: cardOp, transform: `translateY(${cardY}px)`}}>
            <div style={{background: C.zinc900, border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 22 * u, padding: `${30 * u}px ${36 * u}px`, boxShadow: `0 ${34 * u}px ${78 * u}px rgba(0,0,0,.5)`}}>
              <div style={{display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 18 * u}}>
                <span style={{fontFamily: FONT, fontSize: 21 * u, color: C.zinc500, letterSpacing: '.05em', textTransform: 'uppercase'}}>Transcribed</span>
                <span style={{display: 'flex', alignItems: 'center', gap: 10 * u, fontSize: 22 * u, color: C.green, background: 'rgba(16,185,129,.1)', border: '1px solid rgba(16,185,129,.28)', borderRadius: 999, padding: `${8 * u}px ${18 * u}px`}}>
                  <span style={{width: 11 * u, height: 11 * u, borderRadius: 999, background: C.green}} />Copied
                </span>
              </div>
              <div style={{fontFamily: FONT, fontSize: 37 * u, lineHeight: 1.4, color: C.zinc100}}>{RESULT.slice(0, resChars)}</div>
            </div>
          </div>
        </div>
      </Center>
    </SceneWrap>
  );
};

const Paste: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  const win = spring({frame: f - 4, fps, config: {damping: 200}});
  const pasted = f >= 40;
  const caret = f % 30 < 16;
  const lineH = interpolate(f, [40, 52], [0, 1], clamp);
  return (
    <SceneWrap len={len('paste')}>
      <Eyebrow u={u}>Paste anywhere</Eyebrow>
      <Center>
        <div style={{width: 980 * u, transform: `scale(${0.94 + 0.06 * win})`, background: C.zinc900, border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 18 * u, overflow: 'hidden', boxShadow: `0 ${40 * u}px ${90 * u}px rgba(0,0,0,.55)`}}>
          <div style={{display: 'flex', alignItems: 'center', gap: 10 * u, padding: `${16 * u}px ${22 * u}px`, borderBottom: `${1 * u}px solid ${C.zinc800}`}}>
            <span style={{width: 14 * u, height: 14 * u, borderRadius: 999, background: '#ff5f57'}} />
            <span style={{width: 14 * u, height: 14 * u, borderRadius: 999, background: '#febc2e'}} />
            <span style={{width: 14 * u, height: 14 * u, borderRadius: 999, background: '#28c840'}} />
            <span style={{marginLeft: 14 * u, fontFamily: MONO, fontSize: 20 * u, color: C.zinc500}}>auth.ts</span>
          </div>
          <div style={{padding: `${30 * u}px ${34 * u}px`, fontFamily: MONO, fontSize: 28 * u, lineHeight: 1.6, minHeight: 210 * u}}>
            <div style={{color: C.zinc600}}>{'// paste the request to your coding agent'}</div>
            <div style={{color: C.zinc100, marginTop: 12 * u, opacity: lineH}}>
              {pasted ? RESULT : ''}<span style={{opacity: caret ? 1 : 0, color: C.prompt}}>▋</span>
            </div>
          </div>
        </div>
      </Center>
    </SceneWrap>
  );
};

const Prompting: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const saidIn = interpolate(f, [30, 50], [0, 1], clamp);
  const saidY = interpolate(f, [30, 50], [22 * u, 0], ease);
  const promptIn = interpolate(f, [92, 116], [0, 1], clamp);
  const promptY = interpolate(f, [92, 116], [28 * u, 0], ease);
  const reqs = [
    'Limit to 5 attempts per minute per client.',
    'On exceed, return HTTP 429 with a Retry-After header.',
    'Log the requesting IP on a rejected attempt.',
  ];
  return (
    <SceneWrap len={len('prompt')}>
      <Eyebrow u={u}>Prompting · for coding agents</Eyebrow>
      <Center style={{gap: 30 * u}}>
        <Pill s={4 * u} state="prompt" time="00:07" spark bars={[6, 12, 8]} />
        <div style={{opacity: saidIn, transform: `translateY(${saidY}px)`, width: 940 * u, background: 'rgba(255,255,255,.022)', border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 18 * u, padding: `${22 * u}px ${28 * u}px`}}>
          <div style={{fontFamily: FONT, fontSize: 19 * u, color: C.zinc500, letterSpacing: '.05em', textTransform: 'uppercase', marginBottom: 10 * u}}>You said</div>
          <div style={{fontFamily: FONT, fontSize: 28 * u, lineHeight: 1.4, color: C.zinc400}}>{SPOKEN}</div>
        </div>
        <div style={{opacity: promptIn, transform: `translateY(${promptY}px)`, width: 940 * u, background: C.zinc900, border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 18 * u, padding: `${26 * u}px ${30 * u}px`, boxShadow: `0 ${30 * u}px ${70 * u}px rgba(0,0,0,.45)`}}>
          <span style={{display: 'inline-flex', alignItems: 'center', gap: 10 * u, fontFamily: FONT, fontSize: 20 * u, fontWeight: 600, letterSpacing: '.05em', color: '#c4b5fd', background: 'rgba(139,92,246,.14)', border: '1px solid rgba(139,92,246,.36)', borderRadius: 999, padding: `${7 * u}px ${16 * u}px`, marginBottom: 18 * u}}>
            <Spark size={17 * u} color={C.prompt} />PROMPT
          </span>
          <div style={{fontFamily: FONT, fontSize: 18 * u, color: C.zinc500, letterSpacing: '.04em', textTransform: 'uppercase'}}>Requirements</div>
          {reqs.map((r, i) => {
            const o = interpolate(f, [120 + i * 18, 138 + i * 18], [0, 1], clamp);
            const x = interpolate(f, [120 + i * 18, 138 + i * 18], [-16 * u, 0], ease);
            return (
              <div key={i} style={{opacity: o, transform: `translateX(${x}px)`, display: 'flex', gap: 16 * u, alignItems: 'flex-start', marginTop: 14 * u, fontFamily: FONT, fontSize: 27 * u, color: C.zinc300, lineHeight: 1.3}}>
                <span style={{marginTop: 12 * u, width: 9 * u, height: 9 * u, borderRadius: 999, background: C.prompt, flexShrink: 0}} />{r}
              </div>
            );
          })}
        </div>
      </Center>
    </SceneWrap>
  );
};

const Privacy: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const points = ['100% on your device', 'No cloud, no telemetry', 'Free and open source'];
  return (
    <SceneWrap len={len('privacy')}>
      <Eyebrow u={u}>Private by default</Eyebrow>
      <Center style={{gap: 34 * u}}>
        {points.map((p, i) => {
          const o = interpolate(f, [14 + i * 16, 32 + i * 16], [0, 1], clamp);
          const x = interpolate(f, [14 + i * 16, 32 + i * 16], [-22 * u, 0], ease);
          return (
            <div key={i} style={{opacity: o, transform: `translateX(${x}px)`, display: 'flex', alignItems: 'center', gap: 26 * u, fontFamily: FONT, fontSize: 52 * u, color: C.zinc100}}>
              <span style={{width: 52 * u, height: 52 * u, borderRadius: 999, background: 'rgba(16,185,129,.14)', border: `${2 * u}px solid rgba(16,185,129,.4)`, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0}}>
                <svg width={28 * u} height={28 * u} viewBox="0 0 24 24" fill="none" stroke={C.green} strokeWidth={3.2} strokeLinecap="round" strokeLinejoin="round"><path d="M5 13l4 4L19 7" /></svg>
              </span>{p}
            </div>
          );
        })}
      </Center>
    </SceneWrap>
  );
};

const Outro: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  const s = spring({frame: f, fps, config: {damping: 200}});
  const tag = interpolate(f, [16, 34], [0, 1], clamp);
  const meta = interpolate(f, [28, 46], [0, 1], clamp);
  const url = interpolate(f, [42, 60], [0, 1], clamp);
  return (
    <SceneWrap len={len('outro')} closer rise={0}>
      <Center>
        <div style={{transform: `scale(${0.94 + 0.06 * s})`}}><Wordmark size={150 * u} /></div>
        <div style={{opacity: tag, marginTop: 44 * u, fontSize: 44 * u, color: C.zinc300, fontFamily: FONT}}>Local speech to text</div>
        <div style={{opacity: meta, marginTop: 40 * u, display: 'flex', alignItems: 'center', gap: 22 * u, fontSize: 30 * u, color: C.zinc500, fontFamily: FONT}}>
          <span style={{width: 13 * u, height: 13 * u, borderRadius: 999, background: C.bar}} />Free and open source
          <span style={{width: 6 * u, height: 6 * u, borderRadius: 999, background: C.zinc700}} />Windows &amp; macOS
        </div>
        <div style={{opacity: url, marginTop: 50 * u, fontFamily: MONO, fontSize: 32 * u, color: C.zinc400}}>github.com/v2matosevic/Talkty</div>
      </Center>
    </SceneWrap>
  );
};

// =================== TIMELINE ===================
const Sfx: React.FC<{at: number; file: string; vol?: number}> = ({at, file, vol = 1}) => (
  <Sequence from={at} durationInFrames={90} layout="none"><Audio src={staticFile(`audio/${file}`)} volume={vol} /></Sequence>
);

export const Promo: React.FC = () => {
  return (
    <AbsoluteFill style={{background: C.bg0}}>
      <Background />
      <Sequence from={SC.intro[0]} durationInFrames={len('intro') + OVERLAP}><Intro /></Sequence>
      <Sequence from={SC.record[0]} durationInFrames={len('record') + OVERLAP}><RecordFlow /></Sequence>
      <Sequence from={SC.paste[0]} durationInFrames={len('paste') + OVERLAP}><Paste /></Sequence>
      <Sequence from={SC.prompt[0]} durationInFrames={len('prompt') + OVERLAP}><Prompting /></Sequence>
      <Sequence from={SC.privacy[0]} durationInFrames={len('privacy') + OVERLAP}><Privacy /></Sequence>
      <Sequence from={SC.outro[0]} durationInFrames={len('outro')}><Outro /></Sequence>

      {/* Background track (fades baked into the MP3). */}
      <Audio src={staticFile('audio/music.mp3')} volume={0.45} />
      {/* RecordFlow starts at 90: keypress ~ +20, pill pop ~ +52, copied ~ +322 */}
      <Sfx at={112} file="click.wav" vol={0.8} />
      <Sfx at={142} file="pop.wav" vol={0.9} />
      <Sfx at={412} file="ding.wav" vol={0.85} />
      <Sfx at={SC.paste[0]} file="whoosh.wav" vol={0.45} />
      <Sfx at={SC.paste[0] + 36} file="click.wav" vol={0.8} />
      <Sfx at={SC.prompt[0]} file="whoosh.wav" vol={0.45} />
      <Sfx at={SC.prompt[0] + 18} file="shimmer.wav" vol={0.7} />
      <Sfx at={SC.privacy[0]} file="whoosh.wav" vol={0.4} />
      <Sfx at={SC.outro[0]} file="riser.wav" vol={0.7} />
    </AbsoluteFill>
  );
};
