# Embedded scripting preview

GUO has an opt-in scripting panel implemented in Godot, with a per-character
script library. It executes a **limited subset of Razor CE command syntax**
inside the client. It is independently implemented; it does not embed or
redistribute Razor, and does not require an assistant DLL or separate window.

## Product and licensing decision — 2026-10-02

Maintain **both** user choices: optional external assistants and the embedded
independently implemented engine. Keep GUO's own implementation BSD-2-Clause.
Do not copy or link upstream GPL interpreter/handler code into the embedded
engine. Compatibility is implemented from documented behavior and original
tests; original templates are included with the editor.

The existing external plugin configuration and host remain available. On
Windows, `settings.json`'s `plugins` array selects user-installed assistant
DLLs; see [the managed host](../tools/plugin_host/README.md). Native and
managed plugin probes must remain passing as embedded scripting develops.
External assistants retain their own licenses and platform requirements;
this does not bundle Razor or promise Windows assistants on Android/web.
The host probe verifies the ABI with test plugins, not every real assistant.

Embedded scripts never start automatically. Both systems may be installed,
but their runners are separate: embedded Stop does not stop an external
assistant. Simultaneously automating targeting or inventory can produce
conflicting actions. The embedded engine does not intercept plugin loading
or bypass the client's normal packet/filter path.

## Open and run

1. Log in and press **Ctrl+Shift+R**, or enter **`-scripts`** in client chat.
   Touch users can open **Options → Macros → Scripts**, or assign **Scripts**
   and **Stop script** to command-bar slots.
2. Type or paste a script. **Save** stores it under the current character's
   profile, in `scripts/script-NAME.razor`. Names use letters, digits, `_`, `-`.
3. **Run** validates the entire script before executing any command.
4. **Stop**, **Ctrl+Shift+F12**, or **`-stopscript`** cancels the current run.

Closing the panel keeps a running script active. Unsaved edits stay in the
panel during the session, but are discarded on logout. Load/New ask for a
second click before discarding edits. Saving over another script's name also
requires a second click. No script starts automatically on login or load.
Only one embedded script runs at once; Run executes a snapshot of the source.

The editor has syntax colors, command signatures, desktop completion and a
touch Suggest list. **Starter scripts** opens a searchable preview popup.
**Add to my scripts** saves a personal copy without changing current edits or
running it; repeated imports receive a new name. Choose Load to edit the copy.

## Supported syntax

One command per line. Single or double quotes group multiword arguments.
Blank lines and `#` / `//` comments between tokens are accepted. Quoted text
preserves comment characters. Escaped quotes are not implemented; use the
other quote delimiter when needed.

| Command | Preview behavior |
|---|---|
| `sysmsg 'text' [hue]` | Local journal/system message; default hue 946 |
| `say 'text' [hue]` | Ordinary speech through GameActions; default profile speech hue |
| `pause milliseconds`, `wait milliseconds` | Yield to the game loop |
| `dclick serial` | Double-click an existing object |
| `cast 'spell name'` | Cast a Magery spell by its client name, case-insensitive |
| `waitfortarget [milliseconds]`, `wft [milliseconds]` | Await a server object/position target; preview default 30 seconds |
| `target serial` | Target an existing object using the active server cursor |
| `stop` | End the current script |
| `if` / `elseif` / `else` / `endif` | Conditional blocks |
| `while` / `endwhile` | Reevaluate a condition each iteration |
| `for count` / `endfor` | Counted loop with zero-based `index` |
| `foreach 'variable' in 'list'` / `endfor` | Iterate list values |
| `break`, `continue` | Control the innermost loop |
| `loop`, `replay` | Restart the current program without recursive calls |
| `setvar`, `setvariable`, `unsetvar`, `unsetvariable` | Script-local variables |
| `createlist`, `clearlist`, `removelist`, `pushlist`, `poplist` | Basic list operations |
| `createtimer`, `settimer`, `removetimer` | Timers on the injected monotonic clock |

