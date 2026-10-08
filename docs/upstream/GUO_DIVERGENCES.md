# GUO divergences from ClassicUO

Ported files stay as close to upstream as they can (AGENTS.md rules 2 and 3).
When a ported file has to differ from ClassicUO for a reason other than the
port itself (a renamespace, a Compat or SDL using, a rewrite-tier
replacement), the edit is listed here, so that the next upstream merge can
reconcile it by hand instead of overwriting it.

Each entry names the ported file, the upstream file, what changed, why, and
the GUO code it calls. Edits in ported files carry a `// GUO:` comment.

| Story | Ported file | Upstream file | Change | Why |
|---|---|---|---|---|
| SF7 | `godot/GUO/src/Client/Main.cs`, `ReadSettingsFromArgs` | `src/ClassicUO.Client/Main.cs`, same method | The `ARG: {cmd}, VALUE: {value}` trace logs `ArgTrace.Value(cmd, value)` instead of `value`: the options `username`, `password` and `password_enc` show `<hidden>`, every other option its value. One line. | GUO's bootstrap passes `-username` and `-password` to the client for autologin (scripted runs, probes), so any log that kept trace lines carried the account and its password. |

## Reconciling on an upstream merge

- **SF7.** Keep the `ArgTrace.Value(cmd, value)` call on the `ARG:` trace line.
  If upstream adds a command-line option that carries a credential, add its
  name to `ArgTrace.Secret` in `godot/GUO/src/Client/ArgTrace.cs` (GUO-owned).
  `dotnet run --project tools\arg_trace_tests` checks both: it fails if the
  trace prints a raw value, or if an option whose name holds `pass`, `user`,
  `token` or `secret` is not hidden.
