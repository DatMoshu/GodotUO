---
name: web-build
description: "Try to build GUO for the web and report exactly where it stops: doctor, headless export, the local COOP/COEP server, then the headless-browser smoke. With Godot 4.7 mono the export is refused upstream (ADR-0008); the skill reports that refusal verbatim rather than working around it. Use for 'can we export to the web yet' and after an engine or template upgrade."
argument-hint: "[--no-export] [--timeout <s>]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# Web Build

Runs the web pipeline in order and reports what happened. The tool is
`tools\web\run.py` (setup and the reasoning in `tools\web\README.md` and
`docs\architecture\ADR-0008-web-target.md`). Today the expected result is a
refusal at the export step; the point of running it is to notice the day
that changes.

`$ARGUMENTS` go to the smoke.

---

## 1. Doctor

```
launchers\web\doctor.bat
```

Read every line. With the pinned engine the `Web export templates` line is
`MISS` and says why (no `web_*` template in a mono build; godotengine/godot
#70796). If that line is `ok`, the engine or templates changed: say so
prominently, because everything downstream is untested against it.

## 2. Export

```
launchers\web\export.bat
```

Exit 1 today. Quote the `ERROR:` lines from `build\web\export.log`. If the
text differs from the one recorded in ADR-0008 (`Exporting to Web is
currently not supported in Godot 4 when using C#/.NET`), quote the new text
and stop: it needs a human decision.

## 3. Serve and smoke

Only when the export produced `build\web\GUO.html`:

```
launchers\web\smoke.bat [--no-export] [--timeout 120]
```

Passes when the browser console shows a `[GUO]` line or the Godot banner
without a `SharedArrayBuffer`/`Uncaught` error; output in
`build\web\smoke_console.txt` and `build\web\smoke.png`. Read the screenshot
and say what it shows.

`launchers\web\serve.bat` alone serves the folder for a person to open at
`http://127.0.0.1:<UO_WEB_PORT>/GUO.html`; mention it only when there is a
page to serve.

## 4. Report

One table:

| Step | Command | Exit | Evidence |
|---|---|---|---|
| doctor | `launchers\web\doctor.bat` | 1 | templates MISS (expected), wasm workload state, browser found |
| export | `launchers\web\export.bat` | 1 | the refusal line from `build\web\export.log` |
| smoke | `launchers\web\smoke.bat` | not run | no page to load |

Paths as clickable `file:///` URIs. Do not suggest Godot 3, a non-mono
build, or a rewrite as the fix; ADR-0008 rejected each and says why.