Expressions currently include boolean operators, comparisons, character
vitals/status, journal substring checks, list membership/counts and timers.
Wait durations can refer to previously declared variables. Static structure
and literal commands are checked before execution; values depending on runtime
variables or world state can still fail during execution.

Object arguments accept `self`, `backpack`, decimal serials and `0x` hex
serials. Hues are decimal 0–65535. Delays/timeouts are 0–3,600,000 ms.
The script size limit is 65,536 characters. Execution advances at most one
instruction per game update, with a minimum 25 ms gap between instructions.
These are preview limits, not a claim of exact Razor execution timing.

```text
// Local demonstration, without sending speech to other players.
sysmsg 'Starting'
pause 1000
sysmsg 'Finished'
```

```text
cast 'heal'
waitfortarget 5000
target 'self'
```

The second example depends on the server accepting the cast. Missing targets,
unavailable objects, runtime errors and disconnects stop execution with a
line-numbered error. Cancellation prevents future commands; it cannot undo
packets already sent. It does not dismiss an existing targeting cursor.

## Compatibility boundary

This is **not full Razor CE compatibility**, and it is not Razor Enhanced
Python. Agents, recording, per-script hotkey assignments, `@` prefixes,
other spell schools and unlisted commands remain unsupported. Expression
quoting, list variants, aliases, target queues and exact timing still need
upstream behavioral comparisons. Unknown commands fail validation before any
action runs. Legacy external assistant hosting remains independent.

Full compatibility is the goal, not the present status. Each added command
needs argument/error tests, observable host-action tests and relevant live
shard checks. Unit tests of our interpretation alone do not prove CE parity:
a subsequent conformance suite must compare original script fixtures with a
pinned upstream release, including errors, defaults and timing behavior.

