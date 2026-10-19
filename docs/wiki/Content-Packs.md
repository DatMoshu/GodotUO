# Content Packs

A content pack is a ZIP of plain files that the [Asset Store](Asset-Store.md)
installs. Packs are layer L2 (see [Feature Layers](Feature-Layers.md)): they
carry content, never code, and nothing in a pack runs when you install it.
Every file is hash-checked, and UO's own data files are refused.

GUO never ships UO art. A pack carries art its author made and holds the
rights to, with its licence in the manifest.

## What packs can carry today

| Pack content | What it does in the game | Switched on by |
|---|---|---|
| Backgrounds, themes, sounds, profile presets | The canvas behind the game, the screen saver, login and UI looks | Choosing them in Options |
| **Screen effects** | Extra looks for the screen effect menu | Choosing a look; installing selects nothing, and Classic switches effects off. See [Screen Effects](Screen-Effects.md) |
| **HD art** (`hd-art`) | Larger, more detailed drawings of world tiles | Options > **Enhanced HD (installed packs)**, off by default |
| **Regional music and sounds** | Music and ambient sound by area of the map | Options > Sound > **Regional audio and recorded NPC voices**, off by default |
| **Recorded voices** (`voice`) | Recorded speech for an NPC, matched by name, then by body | The same Sound option |
| **3D splats** (`splat`) | A 3D stand-in for one static | Installs, but the game does not show them yet |
| Art, gumps, hues, fonts, map blocks, tile data, animations, translations | Content added on top of your install, bound to explicit IDs | Selecting a deployment in the Store's **Configure deployment** window; restart to apply |

### Enhanced HD

With the option on, world sprites use the PNG from an installed HD art pack.
Anything the pack does not cover, and any drawing whose size does not match
your own install, stays native art. The mouse cursor, the backpack, clicking
and shadows always use native art. The filter is a profile choice: **Auto**
(sharp when the HD scale equals your zoom), **Point**, **Point with
mipmaps** or **Smooth**. With the option off, the game draws exactly as
before; the proof compares every pixel. ADR-0027.

Measured so far on a synthetic pack at the Britain bank: fewer texture
switches per frame, but no CPU gain, so the option stays off by default
until a dense town is measured.

### Regional audio and recorded voices

With the option on, an installed region can play its own music and sounds,
and an NPC whose name (or body) a voice pack covers speaks its recorded
lines. A line with no recording stays silent. **NPC speech no longer uses
text-to-speech**: a voice you set up before October 2026 is silent until a
recorded voice pack covers that NPC. Recordings are WAV, PCM 22050 Hz mono
16-bit. ADR-0036.

The scripted checks prove the packs are read and matched; hearing a live
NPC speak through one is **written, not verified**.

## Making a pack

- The pack format is `docs/data_formats.md` section 12 and, for the newer
  content kinds, `docs/asset_pack_ecosystem.md`.
- `python tools\asset_store\content_examples.py --out <folder>` writes
  editable example packs and their ZIPs.
- The screen effect pack's source is `design/packs/jarjar-postfx/`, a worked
  example of a pack built from a folder.
- Generating content (HD upscales, voices) belongs in the editor or Labs,
  never in the game client.

## Not yet

- A server telling your client which packs it needs (shard content
  negotiation) is on the [Roadmap](Roadmap.md); nothing downloads or
  switches on by itself.
- Showing 3D splats in the world.
- A public hosted catalogue; today a store is a folder you serve yourself.
