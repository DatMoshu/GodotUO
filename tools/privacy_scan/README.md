# privacy_scan

Fails when a tracked file names one person's machine, network or devices:
a private (RFC 1918) address, a personal email, a home folder, or any value
in your local deny list. CI runs it on every push; run it yourself before
pushing, because only your machine knows your deny list.

```
python tools\privacy_scan\run.py            every tracked file
python tools\privacy_scan\run.py --staged   only what is staged, as staged
python tools\privacy_scan\test_privacy_scan.py   the scanner's own tests
```

`--staged` reads each staged file from the index, so it checks what the commit will hold: a leak that is
staged but already cleaned in the working copy is caught, and a leak only in the working copy does not
block the commit. An `allow.txt` regex excuses the one line it matches, never the rest of the file, and
never a hit in a binary.

| File | Tracked | Holds |
|---|---|---|
| `allow.txt` | yes | regexes for lines that are real examples, not leaks |
| `deny.local.txt` | **no** (gitignored) | your own values, one per line: account names, device serials, your LAN address |

Where the values go instead: `launchers\_shared\config.local.bat`
(`UO_SHARD_HOST`, `UO_ANDROID_DEVICE`, `UO_DECK_HOST`, ...).

## Binaries

Compiled programs (.dll, .exe, .so, .dylib, .pdb, .lib, .node, .pyd) are a hit whenever they are
tracked at all: a release build embeds the builder's home folder, which is how `gdcef.dll` reached GitHub
on 2026-10-05. Text in UTF-16 with a byte order mark is decoded and scanned line by line, like UTF-8.
Other files that are not text are scanned as bytes for a home folder and for the deny list, in ASCII and in UTF-16LE (how Windows programs store paths). Images, audio, fonts and models
are still skipped.
