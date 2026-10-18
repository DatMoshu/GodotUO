# JarJar's screen effects pack

Source: MeesaJarJar's `4a03a1a5e783e66c1b364da2d26a0a8ea3e025ef`, scoped
from PR41. The original 32 preset JSONs and 29 shaders are retained verbatim,
outside the client resources. The brief's "29" counts shaders/effects; Death
and Poison reuse Damage, and Old Film composes Film Grain with the existing
Mono and Vignette shaders. All 32 source looks are preserved. No client code
or additional built-in preset is shipped by this change.

Build and publish into a local catalogue:

```text
python tools/asset_store/postfx_pack.py --store-dir build/postfx_catalogue
python tools/asset_store/run.py --store-dir build/postfx_catalogue serve
```

In GUO, open **Options → Video → Store**, add the printed loopback catalogue
URL, and install **JarJar's screen effects**. Open the **Screen effects** menu
from the mobile window controls and choose a look. Installing does not apply
one automatically. Choose **Classic** to switch every effect off. The pack
uses ADR-0023's existing Store loader; the original curated built-ins remain.

The ZIP is `build/asset_packs/jarjar-postfx-1.0.0.zip`; it passes the ordinary
publisher and installer checks. A flat payload layout lets the existing loader
discover presets and shaders. Preview pixels are an original generated checker,
not game art. The pack carries the project's BSD-2-Clause notice and author
credit. Public catalogue submission is a separate review action.

Effects are optional visual changes. Time-bound effects animate only when
selected; cost and renderer compatibility vary. The three composite/alias
presets explain why the preset count differs from the shader count.

Fog of War carries an optional `facing` binding. Main has no facing state
provider, so the existing framework leaves its cone direction at the preset
value (editable manually in the menu). Automatic player-following direction is
not enabled by this pack; its default 360-degree cone needs no direction.
