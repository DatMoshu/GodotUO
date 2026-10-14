# GUO Labs (L4)

Labs are experimental client features, not base-client authoring. The owner
approved the layer policy in AGENTS.md and CONTRIBUTING.md. This folder starts
with an empty registry; it enables no command, option, model or service.

Opt in locally:

```text
dotnet build godot/GUO/GUO.csproj -c Debug -p:GuoLabs=true
```

The default is `GuoLabs=false`. Only an opted-in Debug build defines `GUO_LABS`
and compiles this folder. Release, ExportDebug and ExportRelease exclude all
Labs source even when `GuoLabs=true`; exported clients never carry Labs.
Outside this folder, references must be wrapped in `#if GUO_LABS`.

Put a future feature under `src/Labs/<feature>/`. Implement the shared partial
`LabsRegistration.RegisterFeatures(Context)` once, dispatching to each feature's
registration. Its `Context.Command` feeds CommandManager and `Context.Option`
feeds action rows in Modern Options. Both hosts call the same registration hook;
the absent surface is a no-op. Registration must only bind actions, never launch
a process, load a model or contact a service. Each feature supplies its own
options panel and must preserve its cancellation/disposal lifetime.

Every feature needs an owner-decided proposal, default-off behaviour, README,
dependency doctor with installation guidance, plain failure text naming that
doctor, scenario and smoke line. Generated content belongs in typed Store packs.
Promotion to L1 needs an off-state parity proof and the L1 profile/Options/ADR
requirements. No experimental feature is included in this policy change.
