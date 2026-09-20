# `UOPort.Compat` — the XNA compatibility shim

This folder is the single highest-leverage piece of the port, so it is worth
understanding before touching anything else.

## Why it exists

The port audit (`launchers\pipeline\03_port_audit.bat`) classifies all 429
upstream C# files by how tightly they bind to FNA:

| Tier | Files | Lines | Meaning |
|---|---:|---:|---|
| `verbatim` | 243 | 74,522 | No FNA reference at all |
| `shim` | 102 | 50,513 | Imports **only** `Microsoft.Xna.Framework` |
| `rewrite` | 88 | 31,170 | Touches Graphics / Input / Audio / Media |

The `shim` tier — a third of the codebase — does not actually use FNA for
rendering. It uses FNA's *value types*: `Point`, `Rectangle`, `Color`,
`Vector2`, `Vector3`. Those are plain maths structs with no engine behaviour
behind them.

So instead of rewriting 50,000 lines to speak Godot's types, this folder
provides types with the **same names and the same public API**, living in the
`UOPort.Compat` namespace. A ported file then needs one edit at the top:

```diff
- using Microsoft.Xna.Framework;
+ using UOPort.Compat;
```

and the remaining thousand lines compile untouched.

## What this is not

It is **not** an emulation of FNA, and it is not permanent scaffolding to be
apologised for. It is a deliberate type-compatibility layer, the same
technique any large port uses to avoid a big-bang rewrite. The engine-facing
boundary is narrow and explicit: `ToGodot()` / `FromGodot()` conversions on
each type, used only where ported code hands data to Godot.

## Rules

1. **No engine behaviour in here.** These are value types. Anything that
   touches `RenderingServer`, textures, or nodes belongs in `src/Render`.
2. **Match XNA semantics exactly, including the surprising parts.**
   `Color` stores non-premultiplied 8-bit channels and scales all four —
   alpha included — when multiplied by a float; `Rectangle.Contains` is
   inclusive of the top-left and exclusive of the bottom-right. Ported code
   depends on these edge cases whether or not it says so. Deviating here
   produces off-by-one rendering bugs that are extremely hard to trace back.
3. **Keep conversions explicit.** Never add implicit operators to Godot
   types. An implicit conversion makes it invisible where port code crosses
   into engine code, which is exactly the boundary reviewers need to see.
4. **Do not grow this folder past value types.** If a shim starts needing a
   `GraphicsDevice`, that file belongs in the `rewrite` tier instead.

## Status

| Type | State |
|---|---|
| `Point` | implemented |
| `Rectangle` | implemented |
| `Color` | implemented |
| `Vector2` / `Vector3` / `Vector4` | aliased to Godot's own (API-compatible) |
| `Matrix` | **not shimmed** — only 36 uses, all inside the renderer rewrite |
