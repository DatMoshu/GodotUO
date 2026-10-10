# tools/side_by_side

ClassicUO and GUO live on one monitor, side by side, standing on one tile, so
a difference can be walked around. ClassicUO takes the left half on the owner
account, GUO the right on the first of `UO_SHARD_GM_ACCOUNTS`; both `[go` to
the same spot and the shard is pinned to daylight. ClassicUO is built and
driven by `tools/ab_compare`, whose code this reuses. Needs a shard and the
desktop for the half minute ClassicUO takes to log in.

## Run

```
launchers\dev\side_by_side.bat --monitor NAME --place britain-street
launchers\dev\side_by_side.bat --at 1602 1591
launchers\dev\side_by_side.bat --dump LABEL     also write render dumps for tools/render_diff
launchers\dev\side_by_side.bat --list-monitors
```

## Tests

No tests; both clients are left running for a person to look at.
