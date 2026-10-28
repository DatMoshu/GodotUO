# Roadmap and evidence gaps

This page is direction, not a schedule or permission to change defaults.
The audit baseline is `main` `fc9e3588` (2026-10-28). Binding technical decisions
live in [architecture records](../architecture/README.md); current work and
release status live in [GitHub Issues](https://github.com/DatMoshu/GodotUO/issues)
and the repository's actual release/workflow pages.

## Present source capabilities

[Feature Coverage](Feature-Coverage.md) maps implemented surfaces and their
proof limits. Production Depth World with canvas recovery, editor asset/world
authoring, typed packs, opt-in presentation and scripting, and separate
client/editor MCP paths now have source documentation. Their existence must
not be read as complete parity, full server support or every-device validation.

## Acceptance work still matters

- Reproducible gameplay/rendering parity at matched location, camera and settings.
  Screenshots alone cannot establish smooth remote animation or network acceptance.
- Wider [server case coverage](Server-Compatibility.md), preserving failed and
  not-run cells rather than claiming all emulators work.
- Platform/device checks for touch, controllers, multiple displays, rotation,
  exports and browser limitations; see [Platform Support](Platform-Support.md).
- Matched Release performance evidence under the [performance gate](../perf/performance_gate.md).
  Old timing reports do not establish the current build's speed.
- Current screenshots of settings, format-specific export dialogs and server
  operations; the [Visual Guide](Visual-Guide.md) records what is pictured.

## Incomplete or scoped features

Full Razor CE compatibility remains a goal; [embedded scripting](../embedded-scripting.md)
implements a bounded subset. Runtime journal translation is an optional local
pilot, not reviewed multilingual shard content. [Localization](../localization.md)
keeps future language-pack work separate.

Shard-required pack negotiation and consent remain roadmap work; do not infer
automatic downloading/activation from the Store or content deployment tools.
Current mod ABI capabilities are narrower than arbitrary gump/world editing;
[Modding](Modding.md) documents those boundaries. Labs remains excluded from
exports and needs separate acceptance before any promotion.

## Help with a concrete gap

Use [Contributing](Contributing.md) and the feature's source/proof link. Include
the exact build, environment, action and result. A narrow repeatable case is
more useful than an unsupported blanket compatibility or FPS claim.
