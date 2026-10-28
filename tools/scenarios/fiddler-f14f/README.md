# F14f: audition and slot browsing

Layer L3. Manual editor tools; no runtime toggle, shard or external service.
Requires the configured user-owned client data and pinned .NET Godot editor.

1. Open Assets > Sounds > Effects. Compare occupied and empty views with All;
   they must partition 0x0000–0x0FFF, with stable library-wide totals under search.
2. Select an empty ID. Confirm the inspector is read-only with no document,
   PCM or import/export actions. Next empty advances by ID without wrapping.
3. Open an export review containing occupied and empty selected rows. Confirm
   the displayed occupied/total count, and that empty-only scopes cannot export.
4. Select an occupied effect and click Loop. Check source PCM status, then Stop,
   Escape, selecting another effect and changing families each stop playback.
5. Import a short valid WAV into the working copy, loop it for several cycles,
   then undo/revert. Confirm project PCM is auditioned and the native source is
   unchanged. Music has no effect-loop control.
6. Run the headless reload proof and full project smoke; inspect the authorized
   native captures at 1,920, 1,366 and 1,024 widths and the empty-slot inspector.

Commands:

```
python tools/editor_smoke/run.py --reload --out <evidence-output>
python tools/editor_smoke/run.py --windowed --assets-workbench --out <evidence-output>
launchers\dev\smoke.bat
```

The windowed proof is used only with the person at the machine's agreement.
Native insertion, removal and archive staging remain separate milestones.
