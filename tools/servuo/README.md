# ServUO — a private shard for the ServUO world-objects backend

The editor's world objects (ADR-0014) export per server. This folder runs a
**private** [ServUO](https://github.com/ServUO/ServUO) shard, so the ServUO
backend can be proved against the real thing: XmlSpawner XML, decoration cfg,
and the GM-command apply.

It listens on **127.0.0.1:2596** only. The private ModernUO instance is 2594,
its editor bridge 2595, and the shared dev shard 2593, which is never touched.

| | |
|---|---|
| **Upstream** | <https://github.com/ServUO/ServUO.git>, branch `pub57` |
| **Pinned at** | `d76bf4443cf76d081ddaf8f57c87ff33749256af` ("Fix packet leak", 2026-08-04), fetched 2026-09-27, shallow |
| **Licence** | GPL-3.0. **Not vendored, not redistributed, not patched.** Nothing here is copied from it or derived from its code. |
| **Runtime** | .NET Framework 4.8 (built into Windows 10/11). It builds with the .NET SDK (`dotnet build`, SDK-style `net48` projects). |
| **Checkout** | `tools/servuo/src/`, **gitignored**, about 74 MB (130 MB built). |

## Use it

```
launchers\servuo\fetch.bat       shallow-fetch the pinned commit   (once)
launchers\servuo\build.bat       dotnet build, Release              (once, seconds)
launchers\servuo\configure.bat   write our settings into src\Config (after fetch)
launchers\servuo\start.bat       run it in the background
launchers\servuo\status.bat
launchers\servuo\stop.bat
```

Each calls `python tools\servuo\run.py <command>`. Measured 2026-09-27: the
fetch took seconds, the build 7 s, and the first boot a few seconds (world
empty, 6017 item and 1361 mobile types verified).

**First boot.** ServUO asks on its console whether to create an owner account.
`start` answers it from the configuration (`UO_SHARD_OWNER`, and the password
from `UO_SHARD_OWNER_PASSWORD`), so the scripted GM client can log in. The
password is never written anywhere by this tool. Auto account creation stays
on, as on the ModernUO dev shard.

## What is tracked here, and why

| Path | What |
|---|---|
| `run.py` | fetch, build, configure, start, stop, status |
| `config/*.cfg` | **only the key=value lines we set**: name, listener 127.0.0.1:2596, `CustomPath` = `UO_CLIENT_DATA`, accounts per IP. `configure` writes them over ServUO's own files line by line; those files are not copied here |
| `README.md` | this |

The ServUO **adapter** (XmlSpawner XML and decoration cfg from the neutral
model) lives with the other backends in `tools/world/backends/servuo.py`.
It is our code, written from the file formats.

To move the pin: change `PIN` in `run.py` and the table above, delete `src/`,
and fetch and build again.
