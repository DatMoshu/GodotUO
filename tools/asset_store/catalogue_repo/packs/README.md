# Listings

One file per pack version: `<id>/<version>.json`, with exactly these fields:

| Field | |
|---|---|
| `urls` | HTTPS URLs of the ZIP, tried in order. 1 to 16. |
| `sha256` | The ZIP's SHA-256, 64 lowercase hex digits. |
| `size` | The ZIP's size in bytes. |
| `provenance` | Optional. How the content was made, at most 500 characters. |

The folder name and file name must match the id and version in the pack's
manifest. A published listing does not change: a new version is a new file.
