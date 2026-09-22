# plugin_host — the managed-assistant host

Razor, Razor Enhanced and ClassicAssist are .NET Framework assemblies. They
export no native `Install`; they expect to be loaded into .NET Framework and
handed a `CUO_API.PluginHeader`. Upstream ClassicUO does that in
`ClassicUO.Bootstrap`, the .NET Framework 4.7.2 exe that users start as
`ClassicUO.exe`. It loads the natively compiled client, `cuo.dll`, and passes
it a `HostBindings` table. When the client's `Plugin.Load` finds a DLL with no
native `Install` export, it calls back through that table, and the bootstrap
loads the assistant.

GUO is a Godot process, so nothing starts it through a bootstrap. This folder
is that bootstrap, ported to a library. `godot/GUO/src/Network/PluginClrHost.cs`
starts the .NET Framework CLR inside the Godot process, next to .NET 8, and
calls `Program.Start` here with the address of a zeroed `HostBindings`. The
host fills it in, and GUO wraps it in `UnmanagedAssistantHost`, as upstream's
`Initialize` export does.

## Provenance

Ported from upstream `src/ClassicUO.Bootstrap/src/` at the commit pinned in
`docs/upstream/UPSTREAM_PIN.json`. It is BSD 2-Clause, like the rest of
ClassicUO.

| File | Upstream | Changes |
|---|---|---|
| `src/Program.cs` | `Program.cs` | The top-level statements become `Program.Start(string)`. `Run` becomes `Bind(IntPtr)`, which only fills the table. An `AssemblyResolve` handler restores upstream's AppBase probing. Each change is marked `PORT DEVIATION (GUO)`. |
| `src/Plugin.cs` | `Plugin.cs` | Fills `HWND` from the handle GUO passes in, and sets `SDL_Window` to zero. Marked. |
| `src/CuoInternal.cs` | `CuoInternal.cs` | None. These are the reflection targets assistants look up by name, so they keep their `ClassicUO.*` namespaces, and the assembly keeps its upstream name, `ClassicUO`. |
| — | `LibraryLoader.cs` | Not carried over. Only `Run` used it, to load `cuo.dll`. |

`cuoapi.dll`, the plugin ABI, comes from the upstream checkout by HintPath,
the same way upstream references it. Nothing here re-declares it.

## Build

`dotnet build godot\GUO\GUO.csproj` builds this project as well. Its output
is copied to `plugin_host\` next to `GUO.dll`, where `PluginClrHost` looks
for it. This happens on Windows only, because .NET Framework is Windows only.
To build it on its own:

```
dotnet build tools\plugin_host\GUO.PluginHost.csproj
```

In a git worktree, where `sources\` is not checked out, pass
`-p:UpstreamDir=<main checkout>\sources\ClassicUO` to either build.

## When it runs

GUO starts the host only when `settings.json` lists at least one plugin.
Starting .NET Framework costs memory and time, so it is skipped when there is
nothing to host. Native plugins load without the host, as they do upstream.
