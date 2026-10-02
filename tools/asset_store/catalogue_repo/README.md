# GUO packs — the official catalogue

This repository is the official catalogue of packs for
[GodotUO](https://github.com/DatMoshu/GodotUO), a classic Ultima Online
client on Godot 4. Packs add backgrounds, screen savers, sounds, themes,
screen effects, Razor scripts, and game content such as items, map blocks,
decorations and creatures.

The client lists this catalogue by default. Each listing here is reviewed in
a pull request before it appears. The catalogue is signed: CI signs the index
on every merge, and the client refuses an index that does not verify.

- **The catalogue:** <https://datmoshu.github.io/GodotUO-packs/>
- **Signing key fingerprint:** `KEY-FINGERPRINT` (the client shows the same one)

## What is in this repository

Only listings: one small JSON file per pack version, at
`packs/<id>/<version>.json`. The pack ZIPs live wherever their authors and
mirrors host them. A listing pins the ZIP's SHA-256 and size, so a pack is
the same pack wherever it is downloaded from.

```json
{
  "urls": ["https://example.org/my-pack-1.0.0.zip"],
  "sha256": "<64 lowercase hex digits of the ZIP>",
  "size": 123456,
  "provenance": "Drawn in Aseprite; the music recorded by the author"
}
```

The format is in GodotUO's `docs/data_formats.md`, section 12. The reasons
behind it are in ADR-0026.

## Submitting a pack

1. Build and check your pack with GodotUO's tools:
   `python tools/asset_store/run.py verify my-pack.zip`.
2. Host the ZIP somewhere that serves HTTPS: a release asset on your own
   GitHub repository works well.
3. Open a pull request here that adds `packs/<id>/<version>.json`. The check
   downloads your ZIP, verifies it, and reports what it found.
4. A maintainer reviews the content against the
   [content policy](https://github.com/DatMoshu/GodotUO/blob/main/docs/store/content_policy.md).
   The short version: original work, or openly licensed work credited
   properly. Nothing taken or derived from the Ultima Online client's files.

A new version of a pack is a new listing file. Published versions do not
change.

## Mirrors

A mirror keeps a full copy of the packs, so downloads do not depend on one
host. To run one, see
[hosting](https://github.com/DatMoshu/GodotUO/blob/main/docs/store/hosting.md),
then ask in an issue for your base URL to be added. The index lists mirrors
first, and the client falls back to the next URL when one is down. A mirror
cannot change a pack: the client checks every byte against the signed hash.

## Reporting a problem

Use the **Report a pack** issue form for a pack that breaks the content
policy or infringes your rights. The pack is delisted while it is reviewed.

## Licence

The listing files and this repository's text are dedicated to the public
domain under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).
Each pack has its own licence, stated in its manifest.
