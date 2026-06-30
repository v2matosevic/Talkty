import React from 'react';
import {
  AbsoluteFill, Sequence, Audio, staticFile, useCurrentFrame, useVideoConfig,
  interpolate, spring, Easing,
} from 'remotion';
import {C, FONT, MONO} from './theme';
import {Pill, Wordmark, Spark} from './components';

// ---- scene timing (frames @30fps) ----
const SC = {
  intro: [0, 90], hotkey: [90, 210], speak: [210, 360], transcribe: [360, 540],
  paste: [540, 660], prompt: [660, 900], privacy: [900, 1020], outro: [1020, 1170],
} as const;
const len = (k: keyof typeof SC) => SC[k][1] - SC[k][0];

const ease = {easing: Easing.out(Easing.cubic), extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;
const clamp = {extrapolateLeft: 'clamp', extrapolateRight: 'clamp'} as const;

const useU = () => {
  const {width, height} = useVideoConfig();
  return Math.min(width, height) / 1080;
};
// Scenes overlap (see OVERLAP), so most fade IN only and stay full into the next
// scene, which fades in on top of them = a cross-dissolve, no dip to black.
const OVERLAP = 18;
const useFadeIn = (inF = 14) => {
  const f = useCurrentFrame();
  return interpolate(f, [0, inF], [0, 1], clamp);
};
const useFadeInOut = (dur: number, inF = 16, outF = 18) => {
  const f = useCurrentFrame();
  return interpolate(f, [0, inF, dur - outF, dur], [0, 1, 1, 0], clamp);
};

const Center: React.FC<{children: React.ReactNode; style?: React.CSSProperties}> = ({children, style}) => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center', flexDirection: 'column', ...style}}>
    {children}
  </AbsoluteFill>
);

const Background: React.FC = () => (
  <AbsoluteFill>
    <AbsoluteFill style={{background: 'radial-gradient(130% 90% at 50% -12%, #1b1b21 0%, #0d0d10 50%, #08080a 100%)'}} />
    <AbsoluteFill style={{
      backgroundImage: 'radial-gradient(rgba(255,255,255,0.05) 1px, transparent 1.4px)',
      backgroundSize: '44px 44px', opacity: 0.28,
      WebkitMaskImage: 'radial-gradient(72% 62% at 50% 46%, #000, transparent 80%)',
    }} />
  </AbsoluteFill>
);

const Eyebrow: React.FC<{u: number; children: React.ReactNode; o?: number}> = ({u, children, o = 1}) => (
  <div style={{
    position: 'absolute', top: '14%', width: '100%', textAlign: 'center', opacity: o,
    fontFamily: FONT, fontSize: 30 * u, color: C.zinc500, letterSpacing: '.06em', textTransform: 'uppercase',
  }}>{children}</div>
);

// =================== SCENES ===================

const Intro: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  const s = spring({frame: f, fps, config: {damping: 200, mass: 0.8}});
  const op = useFadeIn(16);
  const tag = interpolate(f, [22, 42], [0, 1], clamp);
  return (
    <Center style={{opacity: op}}>
      <div style={{transform: `scale(${0.92 + 0.08 * s})`}}><Wordmark size={150 * u} /></div>
      <div style={{opacity: tag, marginTop: 46 * u, fontSize: 46 * u, color: C.zinc400, fontFamily: FONT}}>
        Speak. It types.
      </div>
    </Center>
  );
};

const Keycap: React.FC<{u: number; label: string; pressed: number}> = ({u, label, pressed}) => (
  <div style={{
    fontFamily: FONT, fontSize: 40 * u, fontWeight: 600, color: C.zinc100,
    background: `linear-gradient(180deg, ${C.zinc800}, ${C.zinc900})`,
    border: `${2 * u}px solid ${C.zinc700}`, borderRadius: 16 * u,
    padding: `${22 * u}px ${30 * u}px`, minWidth: 90 * u, textAlign: 'center',
    boxShadow: `0 ${8 * u - pressed * 6 * u}px 0 ${C.zinc950}, 0 ${12 * u}px ${24 * u}px rgba(0,0,0,.5)`,
    transform: `translateY(${pressed * 6 * u}px)`,
  }}>{label}</div>
);

