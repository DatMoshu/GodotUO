---
name: parity-check
description: "Compare GUO's rendered output against ground truth from the UO client data, pixel by pixel. Captures a reference with the guoasset MCP, captures GUO's own frame, diffs them, and reports exactly where they differ. Use for any claim of visual parity, and whenever the port looks subtly wrong."
argument-hint: "<what to compare: art <id> | gump <id> | multi <id> | frame>"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash, Write, Edit
model: sonnet
---

# Parity Check

Settles the question "does GUO look like the real client?" with evidence
instead of an opinion.

It compares GUO against ground truth read from the same client data, which is
what `ADR-0001` actually requires.

---

## Why this exists

The parity bar is the classic freeshard community. They compare screenshots.
An unverified parity claim is worse than no claim, because it stops anyone
else from checking.

The trick is picking a reference that cannot drift: the `guoasset` MCP decodes
art from the **same `.mul`/`.uop` files GUO reads**. Reference and output come
from one source of truth, so any difference is GUO's bug — not a version skew,
a different install, or a rescaled screenshot.

---

## 1. Capture ground truth

Via the `guoasset` MCP (see `tools/guoasset/README.md`; build it once with
`launchers\dev\build_guoasset.bat`):

| Target | Tool |
|---|---|
| a land or static tile | `get_image` with `tile` + `kind` |
| a gump | `get_image` with `tile` + `kind=gump` |
| a multi, isometric composite | `get_image` with `multi` |
| a family of ids at once | `get_image` with `ids`, or `atlas_art` |

Write captures to `build/parity/` or the scratchpad. **Never commit them** —
they derive from proprietary client data.

If the MCP is unavailable, say so and stop. Do not substitute a screenshot of
another client and call it ground truth.

## 2. Capture GUO

```
launchers\dev\screenshot.bat
```

Match the reference conditions exactly: same graphic id, same hue, same
scale, no camera offset, no UI overlay. A comparison at a different zoom
proves nothing.

## 3. Diff

Compare pixel by pixel, not by eye. Report:

- **identical** — byte-identical or zero differing pixels
- **N pixels differ** — with the bounding box and a few sample coordinates
  (expected RGBA vs actual)
- **size mismatch** — dimensions differ; that is a framing bug, fix it first

Eyeballing two images side by side reliably misses a one-pixel offset and an
off-by-one palette index, which are the two most common real bugs here.

## 4. Diagnose

Map the symptom to a cause before changing code:

| Symptom | Almost always |
|---|---|
| Soft or blurry edges | Texture filtering. `default_texture_filter=0`; a new viewport or material lost it. |
| Colours close but wrong | Hue applied as a multiplicative tint instead of a palette lookup from `hues.mul`. |
| Whole image offset 1px | Rounding in the isometric transform, or a half-pixel origin. |
| Correct art, wrong object in front | Draw order. Check `CalculateDepthZ()` — see ADR-0001. |
| Right pixels, wrong object clicked | Picking recomputed depth instead of reading `CalculateDepthZ()`. |
| Transparent areas filled black | Alpha discarded on decode, or premultiplication applied twice. |

## 5. Report

State plainly:

- what was compared, and at which ids
- the numeric result (pixels differing, out of how many)
- where the reference came from
- **whether you actually ran the comparison**, or only inspected visually

"Looks right" is not a parity result. If you could not capture ground truth,
say that instead of downgrading the claim quietly.

---

## Rules

- The client install is **read-only**. `guoasset` has no write-side tools;
  do not add one.
- Captures are artefacts: `build/` or scratchpad, never committed.
- Parity before improvement. If GUO differs and GUO looks *better*, it is
  still a bug — record it, propose the change separately.
