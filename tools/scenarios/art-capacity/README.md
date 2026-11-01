# Offline art-capacity qualification scenario

Layer L4; toggle: standalone harness, or Debug `GuoLabs=true` compilation.
No shard/account/install needed. This is a CPU contract scenario; the interactive
scenario runner has no CPU executable surface, so use the launcher directly.

1. `launchers/dev/art_capacity.bat doctor`: require doctor OK.
2. `launchers/dev/art_capacity.bat`: require `[art-capacity] OK` and exit 0.
3. `python -m pytest tools/msbuild/test_labs.py -q -p no:cacheprovider`:
   verify default Debug, Release and exports exclude Labs; opted-in Debug includes it.
4. `launchers/dev/smoke.bat`: require OK before a code commit. Its synthetic
   art-capacity line runs without enabling any client Labs feature.

The launcher tests IDs 65535/65536 and 262143/262144, signed/unsigned extremes,
explicit-format mismatches, truncations, malformed counts and offsets, sparse
indices, complete metadata groups, equipment terminators and separate hue flags,
terminal negotiation failure, and bounded cache eviction/invalidation/reload.

Remaining integration scenario (requires an Accepted ADR and separate review):
matched isolated server/tool versions must preserve high IDs through save/reopen,
static/multi/house data, equipment, container snapshots, effects, targeting, actual
rendering and pixel picking. Compare legacy packets and visuals with the seam off;
qualify UOP sparse logical indices and GPU residency. No public shard test.
