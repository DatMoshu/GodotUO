# The second screen before the world: Servers and Settings

Owner request, 2026-09-28. This screen replaces the pre-game welcome panel (`DualWelcomeGump`) on the Thor's lower
screen. On a device with one screen, the same panel opens as a card from the login gump. It follows
[uo_godot_style.md](uo_godot_style.md) in full: only the client's own art, whole pixels, font 1, and quiet stone and
parchment.

Where a one-screen window has room beside the login gump, the card is docked on its left instead of opening as a
card: see [one_screen_panel.md](one_screen_panel.md).

## What it is for

Before login, the player has two questions: *where do I play* and *how is the client set up*. The top screen keeps
the classic login gump exactly as ClassicUO draws it. The lower screen answers both questions, and it never asks the
player to type an IP.

## Layout (the Thor's lower screen, 1240×1080, 3× → 413×360 art px)

The stone card fills the screen. Two tabs sit at the top, drawn as the client's own gump tabs: the same notched tab
art the paperdoll and book gumps use, not a Godot tab bar.

```
┌─ stone ────────────────────────────────────────────────┐
│ [ Servers ][ Settings ]                  GUO 0.1  ◈     │  tabs: selected = Heading caption, raised
│ ┌─ parchment ────────────────┐ ┌─ parchment ─────────┐ │
│ │ ★ My dev shard     12 ms ● │ │ Moshu Dev           │ │  left: the list; right: the chosen shard
│ │ ★ Friends' shard   48 ms ● │ │ ModernUO, AOS rules │ │
│ │ ─ Recent ─────────────────│ │ 3 online, 12 ms     │ │
│ │   Some shard       91 ms ● │ │ GUO works here ✓    │ │
│ │ ─ Community ──────────────│ │ Needs: client 7.0.x │ │
│ │   Shard A   AOS    60 ms ● │ │ your UO folder      │ │
│ │   Shard B   T2A     —   ○ │ │                     │ │
│ └────────────────────────────┘ │   [  ► Play  ]      │ │  the login gump's own arrow button
│ [ Add server ]  [ Refresh ]    │ [★ Favourite][Site] │ │
└────────────────────────────────┴─────────────────────┘─┘
```

- The list is left-aligned rows on parchment in three groups: **Favourites** (★), **Recent**, and **Community** (the
  catalogue). A group rule is one art pixel of `#5c554a` with the group name in Muted.
- A row shows the name (Ink), the era/ruleset (Muted), the ping, and a status dot. The dot is gold when the shard is
  reachable, hollow when it's down, and red when GUO knows it can't play there.
- A tap selects a row. The detail pane shows the shard's description, requirements and the actions. A double tap, or A
  on a pad, plays.
- **The one memorable element: Play is the client's own login arrow button,** the gold ► from the classic login gump,
  in the detail pane. Nothing else on the screen is that bright.
- On the Odin, a phone or the desktop, the same card opens full-screen from a "Servers" button beside the login gump,
  and closes back to it.

## Play: what happens when you tap it

| Case | Behaviour |
|---|---|
| Before login, and the shard's client version and encryption match the running client | Set `Settings.GlobalSettings.IP/Port` in memory (and save it to settings.json), then reconnect. The top screen's login gump shows the shard's name and keeps the account fields |
| In the world | "Log out and play on Shard B?" → logout → the same as above |
| The shard needs its own data (a custom art pack, another client version or encryption) | The page says so. The first Play opens the first-run screen for its folder (a whole client, or a folder with a `guo_data.json`), kept as the entry's `data_folder`. Then "Restart GUO with Shard B's files?" → a restart with that folder (ADR-0021's custom data slot, or the install for a whole client), its version, encryption and address (`shard_session.json`, data_formats.md section 19). The list then says "GUO is running with Shard B's files" with **Your own files** to go back; Play on any other server asks to restart with the player's own files first. "Change files" picks the folder again |
| The shard lists `third_party_clients: no` | The row's dot is red. Play is disabled, with the reason: "This shard only allows its own client." The shard's site link stays available |