const Hotkey: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  const op = useFadeIn(14);
  const pressed = interpolate(f, [18, 24, 30, 36], [0, 1, 1, 0], clamp);
  const keysOut = interpolate(f, [34, 48], [1, 0], clamp);
  const pillIn = spring({frame: f - 48, fps, config: {damping: 180}});
  const pillOp = interpolate(f, [48, 62], [0, 1], clamp);
  return (
    <AbsoluteFill style={{opacity: op}}>
      <Eyebrow u={u}>Press your hotkey</Eyebrow>
      <Center>
        <div style={{position: 'absolute', display: 'flex', gap: 22 * u, alignItems: 'center', opacity: keysOut, transform: `scale(${1 - 0.06 * (1 - keysOut)})`}}>
          <Keycap u={u} label="Alt" pressed={pressed} />
          <div style={{fontSize: 44 * u, color: C.zinc600, fontFamily: FONT}}>+</div>
          <Keycap u={u} label="Q" pressed={pressed} />
        </div>
        <div style={{position: 'absolute', opacity: pillOp, transform: `scale(${0.7 + 0.3 * pillIn})`}}>
          <Pill s={6 * u} state="record" time="0:00" bars={[6, 12, 8]} />
        </div>
      </Center>
    </AbsoluteFill>
  );
};

const SPOKEN = 'add rate limiting to the login route, five attempts a minute, then a 429 with a retry-after header';

const Speak: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const op = useFadeIn(14);
  const b = (ph: number) => 5 + 9 * (0.5 + 0.5 * Math.sin(f / 3 + ph));
  const sec = Math.min(5, 1 + Math.floor(f / 28));
  const chars = Math.floor(interpolate(f, [20, 140], [0, SPOKEN.length], clamp));
  return (
    <AbsoluteFill style={{opacity: op}}>
      <Eyebrow u={u}>Just talk</Eyebrow>
      <Center>
        <Pill s={6 * u} state="record" time={`0:0${sec}`} bars={[b(0), b(1.3), b(2.6)] as [number, number, number]} />
        <div style={{
          marginTop: 70 * u, width: 0.62 * Math.min(1920, 1080) * (u > 0 ? 1 : 1), maxWidth: 980 * u,
          textAlign: 'center', fontFamily: FONT, fontSize: 36 * u, lineHeight: 1.4, color: C.zinc500,
        }}>
          {SPOKEN.slice(0, chars)}<span style={{opacity: 0.5}}>{f % 30 < 15 ? '|' : ''}</span>
        </div>
      </Center>
    </AbsoluteFill>
  );
};

const RESULT = 'Add rate limiting to the login route: five attempts per minute, then return a 429 with a retry-after header.';

const Transcribe: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const op = useFadeIn(14);
  const copied = f >= 42;
  const cardIn = interpolate(f, [40, 60], [0, 1], clamp);
  const cardY = interpolate(f, [40, 60], [30 * u, 0], {...ease});
  const chars = Math.floor(interpolate(f, [56, 150], [0, RESULT.length], clamp));
  return (
    <AbsoluteFill style={{opacity: op}}>
      <Eyebrow u={u}>{copied ? 'Copied to your clipboard' : 'Transcribing'}</Eyebrow>
      <Center>
        <Pill s={5 * u} state={copied ? 'copied' : 'transcribe'} time={copied ? 'Copied' : '...'} bars={[6, 12, 8]} />
        <div style={{
          marginTop: 56 * u, opacity: cardIn, transform: `translateY(${cardY}px)`,
          width: 900 * u, background: C.zinc900, border: `${1 * u}px solid ${C.zinc800}`,
          borderRadius: 22 * u, padding: `${32 * u}px ${36 * u}px`, boxShadow: `0 ${36 * u}px ${80 * u}px rgba(0,0,0,.5)`,
        }}>
          <div style={{display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 20 * u}}>
            <span style={{fontFamily: FONT, fontSize: 21 * u, color: C.zinc500, letterSpacing: '.05em', textTransform: 'uppercase'}}>Transcribed</span>
            <span style={{display: 'flex', alignItems: 'center', gap: 10 * u, fontSize: 22 * u, color: C.green,
              background: 'rgba(16,185,129,.1)', border: '1px solid rgba(16,185,129,.28)', borderRadius: 999, padding: `${8 * u}px ${18 * u}px`}}>
              <span style={{width: 11 * u, height: 11 * u, borderRadius: 999, background: C.green}} />Copied
            </span>
          </div>
          <div style={{fontFamily: FONT, fontSize: 37 * u, lineHeight: 1.4, color: C.zinc100}}>
            {RESULT.slice(0, chars)}
          </div>
        </div>
      </Center>
    </AbsoluteFill>
  );
};

