# ADR-0033: Share the SpriteMotion Fit Lab inside a native GUO editor host

Status: local editor host validated; clean-machine onboarding remains incomplete. Owner selected one shared fitting UI with native GUO integration.

GUO hosts the existing Three.js lab with an editor-only Chromium view. It does not reimplement fitting controls,
undo, or rig transforms. A native dock owns setup, service reconnection and eventual asset/staging integration.
The worker and saved fits remain usable from the standalone browser. Native Godot controls continue to own game
data, IDs, staging and shard testing. A render completing never automatically installs or publishes game data.

Windows x64 is the first supported host. Godot CEF 1.16.2 is downloaded from its upstream GitHub release and
verified against its SHA-256. The downloaded Chromium runtime is ignored. A small MIT-licensed editor-support library is built locally into tools/spritemotion/runtime (gitignored, because a release build embeds the builder's home path) and verified before installation; browser-patch.md records its source and build. SpriteMotion downloads use an
immutable public commit, not a moving main branch; explicit local-checkout overrides support ongoing development.
The installer uses GUO's Python, an isolated environment and an OS setup lock. Subprocess arguments do not pass
through a shell. Logs and downloads live under ignored build/spritemotion. Local settings follow GUO's existing
environment / config.local.bat / config.bat precedence.

Closing or reloading the editor destroys its browser view and detaches from the shared service. It does not kill
Blender jobs. The addon remains behind TOOLS. Browser binaries must be excluded from client exports; game clients
consume only finished assets. No commercial packs or UO data are part of automatic code installation.

Fresh machines still need a permitted canonical fitting model and prepared asset catalog. The public repository
does not contain those private assets. Do not describe an empty fresh install as a ready-to-render UO content pack.
Drag/drop of raw assets, automatic pack preparation, staging import, and an in-game equip acceptance test are
separate milestones; the first host provides the same currently supported lab operations.
