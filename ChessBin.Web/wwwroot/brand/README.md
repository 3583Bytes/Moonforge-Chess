# ChessBin brand assets

Every file here is derived from the **256×256 transparent PNG entry inside `ChessBinIcon.ico`**
(the original icon carries nine entries; that one is the only full-colour, full-size, transparent
source). The standalone `ChessBinIcon.png` is *not* the source — it is 123×123 and matted onto
solid white.

Generated, all from that one source, cropped to the artwork's tight bounding box (246×200):

| File | Size | Notes |
| --- | --- | --- |
| `chessbin-mark-72.png` / `-144.png` | 72×59, 144×117 | Transparent. Masthead mark; the light plate behind it comes from CSS (`.brand-mark`), not the asset. Served via `srcset` `w`-descriptors at a 36px slot. |
| `apple-touch-icon.png` | 180×180 | Light plate baked in, 10% padding. iOS masks the corners itself. |
| `icon-192.png`, `icon-512.png` | 192², 512² | Same treatment; referenced from `site.webmanifest`. |
| `og-image.png` | 1200×630 | Mark on a rounded plate over the site's dark background. |
| `../favicon.ico` | 16, 32, 48 | PNG-payload ICO, rounded light plate baked in. |

## Why the plate

The knight in the mark is solid `#000000`, so on the site's near-black background it disappears.
Everything that renders against dark — masthead, favicon, app icons, og:image — puts the artwork
on a warm near-white plate (`--plate: #f7f3ea`) with a `--gold-line` hairline.

## Palette

Sampled from the artwork itself and wired into `css/app.css`:

- gold `#f5b807`, bright `#f9d46a` — the brand, and anything the player acts on
- cyan `#04acd9`, deep `#07527a` — the engine's own voice: thinking dots, evaluation bar,
  principal variation, the Moonforge avatar, the WASM loading ring

Regenerating any of these means re-deriving from `ChessBinIcon.ico`; do not upscale the 123px PNG.
