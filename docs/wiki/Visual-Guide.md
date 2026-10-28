# Visual guide and capture register

These are real GUO screenshots, captured on 2026-10-28 from source baseline
`fc9e35880bcd201d4ed78aa9de562b2d971cc496`, using Godot
`4.7.2.stable.mono.official.ed1daf0bf`, Debug/editor with `GuoLabs=false`.
An isolated settings/workspace profile and locally installed UO `7.0.107.76`
were used. Original client files and extracted artwork are not redistributed.

The editor tour ran **without a live shard** at 1920 × 1080, editor scale 1.
It supplies its own yellow callouts and normalizes personal paths/endpoints.
Selected images are cropped and losslessly PNG-optimized; no UI is fabricated.
Click an image to inspect its native size. Per-image dimensions and hashes are
in the [public asset register](../images/guides/captures.json).

## What these pictures prove

A capture shows the pictured layout and state. It does not prove motion,
performance, network round trips, provider authentication or every action
available in a tab. The tour ended with failed checks against several moved
legacy controls, including edit/overlay markers. The targeted Assets run
failed native hue preparation verification. Full source-only smoke built
successfully but failed sound batch export; its splat-LOD fixture check also
failed. None is presented as a passing end-to-end run. Gump Studio reported
39 checks passing; its visual process also logged disposal errors at shutdown.
The separate UO+ options probe reported Apply/Defaults/Cancel and focus/scroll
checks passing. This is documentation evidence, not a gameplay acceptance gate.

## Screenshot index

