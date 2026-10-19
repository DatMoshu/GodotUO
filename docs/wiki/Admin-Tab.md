# Admin Tab

The GodotUO editor's **Admin** tab runs a ModernUO server on your own
computer: its health, accounts, settings, backups and commands, and a live
map of who is where. It is part of the editor (layer L3, see
[Feature Layers](Feature-Layers.md)) and never ships in the game.

It talks to the server through the editor bridge's admin channel
(ADR-0035). Every admin action needs the server's admin token, which GUO
generates for you into your workspace and never shows or prints. Every
action is written to an audit log on the server, with passwords and tokens
masked. Map editing works without the token.

To start: open the editor (`launchers\editor\open_project.bat`), start your
server from the run bar, and open the Admin tab.
[Manage Your Shard From the Editor](Manage-Your-Shard-From-The-Editor.md)
covers setting up the world project and the shard.

## The panels

| Panel | What it does |
|---|---|
| **Health** | Uptime, players online, items, mobiles, memory, last save and last backup, in numbers and plain words. **Save now**, and **Restart**, which saves first. |
| **Accounts** | Every account with its access level, last login and characters. Make one, change its level, give it a new password (generated and shown once, or typed twice), ban it or lift the ban. Passwords fit the login screen's 16-character box. |
| **Settings** | A form over the server's configuration with plain labels, checks on every value and a list of what you changed. **Save and restart** keeps the previous files. Passwords and webhooks stay in your own secrets file and are never shown. |
| **Backups** | **Back up now** saves the world and keeps a copy; the oldest go once there are more than you keep (10 by default). **Restore** asks first, keeps a copy of the world as it is, and puts it back if anything goes wrong. |
| **Commands** | Every command your server has, with its level, usage and help. Run one with nobody logged in; name a staff character in **Run as** for commands that target in game. Shutting down, wiping, deleting accounts and other sweeping commands ask you to type the command's word first. |
| **God view** | Every player, NPC and spawner on a facet over its radar map, kept live. Filter, **Find** by name or serial, and act: **Go there**, **Bring here**, **Open paperdoll**, **Follow**, and **Respawn** or **Clear** for a spawner. With no staff character online, the server's hidden presence acts instead (a white cross on the map). Needs GameMaster level. |
| **Audit** | The server's audit log, newest first, filtered by action, by who or by text, and exported as CSV. It also lists changes to the editor's automation permissions. |

Every time in the tab is UTC, written with a Z.

## Automation permissions

The editor can be driven by an automation client over MCP (see
`tools/editor_mcp/README.md`). What it may do is set per user in **Editor
Settings > GUO > MCP Access**: reading state, moving around and connecting
start on; saving, backups, restarts, settings, accounts, commands and god
view actions start off. Every call is checked against those settings, every
change to them goes in the audit log, and a damaged settings file denies
all access without touching your own manual tools. The editor's AI switch
(Editor Settings > GUO > AI > Enabled) sits above all of it: off hides these
settings and stops every MCP call.

## Limits

- The tab manages a server on this computer. A hosted shard is installed
  and run with `tools/muo_shard` instead.
- Windowed screenshots of the editor's header have not been taken since the
  toolbar was rebuilt; its look is **written, not verified**.

## Checking it

```
python tools\editor_shard\run.py admin-check
python tools\editor_shard\run.py admin-tab
```

The first talks to a running instance's bridge; the second drives the tab
itself against a private instance. `tools/editor_shard/README.md` lists what
each one checks.
