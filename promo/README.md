# Talkty promo video

A [Remotion](https://remotion.dev) project that renders the Talkty explainer in
both orientations from one composition. The UI in the video (the recording pill,
the wordmark, the cards) is the real app, rebuilt in React from the same colors,
fonts, and shapes as Talkty itself. The music and sound effects are synthesized
locally (no licensing), see `scripts/generate-audio.mjs`.

## What it shows

Press a hotkey, speak, the words land on the clipboard as clean text, paste them
anywhere, optionally let Prompting turn a ramble into a structured prompt for a
coding agent, and all of it runs on your device.

## Setup

```bash
npm install
npm run audio        # regenerate the music + SFX into public/audio (optional, already committed)
```

## Preview and render

```bash
npm run studio       # interactive preview in the browser

npm run render:h     # horizontal 1920x1080  -> out/talkty-promo-horizontal.mp4
npm run render:v     # vertical   1080x1920  -> out/talkty-promo-vertical.mp4
```

Both compositions (`PromoH`, `PromoV`) share the same scenes in `src/Promo.tsx`
and adapt by centering on the shorter dimension, so one edit updates both.

## Layout

```
src/
  Root.tsx        the two compositions (H and V)
  Promo.tsx       the timeline: scenes, transitions, and audio
  components.tsx  the Pill and Wordmark, rebuilt from the app
  theme.ts        the brand palette and fonts
scripts/
  generate-audio.mjs   synthesizes music.wav and the UI sound effects
public/audio/     the rendered audio
```
