# The second screen before the world: Servers and Settings

Owner request, 2026-09-28. This screen replaces the pre-game welcome panel (`DualWelcomeGump`) on the Thor's lower
screen. On a device with one screen, the same panel opens as a card from the login gump. It follows
[uo_godot_style.md](uo_godot_style.md) in full: only the client's own art, whole pixels, font 1, and quiet stone and
parchment.

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
| The shard needs its own data (a custom art pack, another client version or encryption) | "Shard B needs its own client files. Restart GUO with them?" → a restart with that shard's data folder (ADR-0021's custom data slot) and version. The folder is set up once, through the first-run folder picker |
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
| Account | Saved accounts, remember password, auto-login, the last server |
| Screen | Window size on the desktop, the UI scale (DualScreenSettings scale), screen effects look (the ADR-0023 menu, preview on top) |
| Second screen | Use it as a shelf in the world; the gumps to shelve (paperdoll, status, backpack, journal, others); shelf scale; companion tabs on or off |
| Controls | Controller on or off (ADR-0025), the glyph family (automatic / Xbox / PlayStation / Nintendo / Deck), touch controls, and a "Test controller" page showing the live pad state |
| Sound | Login music on or off, and its volume |
| About | Client and GUO version, licences, and "Report a problem" (copies a log path) |

The groups are a vertical list on the left with the fields on the right: the same two-pane shape as Servers, so the
screen feels like one object.

## Copy

- Buttons: **Play**, **Add server**, **Refresh**, **Favourite** / **Unfavourite**, **Choose folder…**, **Test
  controller**.
- Empty Community: "The server list couldn't be loaded. Your saved servers are below. Refresh to try again."
- Unreachable: "Shard B isn't answering (no reply in 3 s). It may be down, or the address is wrong."
- Sentence case, no all-caps labels, no exclamation marks.

## Open questions for the owner
1. Should the community catalogue live in this repo (`servers/catalogue.json`) and be seeded with shards that allow
   third-party clients? Who approves additions?
2. Should the dev shard appear as a built-in Favourite in dev builds only?
