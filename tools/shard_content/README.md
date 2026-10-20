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
# Login pack offers (STORE-02)

After deploying a descriptor, write an opt-in login offer for the ModernUO
GUO bridge:

```text
python tools/shard_content/run.py offer --name "Example Shard" --shard-dir SHARD
```

This validates `Data/GUO/public/shard-content.json` with the client's parser,
then writes `Data/GUO/shard-offer.json`. Rebuild/install the bridge using
`python tools/editor_shard/run.py bridge` for the editor's private instance
and restart that instance. The bridge sends the offer two seconds after login.
No file means no advertisement. Other server backends do not send this packet.
Deployment and rollback do not rewrite the opt-in offer: rerun `offer` after
either operation, or remove the offer file to stop advertising packs.

`--descriptor FILE` selects another deployed descriptor. `--feature NAME=FILE`
adds a bounded JSON object for a feature's consumer; `--features-only` omits
the pack request. Advertising a feature block alone activates nothing.

The client asks before fetching any catalogue or pack. Its prompt names the
pack IDs, versions, catalogue hosts and signing-key fingerprints. **Not now**
persists in `<store>/.shard-offers.json`; a changed content identity asks again.
**Install and restart** uses the existing descriptor verification and shard
session restart. It skips restarting if the player changes connections during
the download. `--shard-offer-answer yes|no` is available to scripted runs.

Checks: `python -m pytest tools/shard_content`, the synthetic scene
`res://src/Store/StoreShardOfferProbe.tscn` (also in smoke), and scenario
`client.store.shard_offer_contract`. These prove the contract and decisions;
they do not prove a live login or the visual prompt.

Live acceptance procedure (requires a private ModernUO instance with the bridge):
deploy a synthetic licensed pack, write the offer, restart that instance, and
log in from a scratch Store. Confirm **Not now** fetches nothing and suppresses
the same offer after reconnect. Change the deployed pack, regenerate the offer,
then confirm **Install and restart** verifies and mounts that deployment.
Reconnect once more and verify the already-mounted offer asks nothing. Repeat
with the offer file absent and with invalid JSON: login must still work.