## Where the server list comes from

There's no public API for UO shard lists. The toplists (UOGateway, gtop100, uogm.app) are websites without a
documented API, and scraping them is off-limits. GUO keeps its own list:

1. **Community catalogue:** `servers/catalogue.json` in the repo (later on GitHub Pages, like the Store's catalogue,
   E4). Shard admins add their shard by pull request with a manifest:
   `name, host, port, era, emulator, client_version, encryption, needs_custom_data, third_party_clients, site,
   description`. GUO reads it, and caches the last good copy for offline.
2. **The player's own servers:** "Add server" asks for a name, a host and a port. These are kept in settings.json, and
   nothing leaves the device.
3. **Recent:** the last 5 shards logged in to.
4. **Live status:** GUO times a TCP connect to each visible row (at most 8 at once, every 60 s while the tab is open).
   There's no login and no packets. A player count appears only when the manifest supplies a status URL; nothing else
   guesses one.

## Settings tab

Only what exists before login: the global settings (settings.json) and the second-screen settings. Everything that
belongs to a character's profile says "Set in Options once you're in the world", with no disabled clutter.

| Group | Settings |
|---|---|
| Your UO files | The UO folder (live check: found / version / missing files), client version, a "Choose folder…" picker (G2), clearing the cache |
| Account | Saved accounts per server (see Accounts below), auto-login, the last server |
| Screen | Window size on the desktop, the UI scale (DualScreenSettings scale), screen effects look (the ADR-0023 menu, preview on top) |
| Second screen | Use it as a shelf in the world; the gumps to shelve (paperdoll, status, backpack, journal, others); shelf scale; companion tabs on or off |
| Controls | Controller on or off (ADR-0025), the glyph family (automatic / Xbox / PlayStation / Nintendo / Deck), touch controls, and a "Test controller" page showing the live pad state |
| Sound | Login music on or off, and its volume |
| About | Client and GUO version, licences, and "Report a problem" (copies a log path) |

The groups are a vertical list on the left with the fields on the right: the same two-pane shape as Servers, so the
screen feels like one object.

## Accounts (design; the keystore part waits for the director)

The player keeps accounts per server, and logs in from the list without typing. A saved password is encrypted by the
operating system's own keystore. Upstream's `Crypter` (an XOR mask, not encryption) is never used for it.

### What the player sees

- **Servers tab, a server's page:** an Accounts block under Play. It lists the saved accounts by name, each with
  **Forget**, and has **Add account**, which opens a name field, a password field and a **Save password** box (on by
  default where a keystore exists). With no keystore, the box is gone and the line reads "Passwords aren't saved on
  this system. You'll type it at login."
- **Play with an account picked:** GUO fills the login gump's account and password fields and presses its arrow, as if
  the player had typed them. With one account, it is picked already. With none, Play works as today.
- **The login screen (the second screen, and one-screen touch):** a quick-login row above the classic gump: the
  server, its accounts as buttons, and Login. The classic gump stays as it is under it.
- **Settings > Account:** the same accounts, grouped by server, with Forget and "Forget all".
- A password that can no longer be decrypted (a new Windows profile, a reset phone, a locked keyring) doesn't fail
  silently: "Couldn't read the saved password for moshu. Type it once and it's saved again."

### The classic checkbox

The desktop login gump keeps its **Save account** and **Auto login** boxes working 1:1: they still write `username`
and `password` (through `Crypter`) to settings.json, exactly as upstream. The manager never reads or writes those
fields, and never ticks or unticks the boxes.

One open point for the director: when a quick login fills the gump while the classic box is ticked, upstream's
`LoginScene.Connect` also saves that password to settings.json through `Crypter`, next to the keystore copy. The
recommendation is a marked `// PORT DEVIATION (GUO):` that skips that write for a login the manager started (a
typed login is unchanged). The alternative is to leave it, and say so under Save password.

### Where it is kept

`servers.json` (data_formats.md section 17) gains, per server entry:

