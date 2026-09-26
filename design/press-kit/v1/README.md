# GodotUO — emerald and gold press kit

Approved direction: concept 03, with the capsule nose changed to gold to match the eye rims and mouth. Two marks are included: the full UO emblem and a standalone custom Godot face, each with engraved silver, gold trim, and emerald eyes.

## Quick selection

| Use | File |
|---|---|
| Full emblem master, transparent | `masters/guo-emblem-transparent-native.png` |
| Standalone face master, transparent | `masters/guo-godot-mark-transparent-native.png` |
| Either master on black | `masters/*-native.png` |
| Website | `web/*-512.webp` or `web/*-1024.webp` |
| App / desktop icon | `icons/guo-godot-mark.ico` or `.icns` |
| Full-emblem app icon | `icons/guo-emblem.ico` or `.icns` |
| Browser favicon | `web/favicon.ico` |
| Apple touch icon | `web/apple-touch-icon.png` |
| Social / Open Graph | `social/*-open-graph-1200x630.png` |
| Full HD / 4K presentation | `hd/` |

PNG sizes: 16, 24, 32, 48, 64, 96, 128, 180, 192, 256, 512, and 1024 px. ICO contains 16–256 px entries; ICNS contains 16–1024 px entries. Use the standalone face at small sizes; the full emblem loses detail in favicons.

## Artwork specifications

- Native masters: 1254 × 1254 px, in two forms: `*-transparent-native.png` (RGBA) and `*-native.png` (RGB on opaque black).
- **Every logo export is transparent**: `png/`, `web/` (webp, favicons, Android icons), `icons/` (ICO, ICNS) and the square and 16:9 files in `hd/`. Place them on any background.
- Kept on black on purpose: `social/` Open Graph cards and `preview.png` (platforms show them as finished rectangles), and `web/apple-touch-icon.png` (iOS fills transparency with black anyway).
- The generator could not return real alpha, so the transparency is keyed from the black masters. The emblem's cutout was made separately and is kept as delivered; the face is keyed by `tools/cutout.py` (`python tools/cutout.py`, needs Pillow, NumPy and SciPy). Edge pixels are un-blended from black, so the edges carry no dark fringe beyond the artwork's own bevel shading.
- 2048 / 4096 square and 3840 × 2160 exports are **upscaled**, not native high-resolution or vector artwork. The 4096 and 3840 × 2160 files are not kept in the repository; `node tools/build.cjs` regenerates them, and the release zip includes them.
- The standalone face is a separately generated matching rendition, not a pixel-identical crop of the full emblem.
- Preserve proportions and the gold nose, gold eye rims, emerald eyes, and engraved silver. Keep clear space around the mark. Do not stretch.
- Suggested supporting colors: black `#000000`, gold `#DDAA28`, silver `#C7C9C8`, emerald `#008D36`. These are design tokens, not color-managed print specifications.

## Project copy

**Name:** GodotUO. **Short name:** GUO.

**Short description:** GodotUO ports the Classic Ultima Online client experience to Godot .NET.

**Longer description:** GodotUO carries ClassicUO's C# networking, file readers, and game logic onto Godot, replacing the parts tied to FNA. The project is in pre-release and requires a user-supplied Ultima Online installation; game data is not distributed with the project.

Project copy is based on the repository README at preparation time. Consult the main README for current status, supported versions, and requirements. No release date, contact address, or endorsement is asserted in this kit.

## Credits and source material

Custom artwork generated with OpenAI's built-in image generation tool, using the user-supplied UO emblem and the Godot icon as references. Export resizing and packaging use sharp. See `PROMPTS.md` for production prompts.

Godot logo: the Godot Engine contributors, [official press kit](https://godotengine.org/press/), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Changes include gold and silver materials, engraving, gemstone eyes, and integration with the supplied emblem. This is custom derivative artwork, not an official Godot logo. The press kit's usage guidelines remain relevant to public use; this asset package does not grant endorsement or third-party trademark rights.

The UO emblem was supplied by the project owner. This package does not assign a new license to that source artwork or apply the repository's code license to third-party marks.

## Integration

Copy the `web` folder's favicon, touch icon, Android icons, and manifest into your public asset directory; adjust the URLs in `web/integration.html` to your deployment. No website was published and no existing Godot project configuration was changed.

To adopt the standalone icon in Godot, copy `png/guo-godot-mark-256.png` into the project and select it under Project Settings → Application → Config → Icon. The ICO and ICNS files are provided for platform export configuration.

## Rebuilding and validation

Run `python tools/cutout.py` if the face master changed, then `node tools/build.cjs` with sharp installed, or set `SHARP_MODULE` to the installed sharp module path. This rebuilds export sizes from the masters; it does not regenerate the artwork. `manifest.json` records file sizes and SHA-256 hashes. `node tools/verify.cjs` checks them, and fails if a logo export has lost its transparency.
