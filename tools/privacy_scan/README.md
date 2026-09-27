# privacy_scan

Fails when a tracked file names one person's machine, network or devices:
a private (RFC 1918) address, a personal email, a home folder, or any value
in your local deny list. CI runs it on every push; run it yourself before
pushing, because only your machine knows your deny list.

```
python tools\privacy_scan\run.py            every tracked file
python tools\privacy_scan\run.py --staged   only what is staged
```

| File | Tracked | Holds |
|---|---|---|
| `allow.txt` | yes | regexes for lines that are real examples, not leaks |
| `deny.local.txt` | **no** (gitignored) | your own values, one per line: account names, device serials, your LAN address |

Where the values go instead: `launchers\_shared\config.local.bat`
(`UO_SHARD_HOST`, `UO_ANDROID_DEVICE`, `UO_DECK_HOST`, ...).
