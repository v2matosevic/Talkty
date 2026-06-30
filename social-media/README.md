# Talkty launch kit

Every image here is rendered from the real app: the same colors, fonts, and
recording pill as Talkty itself (see `sources/`). Nothing is AI generated. To
re-render after a tweak, run `pwsh sources/render.ps1` (needs Chrome).

## Images

| File | Size | Use |
|------|------|-----|
| `images/hero.png` | 2400 x 1200 | Main hero. Shows the pill turning speech into clipboard text. README, blog, slides. |
| `images/hero-minimal.png` | 2400 x 1200 | Clean brand hero (pill + wordmark). Good when you want less copy. |
| `images/github-social.png` | 1280 x 640 | GitHub social preview (Settings > Social preview). Also fine as a generic card. |
| `images/x-card.png` | 1600 x 900 | X / Twitter post image (16:9). |
| `images/linkedin-card.png` | 1200 x 627 | LinkedIn link card. |
| `images/pill-states.png` | 2000 x 620 | The recording pill in all four states: recording, transcribing, copied, prompting. |
| `images/feature-prompting.png` | 1600 x 1000 | The Prompting feature: a rambling dictation rewritten into a structured prompt. |
| `images/wordmark-light.png` | transparent | Wordmark for dark backgrounds. |
| `images/wordmark-dark.png` | transparent | Wordmark for light backgrounds. |
| `images/icon.png` | 1024 x 1024 | App mark (the wave bars on a dark tile). Avatars, favicons, store listings. |
| `images/youtube-thumbnail.png` | 1280 x 720 | YouTube thumbnail (under 2 MB). `@2x.jpg` is a higher-res copy. |

## Posts

- `posts/x.md` — launch thread and a single-post version.
- `posts/linkedin.md` — LinkedIn announcement.
- `posts/reddit.md` — tailored posts for r/LocalLLaMA, r/SideProject, r/opensource, r/macapps, r/windowsapps.
- `posts/hackernews.md` — Show HN title and first comment.
- `posts/producthunt.md` — tagline, description, maker comment, topics.
- `posts/youtube.md` — title, description, chapters, pinned comment.
- `posts/shorts-reels-tiktok.md` — captions and hashtags for the vertical video.
- `posts/github-release.md` — release note draft.

All copy is em-dash free and written to be posted as the maker, not as an ad.

## Video

The promotional explainer, rendered in both orientations from one source. The UI
in the video is the real app, animated. Music and sound effects are synthesized
locally, so there is nothing to license.

| File | Size | Use |
|------|------|-----|
| `video/talkty-promo-horizontal.mp4` | 1920 x 1080 | YouTube, X, LinkedIn, the website. |
| `video/talkty-promo-vertical.mp4` | 1080 x 1920 | Reels, Shorts, TikTok, Stories. |

Source and build instructions live in [`../promo`](../promo). Re-render with
`npm run render:h` and `npm run render:v`.

## Brand quick reference

- Background: zinc 950 `#09090B` to `#121214`
- Text: zinc 50 `#FAFAFA` (primary), zinc 400 `#A1A1AA` (secondary)
- Recording bars: coral `#FF6B6B`
- Prompting accent: violet `#8B5CF6`
- Success / copied: green `#10B981`
- Fonts: Segoe UI Variable (UI), Cascadia Code (mono)
