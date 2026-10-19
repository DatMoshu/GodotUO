# Repaint statics in StaticStudio

StaticStudio is an editor package. Select a static in the UO Inspector, choose
**Use inspected static**, then choose an API-format ComfyUI img2img workflow,
enter a prompt and press **Generate image**. Nothing contacts ComfyUI until
you request generation. The target's pixels are captured when bound; binding
a different target cancels the previous job so its result cannot repaint a
different static.

ComfyUI uses `UO_COMFY_URL` and the Art workflow folder. Before using a real
server, run `launchers/comfy/doctor.bat` (or `.sh`) and use `setup_studio` for
missing pinned dependencies. Restart ComfyUI after manually installing node
requirements with its Python, then run `doctor --validate` for node types.
The doctor and editor do not prove that a model can perform inference.

Choose a gallery image and preview it in the World tab. Preview is temporary;
unchecking it restores the existing theme. Choose background handling and a
theme name, then **Save gallery image as theme variant** to save the image,
theme mapping and provenance through the existing variant store. Apply a theme
to a zone, or **Clear** to restore originals. Edits never write to the UO install.
Use existing Store tooling for packaging generated content.

In **Editor Settings > GUO > AI > Enabled**, disabling AI hides image generation
and workflow discovery, cancels pending work and drops the unsaved gallery and
preview. Saved themes and already-staged splats remain available for manual
authoring. Disabling/re-enabling AI, changing a bound target or closing/reloading
the editor prevents late replies from populating a new gallery. Real ComfyUI
jobs already accepted by its server may continue there; cancelling the editor's
request does not interrupt another user's shared server queue.

The package compiles only under `#if TOOLS`; exported clients do not carry its
types. This change does not remove or migrate existing client generation code.

Verification: [headless repaint scenario](../tools/scenarios/editor/staticstudio_repaint.md),
[theme and studio contract](data_formats.md#39-client-side-themes-themesthemejson-staticstudio).
