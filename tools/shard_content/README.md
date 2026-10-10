# tools/shard_content

Deploys content packs to a supported shard and publishes what its players need
(ADR-0026 section 4). `deploy` installs a pack from named catalogues with the
client's own installer, writes the deployment lock, the neutral export the GUO
bridge loads (`Data/GUO/server-content.json`) and the public descriptor
(`Data/GUO/public/shard-content.json`). `check`, `status`, `rollback` and
`serve` do what they say; `prove` (`prove.py`) runs the whole path end to end
on the private ModernUO with a throwaway signed catalogue.

## Run

```
python tools/shard_content/run.py deploy --name NAME --catalogue URL[=KEY] --pack ID --version V --bind REF=TYPE:ID
python tools/shard_content/run.py check [DESCRIPTOR]
python tools/shard_content/run.py status [--shard-dir DIR] [--json]
python tools/shard_content/run.py rollback [--shard-dir DIR]
python tools/shard_content/run.py serve [--host 127.0.0.1] [--port 18870]
python tools/shard_content/run.py prove [--out DIR] [--min-free-gb 16]
```

## Tests

`python tools/shard_content/test_shard_content.py` (unittest): deploy writes the export and descriptor, and a client installs exactly what the descriptor names. `prove` is the live check.
