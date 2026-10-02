# Servers and Accounts

Before you log in, GUO's pre-game screen has a **Servers** tab. It replaces
ClassicUO's single address field and single remembered account with a server
list and saved accounts for each server.

## The Servers tab

| Group | What it holds |
|---|---|
| **Your servers** | Servers you added by address. |
| **Favourites** | Any server you starred, from any group. |
| **Recent** | The servers you played last. |
| **Community** | The catalogue built into the client: shards whose admins asked to be listed and that allow third-party clients. |

- **Open a server's card** to see its address, era, emulator and status, and the
  accounts you saved for it. Then press **Play**.
- **Live status:** while the tab is open, GUO checks each Community shard at
  most once a minute. The check is a plain TCP connect: no login, no packets.
- **Shards with their own art or maps** are marked `needs_custom_data`. They
  need that shard's client files. GUO can point a server at its own client
  files (see [Configuration](Configuration.md)).

### Get a shard listed

Shard admins can ask for a place in the Community group by opening a pull
request that adds one entry to `servers/catalogue.json`:
- Only shards that allow third-party clients such as ClassicUO or GUO are
  listed. Scraped toplists aren't used.
- The project owner approves every addition.
- `servers/catalogue.sample.json` shows every field filled in, and
  `servers/README.md` lists which fields are required.

## Saved accounts and passwords

GUO keeps the accounts you save for each server in `servers.json`, in the
client's user folder. The passwords themselves go to the operating system's
own keystore, and the file records only which store holds each one.

| Platform | Where the password is kept |
|---|---|
| Windows | DPAPI, tied to your Windows user. `servers.json` holds only ciphertext that no other user or machine can decrypt. |
| Android | The Android Keystore. |
| Linux, Steam Deck | The Secret Service keyring (libsecret). |
| No keystore available | GUO doesn't keep the password; you type it each time. |

Upstream ClassicUO keeps one password in `settings.json`, hidden behind a
simple XOR mask. GUO's classic login screen still has upstream's **Save account**
box, and a password typed there is still stored the upstream way. Save it on the
Servers tab instead to keep it in the keystore.

- **Forget an account:** **Forget** on its row. A keyring entry is deleted; a
  ciphertext entry is removed from the file.
- **Don't keep the password:** untick **Save password** when you add the
  account. Play then fills in the name, and you type the password on the login
  screen.

## Dev logins (development builds only)

A **debug** build can mark accounts on the local dev shard (see
[Dev Shard](Dev-Shard.md)) as **dev accounts**. Tick "Dev account" when you
add the account on the dev shard's page (the box reads "Dev account (one-click login)").

- One click then logs in as that account and enters with its character, or
  shows the character list when it has several.
- From the world, the same click logs out first, so switching between an
  admin account and a player account is one action.
- **Ctrl+Shift+D** switches to the next dev account, from the world or the login screen.
- Release builds have no dev shard, so they have no dev logins. GUO never
  creates an account; dev accounts are your own.

## Related

- [Configuration](Configuration.md): `UO_SHARD_HOST`, `UO_SHARD_PORT` and the
  client-data settings.
- [Dev Shard](Dev-Shard.md): run a local ModernUO to play against.
- The design: `docs/ui/second_screen_pregame.md` and `docs/data_formats.md`
  section 18 (the catalogue fields).
