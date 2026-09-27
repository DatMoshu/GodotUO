---
name: android-build
description: "Build GUO for Android and prove it on the attached device: doctor, headless export, adb install, then the smoke that waits for the login gump and files a screenshot. Reports one table of what ran and what it returned. Use for 'does it still build for the phone', after any change under src/Input/Touch, and before claiming anything works on a device."
argument-hint: "[--no-export] [--args \"--host <pc-lan-ip>\"] [--device <serial>]"
user-invocable: true
allowed-tools: Read, Glob, Grep, Bash
model: sonnet
---

# Android Build

Runs the Android pipeline end to end and reports what actually happened.
The tool is `tools\android\run.py` (read `tools\android\README.md` for the
setup); this skill is the order to run it in and the honesty of the report.

`$ARGUMENTS` are passed to the smoke (`--no-export` reuses the last smoke
APK; `--args "..."` bakes extra client flags into the export). A serial in
`--device` becomes `UO_ANDROID_DEVICE` for the run; with two devices
attached adb needs one.

---

## 1. Doctor

```
launchers\android\doctor.bat
```

Exit 0 continues. Exit 1 stops here: report the `MISS` lines and their
`fix:` lines verbatim and do nothing else. Never install an SDK, a JDK or
templates on the user's behalf; the doctor names the step and the user runs
it.

## 2. Export

```
launchers\android\export.bat [--args "..."]
```

Skip when `--no-export` was given. On failure, read `build\android\export.log`
and quote the first `error` line. The export runs `dotnet publish -r
android-arm64` inside; a C# error shows there.

## 3. Install and smoke

```
launchers\android\smoke.bat [--no-export] [--timeout 300]
```

The smoke exports its own probe build (unless `--no-export`), installs it,
starts the app, and polls logcat for

```
[GUO] login probe: ok login gump rendered after N frames; window WxH, screen scale S, dpi scale D, gump WxH at X,Y
```

then screenshots into `build\android\smoke.png` and saves
`build\android\smoke_logcat.txt`. Exit 0 is the pass. On exit 1 read the
logcat file and quote the `FAIL`, `FATAL EXCEPTION` or last `[GUO]` line.

Read `build\android\smoke.png` with the Read tool and say what it shows:
the login gump centred in a fullscreen window with no status bar is the
expected frame.

## 4. Report

One table, no prose before it:

| Step | Command | Exit | Evidence |
|---|---|---|---|
| doctor | `launchers\android\doctor.bat` | 0 | N checks ok |
| export | `launchers\android\export.bat` | 0 | `build\android\GUO-debug.apk`, size |
| smoke | `launchers\android\smoke.bat` | 0 | the probe line, `build\android\smoke.png` |

Then, only if something failed, the quoted lines and where the log is.
Paths as clickable `file:///` URIs.

Say "verified on <device serial>" only for steps that exited 0 on the
device. A step that was skipped is "not run", not "ok".
