# Godot Engine — Version Reference

| Field | Value |
|-------|-------|
| **Engine Version** | **Godot 4.7.2 stable, mono/.NET** |
| **Build string** | `4.7.2.stable.mono.official.ed1daf0bf` |
| **Released** | 2026-08-18 |
| **Project Pinned** | 2026-09-20 |
| **Last Docs Verified** | — (not yet verified against 4.7 docs) |
| **Assistant Knowledge Cutoff** | May 2026 |

The pinned build lives in `tools/godot/` (gitignored) and is restored by
`launchers\dev\fetch_godot.bat`. The **mono/.NET** build is mandatory: this
project ports ClassicUO's C# and the standard build cannot compile C#.

---

## Knowledge Gap Warning

> **Godot 4.7 was released after the assistant's May 2026 knowledge cutoff.**

This matters concretely. Treat any Godot 4.7-specific API as **unverified**
until checked against the real engine or the official docs:

| Version | Assistant knowledge | Risk |
|---|---|---|
| ≤ 4.5 | In training data | LOW |
| 4.6 | Near cutoff | MEDIUM — verify |
| **4.7 / 4.7.2** | **Post-cutoff** | **HIGH — must verify** |

### How to verify cheaply

The engine is right here, so do not guess and do not argue from memory:

```bat
REM Does a class or method actually exist in this build?
tools\godot\godot-console --headless --doctool <outdir>

REM Or run a throwaway script against the real engine
tools\godot\godot-console --headless --path godot\GUO --script res://tools/probe.cs
```

For rendering work specifically, `RenderingServer` and the shader language
have changed across 4.x releases. The renderer is the one area of this port
where a wrong-version assumption is expensive, because it surfaces as subtly
wrong pixels rather than a compile error.

### When you do verify something

Record it — in this folder, or in the relevant ADR's **Engine Compatibility**
table. An unverified assumption that gets confirmed and then forgotten costs
the same as never having checked.

---

## Why 4.7.2 and not 4.6

4.7.2 is the latest stable at the time of pinning, and the user had export
templates for both. For a port expected to run for months, starting on the
newest stable avoids a forced mid-project upgrade. The trade-off — accepted
deliberately — is the post-cutoff knowledge gap above.

## Upgrading

1. Edit `GODOT_VERSION` in `launchers\_shared\config.bat`.
2. Run `launchers\dev\fetch_godot.bat`.
3. Update the two `.cmd` shims in `tools/godot/` and its README.
4. Update this file, including **Assistant Knowledge Cutoff** and the risk table.
5. Re-validate any ADR whose Engine Compatibility risk is MEDIUM or HIGH.
6. Run `launchers\dev\smoke.bat` before committing.
