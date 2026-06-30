import React from 'react';
import {C, FONT} from './theme';

// The recording pill, rebuilt 1:1 from OverlayWindow.xaml. `s` is the scale (like --s).
export const Pill: React.FC<{
  s: number;
  state?: 'record' | 'transcribe' | 'copied' | 'prompt';
  time?: string;
  spark?: boolean;
  bars?: [number, number, number];
}> = ({s, state = 'record', time = '00:00', spark = false, bars = [6, 12, 8]}) => {
  const barColor = state === 'transcribe' ? C.transcribe : state === 'copied' ? C.greenBar : C.bar;
  const timeColor = state === 'copied' ? C.greenBar : C.zinc200;
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center', gap: 8 * s, background: C.pillBg,
      border: `${Math.max(1, 1 * s)}px solid rgba(255,255,255,0.125)`, borderRadius: 18 * s,
      padding: `${8 * s}px ${14 * s}px`, boxShadow: `0 ${10 * s}px ${34 * s}px rgba(0,0,0,0.55)`,
    }}>
      <div style={{display: 'flex', alignItems: 'center', gap: 2 * s, height: 16 * s}}>
        {bars.map((h, i) => (
          <div key={i} style={{width: 3 * s, height: h * s, borderRadius: 1.5 * s, background: barColor}} />
        ))}
      </div>
      <div style={{
        display: 'flex', alignItems: 'center', height: 16 * s,
        color: timeColor, fontSize: 13.5 * s, fontWeight: 600, fontFamily: FONT,
        fontVariantNumeric: 'tabular-nums', fontFeatureSettings: '"tnum" 1',
        letterSpacing: state === 'copied' ? '0' : '.02em', lineHeight: 1,
        paddingBottom: 1 * s,
      }}>{time}</div>
      {spark && (
        <svg width={15 * s} height={15 * s} viewBox="0 0 18 16" style={{marginLeft: 2 * s}}>
          <path d="M9,0.5 L10.5,6.6 L16.5,8 L10.5,9.4 L9,15.5 L7.5,9.4 L1.5,8 L7.5,6.6 Z"
            fill={state === 'prompt' ? C.prompt : C.zinc400} />
        </svg>
      )}
    </div>
  );
};

// Wave-bar mark + "Talkty". `size` is the wordmark font size in px.
export const Wordmark: React.FC<{size: number; color?: string}> = ({size, color = C.zinc50}) => (
  <div style={{display: 'flex', alignItems: 'center', gap: 0.2 * size}}>
    <div style={{display: 'flex', alignItems: 'center', gap: 0.069 * size, height: 0.65 * size}}>
      {[0.3375, 0.65, 0.45].map((h, i) => (
        <div key={i} style={{width: 0.137 * size, height: h * size, borderRadius: 0.0685 * size, background: C.bar}} />
      ))}
    </div>
    <div style={{fontFamily: FONT, fontWeight: 650, fontSize: size, letterSpacing: '-0.025em', color, lineHeight: 1}}>Talkty</div>
  </div>
);

export const Spark: React.FC<{size: number; color: string}> = ({size, color}) => (
  <svg width={size} height={size} viewBox="0 0 18 16">
    <path d="M9,0.5 L10.5,6.6 L16.5,8 L10.5,9.4 L9,15.5 L7.5,9.4 L1.5,8 L7.5,6.6 Z" fill={color} />
  </svg>
);
