# GUO multi-server workspace

The Godot editor run bar keeps named local servers and remote endpoints. Each profile selects a server installation/code project, a client Godot project, client data, and optional content deployment. Client settings, accounts, logs and cache are isolated by server and client slot. The manager only stops the exact process it started, checking PID, creation time and executable. Stop forcibly ends the process: save the world first.

## Get started

1. Run `python tools/server_manager/run.py init` from the repository. This adds missing backend profiles without replacing existing profiles. Close the manager while using the CLI so its in-memory list cannot overwrite external changes.
2. Open the GUO Godot editor and choose **Manage servers**. The **Add backend starters** button provides the same six templates inside the editor.
3. Select a profile and click **Setup / validate** for upstream setup instructions. Install/build the native server and scripts, configure its client data and listener, and complete first-run account creation using that server's tools.
4. Set its executable and working directory using **Browse**. Set client project/data and optional server code project; save the profile. Profiles do not rewrite native server configuration.
5. Choose it in the run bar, start the server, then start one to four clients. Remote profiles can launch clients; manage their server on its own host.
6. Run `python tools/server_manager/run.py doctor` to list missing installation files. Exit 1 means setup remains. File presence and an open port are readiness indicators, not gameplay proof.

| Backend | Starter game port | GUO server content adapter |
|---|---:|---|
| ModernUO | 2610 | Implemented; validate each deployment |
| ServUO | 2611 | All seven sections; tile light/animation overrides refused |
| RunUO | 2612 | All seven sections; tile light/animation overrides refused |
| POL | 2613 | Item definitions |
| Sphere X | 2614 | Item definitions |
| UOX3 | 2615 | Item definitions |

These are starter profiles, not bundled or prevalidated server distributions. Custom executable names/platform builds can be selected with Browse. The upstream links and setup requirements are in `backends.json`. Each native installation needs separate world saves, configuration and admin/bridge ports. The existing ModernUO editor bridge is not automatically switched by selecting another server in this run bar.

Profiles live in ignored `build/editor_servers/profiles.json`; suggested installations live in `build/servers/<backend>`. Never put game data or credentials into source control. Profile removal keeps server files and saves. Closing the editor leaves managed servers running; reopening can recover their process identity.

## Validation checklist per backend

Record the exact server revision, client version, configuration and results in local build artifacts. Prove login/account creation, character creation, entering the world, movement, items/gumps, save and restart. Then validate a server-only pack, client-only pack, combined pack, update/rollback, removal and reconnect. The generators reject unsupported sections before publication. See [adapter usage and limits](../server_adapters/README.md). A successful native probe does not replace a live GUO login/world/save/restart proof for each backend.

Process safety regression check: `dotnet run --project tools/server_manager/tests/ServerManager.Tests.csproj`.
Editor lifecycle check: `python tools/editor_smoke/run.py --headless --reload`.
