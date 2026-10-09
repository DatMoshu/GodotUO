# Server compatibility lab: backends and pins

The server lab runs GUO against other UO server emulators, case by case, so GUO works with any server and not only
ModernUO. This page records which version of each server the lab uses and what each one needs. The same facts are in
`tools/server_manager/backends.json`, in each row's `lab` object (fields in `docs/data_formats.md` section 30); the JSON
is what the tools read, and this page explains it.

Desk-checked 2026-10-09 (SV0) against each upstream repository at its pin. Nothing has been fetched into the project,
built or run for this page. No server is vendored: SV1 and later fetch each one into a gitignored `src/` at its pin, as
ModernUO and ServUO are today, and GUO commits only patches, configuration templates and the lab tooling. Nothing goes
upstream to any server project until Moshu has reviewed it.

## Pins

| Row | Backend | Repository and ref | Commit | Date | Licence |
|---|---|---|---|---|---|
| 1 | ModernUO | modernuo/ModernUO, `main` | `d4531cd94b739613155225c234900de9f47d2c88` | 2026-09-30 | GPL-3.0 |
| 2 | ServUO | ServUO/ServUO, `pub57` | `d76bf4443cf76d081ddaf8f57c87ff33749256af` | 2026-08-04 | GPL-2.0 |
| 3 | UOX3 | UOX3DevTeam/UOX3, tag `v0.99.7a-release` | `a8a4aefeb0d0d5e5fbc8192af8bd5321db4971e0` | 2025-07-30 | GPL-2.0 or later |
| 4 | Sphere X (core) | Sphereserver/Source-X, `master` | `dd28a0ad53258adb55b548b1bd4df989065a1fe4` | 2026-09-18 | Apache-2.0 |
| 4 | Sphere X (scripts) | Sphereserver/Scripts-X, `main` | `27e78bc896da239d3738fe02a6d6bf8e9045c16d` | 2026-03-27 | Apache-2.0 |

- **ModernUO** is the reference row: the pin is `UO_SHARD_REF` in `launchers/_shared/config.bat`, with our patches in
  `tools/modernuo/patches`.
- **ServUO** is the pin `tools/servuo` and the server adapters already use. Its licence is GPL-2.0 (the repo's
  LICENSE); `tools/servuo/README.md` said GPL-3.0 and is corrected.
- **UOX3** is pinned to the release the adapter probe ran. A newer release, `v0.99.7d` (`a1fee724`, 2026-08-13, on
  branch `develop`), exists; the lab moves to it only together with a new adapter probe.
- **Sphere X** had no recorded commit: the adapter probe ran a nightly build, and the repo's `Nightly` tag dates from
  2022. The pins above are the heads of the stable branches on 2026-10-09. Scripts-X dropped its own licence
  boilerplate in that commit; its LICENSE file is Apache-2.0, as Source-X's.
- RunUO and POL are later rows and have no `lab` entry yet.

## What each server needs

| Backend | Toolchain | Client it accepts | Era (setting, lab value) | Headless admin | Loopback only |
|---|---|---|---|---|---|
| ModernUO | .NET SDK 10.0.201 (`global.json`, roll forward to latest major), `net10.0` | 7.0.61.0 or newer (`RequiredClient` in `expansion.json`), unencrypted | `expansion.json`; Endless Journey (Id 11) from `tools/modernuo/config` | MUO patch `0001-headless-owner-account`: owner from `UO_SHARD_OWNER` / `UO_SHARD_OWNER_PASSWORD` at every boot | Yes, `modernuo.json` listener from `UO_SHARD_BIND` |
| ServUO | .NET SDK building SDK-style `net48` projects; runs on .NET Framework 4.8, Windows only | 7.0.x classic; the required version is read from the client.exe in the data folder, older clients are kicked after a delay | `Config/Expansion.cfg` `CurrentExpansion`; EJ (upstream default) | First boot asks on stdin for an owner; `tools/servuo` `start` answers from the same two settings | Yes, `Config/Server.cfg` `Listen=` |
| UOX3 | Visual Studio 2022 (MSVC v143, `make/VS2022/uox3.sln`), C++17; CMake on Linux and macOS; SpiderMonkey and zlib are in the repo | About 4.0.0 to 7.0.109.0, unencrypted only; `uox.ini` `CLIENTSUPPORT70610=1` is the only band on by default | `uox.ini` `CORESHARDERA`; `lbr` (upstream default) | Account file: `data/accounts/accounts.adm` ships `admin`/`admin` with the GM flag; the lab writes its own with a generated password before first boot | **No**: the listener binds every interface (`INADDR_ANY`); `EXTERNALIP` only sets the advertised address |
| Sphere X | CMake 3.29 or newer, a C++20 compiler (Visual Studio 2022), the MariaDB client library (shipped in `lib/bin` for Windows) | Any supported client while `sphere.ini` `ClientVersion` stays commented out; unencrypted clients are refused until `UseNoCrypt=1` | `sphere.ini` `Feature*` flags and `EraLimit*`; upstream default (T2A features on, AOS and later off, era limits at ToL) | Account file: `accounts/sphereacct.scp`, a `[name]` section with `PLEVEL=7` and `PASSWORD`; console alternative `ACCOUNT <name> PLEVEL 7` | Yes, `sphere.ini` `ServIP` is the bound address; the experimental UDP ping server stays off |

GUO connects as client `UO_CLIENT_VERSION` (7.0.107.76 by default) without encryption, which every row accepts once
configured as above.

## Consequences for the later stories

- **Admin passwords.** UOX3 and Sphere X ship an `admin` account with the password `admin`. The lab never starts either
  with the shipped file: SV5 and SV6 write the account file with a generated password in the local secrets file (the
  SF1 pattern) before the first boot.
- **UOX3 binding.** UOX3 cannot be told to listen on 127.0.0.1 alone. SV5 adds a local patch in
  `tools/uox3/patches` (bind to `EXTERNALIP`) or a Windows firewall rule that blocks its port from outside, and the lab
  refuses to start UOX3 until one is in place. Either is recorded per backend, like MU1.
- **Sphere X encryption.** The lab's `sphere.ini` sets `UseNoCrypt=1`; without it GUO cannot log in at all.
- **Eras differ.** Rows 3 and 4 run at their upstream default eras, so later-era cases (property tooltips on Sphere X,
  for one) can be `n/a` by era rather than failures; the grid records each row's era next to its pin.
- **Toolchains.** Rows 3 and 4 need Visual Studio 2022 with the C++ workload, and Sphere X also CMake 3.29 or newer.
  SV5 and SV6 check for them in a `doctor` step before any fetch.
