# Community and Support

## Where to go

| You want to | Go to |
|---|---|
| Ask how to do something | Discussions → **Q&A** |
| Share a screenshot, video, build or shard | Discussions → **Show and tell** |
| Suggest a feature or a direction | Discussions → **Ideas** first; it becomes an issue once it's agreed |
| Report a bug | Issues → **Bug report** |
| Report a security problem | **Privately**: the repository's Security tab, "Report a vulnerability" (see `SECURITY.md`) |
| List your shard in the client | A pull request to `servers/catalogue.json` ([Servers and Accounts](Servers-and-Accounts.md)) |
| Contribute code or docs | [Contributing](Contributing.md) |

## Good bug reports

- **Platform and build:** Windows, Android (device), Steam Deck or web;
  downloaded or self-built, and from which commit.
- **Client version** (`UO_CLIENT_VERSION`) and the **shard emulator**.
- **Steps to reproduce**, what you expected, and what happened.
- **Whether ClassicUO does the same.** If it does, it may be an upstream issue.
- **The client log.** Never attach UO client data. Screenshots of the game are
  fine in an issue, but they aren't committed to the repository.

## Labels

| Label | Means |
|---|---|
| `good first issue` | Small, well-scoped, a good way in |
| `help wanted` | Open for anyone to pick up |
| `parity` | GUO differs from ClassicUO |
| `area: render`, `area: input`, `area: network`, `area: data`, `area: editor`, `area: tools`, `area: ui` | The subsystem |
| `platform: android`, `platform: steamdeck`, `platform: web`, `platform: windows` | Only on that platform |
| `upstream` | Also happens in ClassicUO |
| `needs repro` | Can't act on it until someone can reproduce it |

## Code of conduct

Everyone in the issues, discussions and pull requests follows
`CODE_OF_CONDUCT.md`.

## Not affiliated

Ultima Online is a registered trademark of Electronic Arts Inc. GodotUO is an
unofficial fan project, not affiliated with or endorsed by Electronic Arts or
Broadsword Online Games. Bring your own client data.