```json
"accounts": [
  { "name": "moshu", "secret": { "store": "dpapi", "blob": "AQAAANCMnd8BFdERjHoAwE..." }, "last_used": "2026-09-28T19:00:00Z" }
]
```

| `store` | Platform | What `secret` holds |
|---|---|---|
| `dpapi` | Windows | `blob`: `CryptProtectData` output, base64. CurrentUser scope, with GUO's entropy plus `host:port:name`, so a blob copied onto another entry doesn't decrypt |
| `android-keystore` | Android | `iv` and `blob`: AES-256-GCM, with a non-exportable key (`guo.accounts.v1`) in AndroidKeyStore. `host:port:name` is the associated data |
| `libsecret` | Linux, Steam Deck | Nothing. The password lives in the Secret Service keyring (schema `org.guo.Account`, attributes host, port, name); the file only says it's there |
| `none` | Web, or a Linux without a keyring | Nothing. The password is never kept |

No plaintext password is ever written to a file, a log, a probe screenshot or a Discord caption. The account name
is not secret (upstream saves it in the clear too), and it is kept in the clear.

### How each store is reached

- **Windows:** P/Invoke of `crypt32.dll` `CryptProtectData` / `CryptUnprotectData`. No NuGet package.
- **Android:** the JNI route that `SafFolder` uses (`JavaClassWrapper`, and the activity from the `AndroidRuntime`
  singleton): `KeyGenParameterSpec.Builder`, `KeyGenerator`, `KeyStore` and `Cipher`. If `JavaClassWrapper` can't
  pass one of those calls (varargs, byte arrays), the fallback is a small Android plugin with the same four calls.
  That adds a plugin to the export, so it would go to the director first.
- **Linux / Steam Deck:** P/Invoke of `libsecret-1.so.0` (`secret_password_store_sync`, `_lookup_sync`, `_clear_sync`).
  If the library or the Secret Service is missing, the store is `none` and the UI says so. There's no plaintext
  fallback. In the Deck's Game Mode, the keyring may be locked; a lookup that fails asks for the password once.
- **Web:** `none`, always. The Save password box isn't shown.

### What it protects against, and what it doesn't

It protects the password in a copied, synced or shared settings folder, in a bug report, and against another user
account on the same machine. It does not protect against malware running as the same user, or on a rooted phone:
DPAPI and an unlocked keyring hand the password to any process of that user. The Settings > Account page says this in
one line.

### Code shape

- `ISecretStore` with `Kind`, `Available`, `Protect(entry, name, password)` and `Unprotect(entry, name)`, returning
  a secret or null with a reason. There is one implementation per platform, chosen at start.
- `AccountBook` sits on `ServerBook` (the same file and the same save), with `Add`, `Forget`, `ForgetAll`, `Password`
  and `Touch` (last used).
- All of it is new GUO code under `src/Input/Touch/Pregame/Accounts/`. The only touch to a ported file is the
  Connect deviation above, if it is approved.

### The probe

It uses a fake account (`guoprobe` with a random password made per run), never a real one, and doesn't print the
password. It checks that:

- the account is saved, and that servers.json contains neither the password nor its Crypter form;
- a round trip through the store returns the password;
- a blob moved to another entry doesn't decrypt;
- Forget removes it (on Linux, from the keyring too);
- quick login fills the gump and reaches the shard as the `guoeffects` account;
- the classic box behaves as upstream on the desktop.

On Android it runs on the Thor once the store is built.

## Copy

- Buttons: **Play**, **Add server**, **Refresh**, **Favourite** / **Unfavourite**, **Choose folder…**, **Test
  controller**.
- Empty Community: "The server list couldn't be loaded. Your saved servers are above. Refresh to try again."
- Unreachable: "Shard B isn't answering (no reply in 3 s). It may be down, or the address is wrong."
- Sentence case, no all-caps labels, no exclamation marks.

## Owner decisions (2026-09-28)
1. The community catalogue lives in this repo (`servers/catalogue.json`), seeded with shards that allow third-party
   clients. The owner approves every addition.
2. Dev builds carry a built-in dev shard Favourite from a dev-only setting; release builds never include it.
