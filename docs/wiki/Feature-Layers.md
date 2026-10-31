# Feature Layers

Everything in GUO belongs to one of five layers. The layer decides what a
change must prove before it is merged, whether it is on by default, and
whether it ships in the game you download. The owner approved the layers on
2026-10-13; the binding text is the layer table in `AGENTS.md` and
`CONTRIBUTING.md`.

| Layer | What it holds | On by default? | In an exported game? |
|---|---|---|---|
| **L0 Core port** | ClassicUO's own code, carried across | Yes | Yes |
| **L1 Client options** | GUO features a player switches on in Options | No | Yes, switched off |
| **L2 Packs** | Art, HD art, splats, screen effects, sounds, voices, zones, themes | Only what you install | Never bundled; installed from the Store |
| **L3 Editor packages** | The GodotUO editor: authoring, generation, AI, MCP, tours | Editor only | Never |
| **L4 Labs** | Experiments not ready to be an option | No, needs an opt-in build | Never |

## What each layer has to prove

- **L0** is held to parity: it must behave exactly as ClassicUO does. Any
  deliberate difference carries a `PORT DEVIATION (GUO)` marker, and
  `docs/upstream/GUO_DIVERGENCES.md` lists them. See
  [Parity and Drift](Parity-and-Drift.md).
- **L1** options are off by default and need a profile field, an Options
  entry, proof that with the option off the game draws exactly as before, a
  scripted scenario and a section in `docs/data_formats.md`. Anything that
  changes how the world is drawn or how input works also needs an ADR. An
  option may not start another program, run Python or call a network
  service. **Enhanced HD** and **Regional audio and recorded NPC voices**
  are L1 options.
- **L2** packs are typed content only: no code, and nothing runs when one is
  installed. See [Content Packs](Content-Packs.md).
- **L3** editor code is compiled only into the editor (`#if TOOLS`), and
  every AI feature also sits behind the editor's AI switch (Editor Settings >
  GUO > AI > Enabled). With it off, AI menus are hidden and no AI work runs;
  manual authoring always stays. See [Editor](Editor.md).
- **L4** Labs live in `godot/GUO/src/Labs/<feature>/` and are compiled only
  into a Debug build made with `-p:GuoLabs=true`. Release and export builds
  leave them out even when the flag is set. Each Lab needs a README, a
  doctor launcher that says what to install, a scenario and a smoke line.
  The source now includes Weather, WaterRain and GPU renderer Labs. Their presence is not a release guarantee; they remain excluded from exported clients. See the [Labs source](../../godot/GUO/src/Labs/).

## Rules every change follows

1. A new chat command, profile field, Options entry, shader or edit to
   ported code starts with a short proposal or ADR that the owner decides.
   The first question: would ClassicUO ship it? If not, it belongs in a pack,
   the editor or Labs.
2. One feature per pull request, about 1,500 lines or 20 files at most
   unless agreed first.
3. Generating content belongs in the editor or Labs; the game only reads
   packs.
4. Anything that needs an outside program has a doctor launcher, and its
   failure text names that launcher.
5. Every L1 and L4 feature has a scenario under `tools/scenarios/` and a
   line in the smoke.
6. Content that can be a pack goes to the Store.
7. Contributors and agents fill in the same review template: layer, toggle,
   parity proof, scenario, doctor and docs section.

## Building with Labs

```
dotnet build godot\GUO\GUO.csproj -c Debug -p:GuoLabs=true
```

`godot/GUO/src/Labs/README.md` explains how a Lab registers its chat commands
and Options rows.

## See also

- [Content Packs](Content-Packs.md): what an L2 pack may carry
- [Contributing](Contributing.md): how to send a change
- [Architecture](Architecture.md): how the port is put together
