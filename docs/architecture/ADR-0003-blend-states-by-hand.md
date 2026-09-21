# ADR-0003: Reimplement XNA's Blend Unit Rather Than Map to Godot's Modes

## Status

Accepted

## Date

2026-09-20

## Last Verified

2026-09-20 — measured against upstream at pin
`007ef8c3e13dec13fcc02387fe4817ef6f371c85`.

## Decision Makers

Project owner; `uo-render-engineer`.

## Summary

ClassicUO's effects draw under five custom blend states. Godot's canvas has
five *fixed* blend modes, and they are not the same five. Rather than map each
upstream state to its nearest Godot mode, we reimplement XNA's fixed-function
blend unit in a shader: the batcher pushes the six `BlendState` values into
`uo_hue_blend.gdshader`, which reads the destination back and evaluates the
equation itself.

## Context — measured, not assumed

`GameEffectView` builds five states and `LightningEffectView` uses two of
them. Written as (colour source factor, colour destination factor, function):

| State | Equation | Nearest Godot mode |
|---|---|---|
| multiply | `dst * src` | `blend_mul`, but only when source alpha is 1 |
| screen | `src + dst` | `blend_add`, but Godot's scales src by its alpha |
| screenLess | `src*dst + dst*(1-srcA)` | `blend_mul` — an exact match |
| normalHalf | `src*dst + dst*src`, i.e. `2*src*dst` | none |
| shadowBlue | `dst*(1-src) - src*src`, reverse subtract | none |

So one of five maps exactly, two map only while alpha happens to be 1, and two
do not map at all. Approximating three of five is not parity, and the way it
fails is invisible: an effect slightly too bright is not something anyone
spots in a screenshot.

Godot's `render_mode` blend is fixed at shader compile time, so even the modes
that do exist cannot be switched per draw on one material.

**Measured** — `launchers\dev\blend_probe.bat`: a canvas item created through
`RenderingServer` *can* read what was drawn underneath it, using
`canvas_item_set_copy_to_backbuffer` together with `hint_screen_texture`. A
reverse subtract evaluated by hand against that read matched the arithmetic to
within 0.001. This is what makes the decision below possible; it was not
assumed, because Godot 4.7.2 is past the assistant's knowledge cutoff.

## Decision

### 1. `BlendState` stays XNA-shaped, as plain data

`GUO.Renderer.BlendState` has XNA's six properties under XNA's names, with
XNA's defaults — `new BlendState()` is opaque, `(One, Zero, Add)`. Upstream's
effect states set only the colour triple and rely on the alpha defaults, so
the defaults are load-bearing, not cosmetic. `Blend` and `BlendFunction` are
XNA's enums in XNA's declaration order.

This is what lets `GameEffectView` port with its five state objects written
exactly as upstream writes them.

### 2. The shader evaluates the equation, not a lookup table of special cases

`uo_hue_blend.gdshader` takes the six values as uniforms and implements
`factor()` and `combine()` generically. It is a reimplementation of the
fixed-function unit, not a translation of the five states that exist today, so
a sixth state arriving from upstream keeps working without anyone noticing it
had to.

`Blend.BlendFactor`, `InverseBlendFactor` and `SourceAlphaSaturation` throw.
No ClassicUO state uses them, and a constant blend colour would need plumbing
with no caller. Naming the gap beats mapping them to something nearby.

### 3. The default path keeps hardware blending

`SetBlendState` recognises premultiplied alpha — `(One, InverseSourceAlpha,
Add)`, which is XNA's `BlendState.AlphaBlend` and what upstream draws
everything under — and routes it to the ordinary `uo_hue.gdshader` with no
back-buffer read at all. Only genuinely custom states pay for the read.

**This corrected an existing bug.** `uo_hue.gdshader` was on `blend_mix`,
which is `(SourceAlpha, InverseSourceAlpha)`. But `uo_shade` returns
`color * alpha`, exactly as upstream's `IsometricWorld.fx` does, which is a
*premultiplied* colour — upstream then blends it with `BlendState.AlphaBlend`,
which is `(One, InverseSourceAlpha)`. Under `blend_mix` every translucent
sprite in the client would have been multiplied by alpha a second time and
drawn too dark. The mode is now `blend_premul_alpha`.

### 4. Both shaders share one include

`uo_hue_core.gdshaderinc` holds the palette lookup, the mode switch and the
circle of transparency. The two `.gdshader` files are a `render_mode` line, a
`vertex()` and a `fragment()` each. The hueing cannot drift between them.

The one duplicated line is `packed_hue = COLOR;`. Godot rejects a varying
assigned anywhere but `vertex()` or `fragment()` directly — *"Varying may not
be assigned in the 'uo_vertex' function"* — so it cannot live in a shared
helper.

## Alternatives Rejected

**Map each state to the nearest Godot mode.** Cheapest, and needs no
back-buffer read. Rejected: two of the five have no mapping at all, and two
more are wrong whenever alpha is below 1. Project rule 3 is parity before
improvement, and this would be neither.

**Render effects to a SubViewport and composite.** Moves the problem: the
composite still needs the blend equation, and it adds a full-screen target per
effect layer.

**A shader variant per blend state, using `render_mode`.** Would keep hardware
blending for `multiply` and `screenLess`. Rejected: it still cannot express
`normalHalf` or `shadowBlue`, so the by-hand path is needed anyway, and then
there would be two mechanisms instead of one.

## Consequences

* `GameEffectView` and `LightningEffectView` port unchanged. With every area
  enabled the error count goes 80 → 66, and every `GameObject` abstract-member
  error is gone.
* A custom-blend sprite costs a back-buffer copy and breaks batching across
  itself. Affordable: effects are a handful on screen at once, and the default
  path is untouched.
* Translucent sprites are no longer drawn too dark — see 3 above.
* Blend materials are cached per distinct state. There are five in the client,
  so the cache does not grow.

## Validation

* `launchers\dev\blend_probe.bat` — the back-buffer read works from a
  `RenderingServer` canvas item.
* `launchers\dev\batcher_probe.bat` — draws through the real batcher under all
  five upstream states plus the default, and checks each against the equation
  computed independently in C#. All six match to within one 8-bit step, and
  the hue checks alongside them match exactly.
* Every shader uniform that is global state reaches *every* material, not just
  the default one. Setting only the default is how one effect sprite ends up
  unhued while everything beside it looks right.