The syntax reference is the [Razor CE command guide](https://www.razorce.com/guide/commands/).
No upstream Razor implementation was copied. Upstream-engine integration is
excluded by the owner's permissive-license decision.

## Implementation and verification

- `src/Game/Scripting/ScriptRunner.cs`: engine-independent parser/scheduler;
  injectable host and monotonic clock for deterministic tests.
- `ClientScriptHost.cs`: validated actions routed through existing GameActions
  and TargetManager, on the game thread. No packet bypass or Compat expansion.
- `ScriptLibrary.cs`: profile-scoped files and temporary-file replacement.
- `src/Input/Touch/Modern/ModernScripts.cs`: UoTheme panel and CodeEdit;
  nearest-neighbour viewport, native mouse selection/wheel input.
- World owns the runner; World.Clear cancels it before dropping character state.

Run `dotnet run --project tools/script_tests/ScriptTests.csproj` for isolated
runner and library checks. Run the client with `--scripts-probe` against the
local dev shard for real editor input, save/load, Run/Stop, validation and
journal checks. The probe uses a scratch profile and captures the panel through
the standard `--screenshot-dir` / `--screenshot-name` options. Build with
`dotnet build godot/GUO/GUO.csproj`; worktrees can supply `-p:UpstreamDir=...`.

Mobile and web packaging, physical-device soft keyboard behavior and full CE
compatibility have not been verified. The feature does not depend on the
Windows CLR host. Touch text scrolling and a multiline keyboard request are
implemented; real-device keyboard occlusion and text selection need testing.

Verified on 2026-10-02: client build, 67 independent runner/library checks,
20 live editor checks each on desktop (1100x780) and simulated portrait touch
(720x960), and all six repository smoke stages passed. Desktop editor/starter
popup and portrait editor captures were visually inspected. A subsequent
landscape run also validated all eight starter scripts with the real client
host. At 1280x720 with `GUO_UI_SCALE=2`, all 29 checks passed, including actual
touch selection of a completion and a minimum visible height for the starter
list. Both scaled screenshots were inspected. This caught and fixed duplicate
scaling in the shared probe-coordinate helper, and a collapsed starter list
in short cards. The probe asserts Run remains inside the visible card. Local evidence is under
`build/scripts/` (gitignored). The live run reports CanvasItem/ObjectDB leak
warnings at process exit; their origin has not been isolated by this work.

External-host regression: the native and managed test plugins passed their
host assertions (load, lifecycle, both packet directions, positions and grown
packet rejection). The accompanying full gameplay session exited 1 with
20/28 checks passing: movement/world picking, vendor, combat and trade checks
failed. This is not a clean full gameplay pass or certification of actual
Razor/Enhanced/ClassicAssist builds. Evidence: `build/scripts/plugins.log`
and `build/plugin_probe/session.log`.

## Script packs and shared store integration

The current `guo/store-pack@1` `razor-script` adapter supports bounded UTF-8
source files, verified offline browsing and explicit import into the personal
library. Import preserves licence attribution and never overwrites an existing
script. Installation, browsing and compatibility checks never execute the
source. Updating or uninstalling a pack does not modify personal copies.
The store tests cover payload rejection, tampering, duplicate imports, updates
and cleanup: 82 Python/C# validator cases agree, 10 store tests pass and the
HTTP store smoke passes. The desktop store-to-editor proof passes 46 checks,
including actual clicks through approval, enable, Run, disable, revoke and import.
At 1280x720 with simulated touch and UI scale 2, 47 checks pass, including a
viewport-bound check for the managed controls. The first capture exposed a
clipped bottom row; a wrapping button row fixed it and the new capture was
visually inspected. This remains desktop touch simulation, not a device test.

This v1 adapter is not the final ecosystem contract. The asset-pack coordinator
owns `guo/store-pack@2` shared validators, UI and contracts. Its script components
declare `type: script`, a local component ID, target, entry, language `razor-ce`,
runtime `guo-razor`, an exact `runtime_version` and required capabilities.
Namespaced identities are `pack-id:component-id`; dependencies pin exact versions
and the verified closure pins payload hashes for approval. Initial execution is
client-only. Unsupported targets, runtimes, versions and capabilities stay inert.

`ScriptPackSession` implements session-only review, approval, enable, Run,
disable, revoke and rollback. Approval binds the entire canonical manifest,
all declared payload hashes, capabilities and resolved dependency identities.
Changing auxiliary files requires renewed approval. Each review/approval/enable/
Run reverifies through `VerifyContent` and uses bounded `ReadPayload` bytes.
No executable is loaded directly from a disk path. A `CapabilityScriptHost`
checks declarations before compilation and again before prepared actions run;
unknown capabilities and commands fail closed. Runtime version `0.1.0` denotes
the implemented CE subset, not full compatibility.

The world owns the session and ticks its runner. A store change only signals a
dirty flag from its notification thread; the next game-thread tick reverifies
approvals before any action. Disable, revoke, dependency removal and logout
stop affected execution. Logout disposes the store client and unsubscribes the
notification handler. The editor and emergency stop controls cover both runners.
Failed activation retains the previous activation. Rollback reverifies an old
approved release and never restarts it automatically; it cannot undo gameplay
actions already sent to a server. Approvals are deliberately not persisted.
Manual filesystem mutations outside StoreClient are caught on the next explicit
review/enable/Run or reconciliation; already loaded source does not change.

The isolated scripting suite passes 100 checks including capabilities, approval
gates, dependency auxiliary changes, revoked actions, rollback and notification-
driven uninstall cleanup. The v2 Python/C# corpus agrees on 18 cases. Generate
the original managed example with `tools/asset_store/razor_scripts.py --managed`.
Personal copies are explicitly independent, editable scripts; they do not retain
managed pack approval and run only through the separate personal editor action.
There are no install hooks or automatic world-save migrations. Server migration
and snapshot machinery belongs to the server integration.
