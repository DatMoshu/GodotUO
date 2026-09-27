# Profile migration ladder

`python tools/profile_migrations/run.py` builds GUO and runs a dedicated
headless scene on desktop, mobile and web profile platforms. It uses the
real `Profile`, generated JSON context and `PlatformDefaults.Apply` code;
there are no duplicate migration implementations or mocked profile types.
No client data, login or existing profile files are needed or changed.

The fixtures start at each version v4 through v9 and migrate to the current
version (v10). Assertions cover the v5 window changes, v6 low-power default,
v7 shelf defaults, v8 mobile grid-loot correction, v9 macro row, and v10
screen saver (enabled on mobile; disabled on desktop and web). Custom
values and previously opted-out settings survive their migration gates.
Saved JSON reloads without a second migration; current and future versions
remain byte-for-byte unchanged through serialization.
The screen-saver timeout defaults to ten minutes; custom timeouts survive
migration and JSON reload. A v9 macro-row opt-out survives the v10 migration,
and v10/future screen-saver opt-outs remain off. New profiles are checked too.

Use `--no-build` after a successful client build, or `--dotnet <executable>`
when the SDK is not on PATH. Exit 0 means all three platforms passed; exit 1
means build, startup or assertions failed. The probe is never loaded by the
ordinary game scene.
