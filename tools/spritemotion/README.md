# Fit Lab in GUO

Open the GUO editor, select the **Fit Lab** bottom tab, and click **Open Fit Lab**.
Float the dock or enlarge the bottom panel for the full fitting workspace.

The host runs the same Three.js Fit Lab as the standalone browser. They share
saved adjustments, backups and Blender jobs. Closing GUO does not stop those jobs.

On first use, GUO installs the pinned Chromium runtime (approximately 1 GB download),
verifies its checksum, applies the bundled editor-support library, and prepares an
isolated SpriteMotion Python environment. Downloads are cached. Subsequent opens
reconnect to the running service or start it automatically. No Rust build is needed.

This uses the Python already required by GUO. It does not yet install Blender or
prepare a fitting model/catalog on a clean machine. Public SpriteMotion code does
not include proprietary UO models, client data, or commercial packs. Therefore a
fresh public install is not yet a zero-setup rendering workstation. Those inputs
need an import/preparation flow before that promise can be made.

Existing workspaces use SPRITEMOTION_ROOT and SPRITEMOTION_FIT_PACK in GUO's usual
local configuration; a single exported catalog is selected automatically. With no
local checkout, the installer fetches an immutable public SpriteMotion revision.
It never updates a user's checkout or private sidecar. Software updates should be
explicit version updates after validation, not an automatic pull of a moving branch.

Logs: build/spritemotion/service.log. To verify setup independently:

    python tools/spritemotion/run.py open
    python tools/spritemotion/test_setup.py

Maintainer smoke: launch the editor with user argument --spritemotion-smoke. It
loads the lab, captures build/spritemotion/editor.png, and exits. This requires an
existing prepared catalog. See browser-patch.md for the native library provenance.

Validated locally: C# build; setup regression tests; HTTP 200 page load; Three.js
and rendered-frame display in the dock; mouse dropdown and equipment-slot switching.
Not yet validated: a fresh-machine install, C# hot reload with an active browser,
raw asset drag/drop from GUO, full render-to-game staging, and exported-client scans.
Export presets exclude addons/godot_cef as well as the native editor addon.