| Surface | Guide | Capture |
|---|---|---|
| Classic login | [Getting Started](Getting-Started.md) | [login](#login) |
| UO+ options | [Gameplay and Settings](Gameplay-and-Settings.md) | [uoplus-options](#uoplus-options) |
| Assets workbench | [Assets and Fiddler](Assets-and-Fiddler.md) | [assets-workbench](#assets-workbench) |
| Search by asset ID | [Editor](Editor.md) | [asset-search](#asset-search) |
| Gump art browser | [Assets and Fiddler](Assets-and-Fiddler.md) | [gump-browser](#gump-browser) |
| Animation browser | [Assets and Fiddler](Assets-and-Fiddler.md) | [animation-browser](#animation-browser) |
| Hue working palette | [Assets and Fiddler](Assets-and-Fiddler.md) | [hue-operations](#hue-operations) |
| Hue import review | [Assets and Fiddler](Assets-and-Fiddler.md) | [hue-import](#hue-import) |
| Cliloc CSV review | [Assets and Fiddler](Assets-and-Fiddler.md) | [cliloc-import](#cliloc-import) |
| Cliloc language selection | [Assets and Fiddler](Assets-and-Fiddler.md) | [cliloc-japanese](#cliloc-japanese) |
| Sound browser | [Assets and Fiddler](Assets-and-Fiddler.md) | [sound-browser](#sound-browser) |
| WAV import review | [Assets and Fiddler](Assets-and-Fiddler.md) | [sound-import](#sound-import) |
| Sound batch review | [Assets and Fiddler](Assets-and-Fiddler.md) | [sound-export-review](#sound-export-review) |
| Multi component browser | [Building Multis](Building-Multis.md) | [multi-browser](#multi-browser) |
| World tools and inspector | [World Editing](World-Editing.md) | [world-tools](#world-tools) |
| Cell and altitude guides | [World Editing](World-Editing.md) | [world-guides](#world-guides) |
| Minimap visibility | [World Editing](World-Editing.md) | [world-map](#world-map) |
| Water authoring | [Water](Water.md) | [water-authoring](#water-authoring) |
| AI chat controls | [AI and MCP](AI-and-MCP.md) | [ai-chat](#ai-chat) |
| Agent queue controls | [AI and MCP](AI-and-MCP.md) | [ai-queue](#ai-queue) |
| Content Store surface | [Asset Store](Asset-Store.md) | [content-store](#content-store) |
| Gump Studio | [Editor](Editor.md) | [gump-studio](#gump-studio) |
| Editor run bar | [Servers and Accounts](Servers-and-Accounts.md) | [editor-runbar](#editor-runbar) |

<a id="login"></a>

### Classic login

![Classic login in GUO](../images/guides/login.png)

Empty account fields in the classic pregame path. Actual captured viewport is 640 × 480; no shard login was attempted. [Open the guide](Getting-Started.md).

<a id="uoplus-options"></a>

### UO+ options

![UO+ options in GUO](../images/guides/uoplus-options.png)

1280 × 720 local UI probe: touch mode, experimental zoom, water and ambient toggles. This is a probe fixture, not a player profile recommendation. [Open the guide](Gameplay-and-Settings.md).

<a id="assets-workbench"></a>

### Assets workbench

![Assets workbench in GUO](../images/guides/assets-workbench.png)

Library, paged art grid and item metadata. The harness requested 1366-wide logical sizing; the actual image is 2049 × 1350 because of desktop DPI. [Open the guide](Assets-and-Fiddler.md).

<a id="asset-search"></a>

### Search by asset ID

![Search by asset ID in GUO](../images/guides/asset-search.png)

F3-style search locating backpack 0x0E75. The image is cropped to the search surface; selection does not modify installed art. [Open the guide](Editor.md).

<a id="gump-browser"></a>

### Gump art browser

![Gump art browser in GUO](../images/guides/gump-browser.png)

Gump 0x07D0 with its source class reference. Browse UI art here; use Gump Studio to author layouts. [Open the guide](Assets-and-Fiddler.md).

<a id="animation-browser"></a>

### Animation browser

![Animation browser in GUO](../images/guides/animation-browser.png)

Body, action and direction controls with frame preview. A still does not verify playback or equipment layering. [Open the guide](Assets-and-Fiddler.md).

<a id="hue-operations"></a>

### Hue working palette

![Hue working palette in GUO](../images/guides/hue-operations.png)

Project hue editing, comparison preview and range operations. Native hue preparation failed independent verification in this capture run; this image is not an export success receipt. [Open the guide](Assets-and-Fiddler.md).

<a id="hue-import"></a>

### Hue import review

![Hue import review in GUO](../images/guides/hue-import.png)

Fiddler text palette compared with the current working palette before an undoable import. The installed palette stays unchanged. [Open the guide](Assets-and-Fiddler.md).

<a id="cliloc-import"></a>

### Cliloc CSV review

![Cliloc CSV review in GUO](../images/guides/cliloc-import.png)

Synthetic project text with changed placeholder tokens, shown before importing into a working copy. Review tokens against consuming scripts. [Open the guide](Assets-and-Fiddler.md).

<a id="cliloc-japanese"></a>

### Cliloc language selection

![Cliloc language selection in GUO](../images/guides/cliloc-japanese.png)

Japanese source strings displayed in the working-document UI. This does not prove runtime translation provider support. [Open the guide](Assets-and-Fiddler.md).

<a id="sound-browser"></a>

### Sound browser

![Sound browser in GUO](../images/guides/sound-browser.png)

Occupied sound IDs and PCM metadata. A screenshot cannot demonstrate audible output. [Open the guide](Assets-and-Fiddler.md).

<a id="sound-import"></a>

### WAV import review

![WAV import review in GUO](../images/guides/sound-import.png)

Synthetic silence compared with source PCM before an undoable working-copy import. No source sound archive is distributed. [Open the guide](Assets-and-Fiddler.md).

<a id="sound-export-review"></a>

### Sound batch review

![Sound batch review in GUO](../images/guides/sound-export-review.png)

Selected effects, output naming and destination controls before writing. The full source-only smoke failed sound batch export; this picture proves layout only. [Open the guide](Assets-and-Fiddler.md).

<a id="multi-browser"></a>

### Multi component browser

![Multi component browser in GUO](../images/guides/multi-browser.png)

House multi 0x0064 with its component list. The preview is clipped at this layout; this is not a complete multi render or native staging proof. [Open the guide](Building-Multis.md).

<a id="world-tools"></a>

### World tools and inspector

![World tools and inspector in GUO](../images/guides/world-tools.png)

Britain in the editor with brush library, picker and minimap. No live shard is connected. [Open the guide](World-Editing.md).

<a id="world-guides"></a>

### Cell and altitude guides

![Cell and altitude guides in GUO](../images/guides/world-guides.png)

Editor-only cell grid and altitude numbers over the world view. These overlays are separate from normal player rendering. [Open the guide](World-Editing.md).

<a id="world-map"></a>

### Minimap visibility

![Minimap visibility in GUO](../images/guides/world-map.png)

A different map location with the minimap hidden. This is an editor view, not proof of navigation packets or player movement. [Open the guide](World-Editing.md).

<a id="water-authoring"></a>

### Water authoring

![Water authoring in GUO](../images/guides/water-authoring.png)

Water controls and Britain docks. The left test preview is explicitly synthetic; the right view uses installed art. No shard is needed for the preview. [Open the guide](Water.md).

<a id="ai-chat"></a>

### AI chat controls

![AI chat controls in GUO](../images/guides/ai-chat.png)

Cropped AI dock with an empty conversation. The tour normalizes local endpoint text; no prompt was sent and no provider session was tested. [Open the guide](AI-and-MCP.md).

<a id="ai-queue"></a>

### Agent queue controls

![Agent queue controls in GUO](../images/guides/ai-queue.png)

Empty shared request queue with no registered addresses or leases. This is not proof of delivery to an external agent. [Open the guide](AI-and-MCP.md).

<a id="content-store"></a>

### Content Store surface

![Content Store surface in GUO](../images/guides/content-store.png)

Empty catalogue browsing surface. This tour did not fetch a catalogue, install a pack or accept a signing key. [Open the guide](Asset-Store.md).

<a id="gump-studio"></a>

### Gump Studio

![Gump Studio in GUO](../images/guides/gump-studio.png)

1600 × 1000 authoring surface with an imported classic status layout and element properties. Fixture values are synthetic. [Open the guide](Editor.md).

<a id="editor-runbar"></a>

### Editor run bar

![Editor run bar in GUO](../images/guides/editor-runbar.png)

The existing tour highlights run controls. Its green status indicator is tour state; no live-server readiness is inferred. [Open the guide](Servers-and-Accounts.md).

## Existing gameplay illustration

![GUO showing the gardens of Castle British on a local ModernUO shard](../images/castle-british.png)

This retained repository illustration is historical: its original build,
profile and viewport were not recorded. It is not proof of current renderer
correctness.

## Documentation diagrams

| Diagram | Explains | Alternative |
|---|---|---|
| [Architecture](Architecture.md) | Client state, Godot host, rendering and editor boundaries | [PNG](../images/guides/architecture.png), [SVG](../images/guides/architecture.svg) |
| [Asset pipeline](Assets-and-Fiddler.md#review-and-stage) | Source, project, review, stage and read-back | [PNG](../images/guides/asset-pipeline.png), [SVG](../images/guides/asset-pipeline.svg) |
| [AI tool flow](AI-and-MCP.md#request-flow) | Gate, permission, approval, dispatch and result | [PNG](../images/guides/ai-tool-flow.png), [SVG](../images/guides/ai-tool-flow.svg) |
| [Install workflow](Install-and-Update.md) | Configure, verify, build, connect and recover | [PNG](../images/guides/first-session.png), [SVG](../images/guides/first-session.svg) |

## Capture gaps

The [feature matrix](Feature-Coverage.md) is broader than this gallery.
New connected gameplay, all Options pages, renderer variants, every Fiddler
exchange, native staging/read-back, Admin mutations, live edits, physical
devices and external AI sessions need separate receipts. Source references
and historical proofs remain useful, but are not current visual coverage.
