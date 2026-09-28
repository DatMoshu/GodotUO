# The community server catalogue

`catalogue.json` is the **Community** group of GUO's Servers tab: the shards a
player can pick without typing an address (docs/ui/second_screen_pregame.md).
GUO builds it into the client; later it will also be served from GitHub Pages,
like the Store's catalogue.

## Rules

- Only shards that allow third-party clients such as ClassicUO or GUO.
- The owner approves every addition. A pull request that adds a shard waits
  for that approval, however complete it is.
- No scraped toplists. A shard is listed because its admin asked.

## Adding a shard

Open a pull request that adds one entry to `servers` in `catalogue.json`.
`catalogue.sample.json` shows every field filled in. The fields are documented
in `docs/data_formats.md` (section 18):

| Field | Required | |
|---|---|---|
| `name` | yes | As players know it |
| `host`, `port` | yes | The login server |
| `era`, `emulator` | no | For example `AOS`, `ModernUO` |
| `client_version` | no | Only when the shard needs one version |
| `encryption` | no | Only when it needs encryption |
| `needs_custom_data` | no | `true` when it ships its own art or maps |
| `third_party_clients` | yes | Must be `true` to be listed |
| `site` | no | An `https://` page |
| `description` | no | One or two sentences |

GUO checks each listed shard with a bare TCP connect (no login, no packets),
at most every 60 seconds while the Servers tab is open.
