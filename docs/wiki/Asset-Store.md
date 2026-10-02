# Asset Store

The GodotUO Asset Store installs **content packs** into the client: login and
game backgrounds, screen-saver loops, themes, sounds and profile presets.
Options > **Store** shows a catalogue where you can search, filter by kind,
install, update and uninstall.

## What a pack can and can't do

- A pack is a ZIP of plain files plus a `manifest.json` with a hash for every
  file.
- Installing a pack only stores its files. It never runs code, never merges
  arbitrary settings into your profile, and never replaces game art.
- **Art overrides are disabled.** UO art is proprietary, and the Store doesn't
  carry it.
- The supported kinds are `background`, `theme`, `sound` and `profile-preset`.

## How installs stay safe

- The catalogue's index gives each pack's hash, and the download is checked
  against it.
- Every file is then checked against the manifest.
- An install is unpacked beside its destination and swapped in only after
  every check passes, so a bad download never replaces a good pack.
- The client refuses:
  - links, path traversal, names that differ only in case, executables,
    UO data files and oversized content;
  - redirects: packs must come from the configured store address.
- Versions never change once published. An update adds the new version beside
  the old one, which stays until you remove it. A pack that leaves the
  catalogue stays removable offline.
- On a remote store, HTTPS proves who published a pack. Hashes check integrity
  only; they aren't signatures.

## Running a store

`tools/asset_store` (Python standard library only) checks, publishes, indexes
and serves packs from a folder:

```
python tools\asset_store\run.py --help
python tools\asset_store\seed.py      ten CC0 sample packs from the built-in background loops
python tools\asset_store\smoke.py     publish, install, update and uninstall, end to end
```

Point the client at a store with `UO_STORE_URL`, or set a per-character store
address in Options. The store's root also serves a searchable static page of
pack cards. Hosting the public catalogue on GitHub Pages is planned.

## Making a pack

The pack and index format is `docs/data_formats.md` section 12, which is the
contract for both the publisher and the client. Only publish content you hold
the rights to, and put its licence in the pack's manifest.

The design record is ADR-0019 (`docs/architecture/ADR-0019-asset-store.md`).
