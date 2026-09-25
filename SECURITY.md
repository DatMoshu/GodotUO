# Security Policy

## Supported versions

Only the `main` branch receives fixes.

## Reporting a vulnerability

**Do not report security issues in a public issue.**

Use GitHub's private vulnerability reporting: the repository's **Security**
tab → **Report a vulnerability**. Include what is affected, how to reproduce
it, and the impact you expect.

You should get an acknowledgement within a week. This is a volunteer project;
fixes land as soon as they reasonably can, and you will be told before
anything is disclosed.

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

## Out of scope

- Bugs that are also in upstream ClassicUO: report them to
  [ClassicUO](https://github.com/ClassicUO/ClassicUO) as well, and we will
  follow its fix.
- Server-side issues in ModernUO or any shard.
- Cheating or automation through assistant plugins. Loading them is a
  supported feature, as it is in ClassicUO.