const Paste: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const op = useFadeIn(14);
  const winIn = spring({frame: f - 6, fps: 30, config: {damping: 200}});
  const pasted = f >= 38;
  const caret = f % 30 < 15;
  return (
    <AbsoluteFill style={{opacity: op}}>
      <Eyebrow u={u}>Paste anywhere</Eyebrow>
      <Center>
        <div style={{
          width: 940 * u, transform: `scale(${0.92 + 0.08 * winIn})`,
          background: C.zinc900, border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 18 * u, overflow: 'hidden',
          boxShadow: `0 ${40 * u}px ${90 * u}px rgba(0,0,0,.55)`,
        }}>
          <div style={{display: 'flex', alignItems: 'center', gap: 10 * u, padding: `${16 * u}px ${22 * u}px`, borderBottom: `${1 * u}px solid ${C.zinc800}`}}>
            <span style={{width: 14 * u, height: 14 * u, borderRadius: 999, background: '#ff5f57'}} />
            <span style={{width: 14 * u, height: 14 * u, borderRadius: 999, background: '#febc2e'}} />
            <span style={{width: 14 * u, height: 14 * u, borderRadius: 999, background: '#28c840'}} />
            <span style={{marginLeft: 14 * u, fontFamily: MONO, fontSize: 20 * u, color: C.zinc500}}>auth.ts</span>
          </div>
          <div style={{padding: `${30 * u}px ${34 * u}px`, fontFamily: MONO, fontSize: 28 * u, lineHeight: 1.6, minHeight: 200 * u}}>
            <div style={{color: C.zinc600}}>{'// paste the request to your coding agent'}</div>
            <div style={{color: C.zinc200, marginTop: 10 * u}}>
              {pasted ? RESULT : ''}<span style={{opacity: caret ? 1 : 0, color: C.prompt}}>▋</span>
            </div>
          </div>
        </div>
      </Center>
    </AbsoluteFill>
  );
};

const Prompting: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const op = useFadeIn(14);
  const saidIn = interpolate(f, [34, 54], [0, 1], clamp);
  const promptIn = interpolate(f, [96, 120], [0, 1], clamp);
  const promptY = interpolate(f, [96, 120], [30 * u, 0], {...ease});
  const reqs = [
    'Limit to 5 attempts per minute per client.',
    'On exceed, return HTTP 429 with a Retry-After header.',
    'Log the requesting IP on a rejected attempt.',
  ];
  return (
    <AbsoluteFill style={{opacity: op}}>
      <Eyebrow u={u}>Prompting · for coding agents</Eyebrow>
      <Center style={{gap: 30 * u}}>
        <Pill s={4 * u} state="prompt" time="0:07" spark bars={[6, 12, 8]} />
        <div style={{opacity: saidIn, width: 940 * u, background: 'rgba(255,255,255,.022)', border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 18 * u, padding: `${22 * u}px ${28 * u}px`}}>
          <div style={{fontFamily: FONT, fontSize: 19 * u, color: C.zinc500, letterSpacing: '.05em', textTransform: 'uppercase', marginBottom: 10 * u}}>You said</div>
          <div style={{fontFamily: FONT, fontSize: 28 * u, lineHeight: 1.4, color: C.zinc400}}>{SPOKEN}</div>
        </div>
        <div style={{opacity: promptIn, transform: `translateY(${promptY}px)`, width: 940 * u, background: C.zinc900, border: `${1 * u}px solid ${C.zinc800}`, borderRadius: 18 * u, padding: `${26 * u}px ${30 * u}px`, boxShadow: `0 ${30 * u}px ${70 * u}px rgba(0,0,0,.45)`}}>
          <span style={{display: 'inline-flex', alignItems: 'center', gap: 10 * u, fontFamily: FONT, fontSize: 20 * u, fontWeight: 600, letterSpacing: '.05em', color: '#c4b5fd', background: 'rgba(139,92,246,.14)', border: '1px solid rgba(139,92,246,.36)', borderRadius: 999, padding: `${7 * u}px ${16 * u}px`, marginBottom: 18 * u}}>
            <Spark size={17 * u} color={C.prompt} />PROMPT
          </span>
          <div style={{fontFamily: FONT, fontSize: 18 * u, color: C.zinc500, letterSpacing: '.04em', textTransform: 'uppercase'}}>Requirements</div>
          {reqs.map((r, i) => {
            const o = interpolate(f, [126 + i * 16, 142 + i * 16], [0, 1], clamp);
            return (
              <div key={i} style={{opacity: o, display: 'flex', gap: 16 * u, alignItems: 'flex-start', marginTop: 14 * u, fontFamily: FONT, fontSize: 27 * u, color: C.zinc300, lineHeight: 1.3}}>
                <span style={{marginTop: 12 * u, width: 9 * u, height: 9 * u, borderRadius: 999, background: C.prompt, flexShrink: 0}} />{r}
              </div>
            );
          })}
        </div>
      </Center>
    </AbsoluteFill>
  );
};

