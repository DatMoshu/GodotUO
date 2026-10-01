# FAQ

**What is GUO? Is it a client or an emulator like RunUO/ServUO?**
A client: a port of ClassicUO to the Godot engine that connects to RunUO, ServUO or ModernUO
shards. See [What is GUO?](What-Is-GUO.md) for how it differs from ClassicUO.

**Is there a web build?**
Yes, locally. The official Godot 4.7.2 mono still refuses to export a C#
project to the web (godotengine/godot#70796), so GUO is exported with a
community build of 4.7.2 that includes the open pull request #106125. It
plays on a local shard in Chrome, and reaches the login screen in Firefox.
Your own UO install is served to the page from your own PC. There is no
hosted page. See [Web Client](Web-Client.md) and ADR-0008 (Amendment 1).

**The audit says 100% ported. Is it finished?**
No. "Ported" means the file exists in `godot\GUO\src` and compiles. It says
nothing about behaviour. Working claims need a smoke run, a screenshot or a
parity diff, and the repository's status is "playable on a local shard, not
yet at parity". See [Parity and Drift](Parity-and-Drift.md).

**Why must the Godot logo never appear?**
The project's brand is the GUO sigil, and a build that shows the engine's
logo anywhere (executable icon, splash, launcher icon) is a build that was
not finished. `tools\windows\run.py export` fails if the exported
executable's icon still matches the export template's; the Android icons are
dumped from the APK and checked the same way. The one place it still shows
is the first few hundred milliseconds of a run through the engine binary
itself (`play.bat`, the editor), which is the engine executable's own
resource; an exported build never shows it. See [Windows Build](Windows-Build.md).

**Why is pixel art never filtered?**
UO's art is hand-placed pixels at one client pixel per screen pixel, and any
bilinear sampling smears it. `default_texture_filter=0` in `project.godot`
is deliberate, and every new viewport or material must keep nearest-neighbour
sampling (AGENTS.md rule 7). The exceptions are things that are not UO art:
the brand sigil (Lanczos) and a player's own background picture or video
(`Linear`).

**Can I use my own Godot install?**
No. The engine is pinned at 4.7.2 stable mono and fetched into
`tools\godot` by the bootstrap; the non-mono build cannot run C#, and other
versions have not been tried. `GODOT_EXE` overrides the executable if you
must.

**`godot` prints nothing from a script.**
Use `godot-console`. The plain executable returns at once and writes nothing
to stdout; the console build blocks and prints. Every script, agent and CI
step must call the console build.

**Where do I put my UO install path?**
`launchers\_shared\config.local.bat`, copied from
`config.local.bat.example`. It is gitignored. Never in `config.bat`, and CI
rejects a machine path anywhere else.

**Can I commit a screenshot?**
Not one rendered from game data. Captures of the client are proprietary art;
they stay under `build\` (gitignored). Describe what the picture showed in
the commit message and keep the file locally.

**The client connects to nothing.**
Nothing listens on `127.0.0.1:2593` until you run a shard. See
[Dev Shard](Dev-Shard.md), or point `UO_SHARD_HOST` / `UO_SHARD_PORT` at
one.

**Why does a scripted run not take focus?**
So an agent can run the client while a person works at the machine. Windows
are created without focus for scripted runs; `--focus` asks for it. The
ClassicUO side of the A/B tool has to type into a window, which is why its
pass is behind `--allow-foreground`.

**What is `PORT DEVIATION (GUO)`?**
The marker on every unavoidable change to a ported file. It is what gets
reconciled on an upstream merge. See [Parity and Drift](Parity-and-Drift.md).

**Which phones work?**
One Android 13 handheld has been run, including its second screen. Anything
else is written, not verified. See [Android Build](Android-Build.md) and
[Mobile UI](Mobile-UI.md).

**Does an exported build load Razor and the other assistants?**
On Windows, yes: the exported build ships the plugin host and
`plugin_probe.bat` checks a native and a managed plugin. On Android, no: the
plugin host is a Windows-only build item.

**Where are the agents and skills?**
`.claude\` holds the studio agents (from Claude Code Game Studios, MIT) and
the port-specific ones listed in `AGENTS.md`. Claude Code, Codex, GitHub
Copilot and Cursor all read the same instructions and skills (Codex needs
`launchers\dev\agent_skills.bat` once). They follow the same rules a
person does; nothing they run is undisclosed, and `SECURITY.md` counts a
hook that sends data anywhere as in scope.
