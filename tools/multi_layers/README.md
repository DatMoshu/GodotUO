# Layered multi document checks

This offline console harness links the editor's actual document and layer codec.
It checks revision guards, locks, atomic edits, undo, native export membership,
save/reopen, malformed metadata and exhausted layer identifiers. It needs the
.NET 8 SDK; it does not start Godot, a shard, or an AI provider.

Run from the checkout root, with build output outside the checkout:

```text
dotnet run --project tools/multi_layers/MultiLayers.Tests.csproj --artifacts-path <external-output-folder>
```

The optional `python tools/multi_layers/tavern.py` builds the layered tavern
fixture from `tools/multi/examples/layered_tavern.json`, the configured client
installation and an existing `Config.build/multi/catalogue`. Generated fixture
files use `Config.build`; they contain client-derived content and stay local.

See [layered multi editing](../../docs/layered_multi_editor.md) for authoring,
metadata and native export semantics. Full editor reload remains a separate
required check; passing this harness alone does not prove editor acceptance.