const Privacy: React.FC = () => {
  const f = useCurrentFrame(); const u = useU();
  const op = useFadeIn(14);
  const points = ['100% on your device', 'No cloud, no telemetry', 'Free and open source'];
  return (
    <AbsoluteFill style={{opacity: op}}>
      <Eyebrow u={u}>Private by default</Eyebrow>
      <Center style={{gap: 34 * u}}>
        {points.map((p, i) => {
          const o = interpolate(f, [12 + i * 18, 30 + i * 18], [0, 1], clamp);
          const x = interpolate(f, [12 + i * 18, 30 + i * 18], [-24 * u, 0], {...ease});
          return (
            <div key={i} style={{opacity: o, transform: `translateX(${x}px)`, display: 'flex', alignItems: 'center', gap: 26 * u, fontFamily: FONT, fontSize: 52 * u, color: C.zinc100}}>
              <span style={{width: 52 * u, height: 52 * u, borderRadius: 999, background: 'rgba(16,185,129,.14)', border: `${2 * u}px solid rgba(16,185,129,.4)`, display: 'flex', alignItems: 'center', justifyContent: 'center'}}>
                <svg width={28 * u} height={28 * u} viewBox="0 0 24 24" fill="none" stroke={C.green} strokeWidth={3.2} strokeLinecap="round" strokeLinejoin="round"><path d="M5 13l4 4L19 7" /></svg>
              </span>
              {p}
            </div>
          );
        })}
      </Center>
    </AbsoluteFill>
  );
};

const Outro: React.FC = () => {
  const f = useCurrentFrame(); const {fps} = useVideoConfig(); const u = useU();
  const op = useFadeInOut(len('outro'), 16, 20);
  const s = spring({frame: f, fps, config: {damping: 200}});
  const tag = interpolate(f, [18, 36], [0, 1], clamp);
  const meta = interpolate(f, [30, 48], [0, 1], clamp);
  const url = interpolate(f, [44, 62], [0, 1], clamp);
  return (
    <Center style={{opacity: op}}>
      <div style={{transform: `scale(${0.94 + 0.06 * s})`}}><Wordmark size={150 * u} /></div>
      <div style={{opacity: tag, marginTop: 44 * u, fontSize: 44 * u, color: C.zinc300, fontFamily: FONT}}>Local speech to text</div>
      <div style={{opacity: meta, marginTop: 40 * u, display: 'flex', alignItems: 'center', gap: 22 * u, fontSize: 30 * u, color: C.zinc500, fontFamily: FONT}}>
        <span style={{width: 13 * u, height: 13 * u, borderRadius: 999, background: C.bar}} />Free and open source
        <span style={{width: 6 * u, height: 6 * u, borderRadius: 999, background: C.zinc700}} />Windows &amp; macOS
      </div>
      <div style={{opacity: url, marginTop: 50 * u, fontFamily: MONO, fontSize: 32 * u, color: C.zinc400}}>github.com/v2matosevic/Talkty</div>
    </Center>
  );
};

// =================== TIMELINE ===================
const Sfx: React.FC<{at: number; file: string; vol?: number}> = ({at, file, vol = 1}) => (
  <Sequence from={at} durationInFrames={90} layout="none">
    <Audio src={staticFile(`audio/${file}`)} volume={vol} />
  </Sequence>
);

export const Promo: React.FC = () => {
  const {durationInFrames} = useVideoConfig();
  return (
    <AbsoluteFill style={{background: C.bg0}}>
      <Background />
      <Sequence from={SC.intro[0]} durationInFrames={len('intro') + OVERLAP}><Intro /></Sequence>
      <Sequence from={SC.hotkey[0]} durationInFrames={len('hotkey') + OVERLAP}><Hotkey /></Sequence>
      <Sequence from={SC.speak[0]} durationInFrames={len('speak') + OVERLAP}><Speak /></Sequence>
      <Sequence from={SC.transcribe[0]} durationInFrames={len('transcribe') + OVERLAP}><Transcribe /></Sequence>
      <Sequence from={SC.paste[0]} durationInFrames={len('paste') + OVERLAP}><Paste /></Sequence>
      <Sequence from={SC.prompt[0]} durationInFrames={len('prompt') + OVERLAP}><Prompting /></Sequence>
      <Sequence from={SC.privacy[0]} durationInFrames={len('privacy') + OVERLAP}><Privacy /></Sequence>
      <Sequence from={SC.outro[0]} durationInFrames={len('outro')}><Outro /></Sequence>

      {/* music bed */}
      <Audio src={staticFile('audio/music.wav')}
        volume={(f) => interpolate(f, [0, 30, durationInFrames - 55, durationInFrames], [0, 0.4, 0.4, 0], clamp)} />
      {/* sound effects */}
      <Sfx at={112} file="click.wav" vol={0.8} />
      <Sfx at={138} file="pop.wav" vol={0.9} />
      <Sfx at={358} file="whoosh.wav" vol={0.5} />
      <Sfx at={402} file="ding.wav" vol={0.85} />
      <Sfx at={575} file="click.wav" vol={0.8} />
      <Sfx at={658} file="whoosh.wav" vol={0.5} />
      <Sfx at={678} file="shimmer.wav" vol={0.7} />
      <Sfx at={1020} file="riser.wav" vol={0.7} />
    </AbsoluteFill>
  );
};
