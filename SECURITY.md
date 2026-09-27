# Security Policy

## Supported versions

Only the `main` branch receives fixes.

## Reporting a vulnerability

**Do not report security issues in a public issue.**

Use GitHub's private vulnerability reporting: the repository's **Security**
tab → **Report a vulnerability**, when available. If that control is absent,
ask a maintainer for a private reporting channel without including the
vulnerability details in a public message.

Include the affected commit, platform, entry point, expected impact and a
minimal reproduction. Synthetic packets or packs are preferable to client
data. Redact account credentials, private addresses and personal paths.
Report only systems and data you are authorized to test.

This is a volunteer project without a guaranteed response or fix deadline.
Maintainers should coordinate a fix and disclosure with the reporter through
the private channel. Do not publish exploit details while that coordination
is in progress.

## In scope

GUO is a game client that connects to servers other people run, and loads
assistant plugins. The things that matter most:

- **Network input.** A malicious or compromised shard sending packets that
  crash the client, corrupt memory, or write outside the client's own folder.
  The packet readers use unsafe code, as upstream does.
- **Plugins.** The plugin host loads native and .NET assistants (Razor,
  ClassicAssist and the like) into the client process. A plugin is trusted
  code by design; bugs where the host lets a plugin write past a buffer, or
  loads something the user did not list, are in scope.
- **Client data.** Anything that makes the client write to the UO install,
  which it must only ever read.
- **The tooling.** Launchers, Python tools or `.claude/` hooks that run
  something undisclosed, or send data anywhere.
- **Asset packs.** Path traversal, hash/manifest validation bypasses,
  unintended executable content, or installation/uninstallation outside the
  Store's user-data directory. A remote Store is trusted through its
  configured URL; a hash alone does not authenticate a publisher.

## Out of scope

- Server-side issues in ModernUO or any shard.
- Cheating or automation through assistant plugins. Loading them is a
  supported feature, as it is in ClassicUO.

If a GUO vulnerability also affects [ClassicUO](https://github.com/ClassicUO/ClassicUO),
tell the maintainers privately so they can coordinate with upstream. Sharing
code with upstream does not make a GUO exposure safe to disclose publicly.
