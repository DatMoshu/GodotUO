# comfy

ComfyUI client for GUO: images, audio (SFX/music) and 3D (gaussian PLY with
client LOD mipmaps). No Hunyuan anywhere in this set, ever.

```
python tools/comfy/run.py status
python tools/comfy/run.py txt2img --prompt "..." --out build/comfy/proof.png
python tools/comfy/run.py run --workflow tools/comfy/workflows/guo_multi.json --out-dir build/comfy/multi/ --save-splat "3d/GUO_multi_"
python tools/comfy/run.py run --workflow tools/comfy/workflows/guo_sfx.json --out-dir build/comfy/sfx/
python tools/comfy/run.py wait --prompt-id <id> --out-dir build/comfy/multi/
python tools/comfy/splatlod.py lod build/comfy/multi/<splat>.ply --out-dir build/comfy/multi_lod --stem armoire
python tools/comfy/stage.py --pack guo-comfy-gen --out build/staged/guo-comfy-gen --sfx 2100=<sfx.mp3> --music 101=<music.mp3> --splat armoire=build/comfy/multi_lod
python tools/comfy/probe.py --godot <godot-console> --stage build/staged/guo-comfy-gen --data <client>
```

* URL: `UO_COMFY_URL` (env → `launchers/_shared/config.local.bat` →
  `config.bat`, default `http://127.0.0.1:8188`). Same value the editor's Art
  dock reads, and the AI dock's Services tab (`ai_services.json`, Test hits
  `/system_stats`).
* `workflows/` holds GUO's tuned variants, derived from the hand-crafted
  originals by `make_guo_workflows.py` (re-runnable; documents every edit):
  `guo_sfx`/`guo_music` (reprompt on, working qwen encoder), `guo_multi`
  (external post removed, SaveGLB appended — SplatToFile3D is convert-only),
  `guo_seamless` (unchanged fork).
* `run` takes UI-format workflows (nodes/links + `definitions.subgraphs`,
  exactly what the frontend saves) or API format. `uiconv.py` expands
  subgraphs, resolves reroutes, mute/bypass, seed control widgets and UI-only
  widgets, and converts to an API prompt. `--validate` checks node types
  against the server; `--print-prompt` shows the converted prompt.
* `run` downloads every file ref in `/history` outputs, whatever the node
  type: `.png/.jpg/.webp`, `.wav/.mp3/.ogg/.flac`, `.obj/.fbx/.glb/.ply/.stl`.
* `--drop TYPE` removes sink nodes (external posts, stray saves with foreign
  paths). `--ui-set node.widget=value` overrides outer widgets, e.g.
  reprompt on (`52.3=true`), category (`52.4=music`).
* Polling is one quiet `/history` hit per 15 s. `wait` resumes an
  already-queued prompt without duplicating heavy 3D work.
* `splatlod.py` builds the client's mipmap chain (opacity/volume-weighted
  sampling; default ratios 1, 1/4, 1/16, 1/64) and derives the multi footprint
  (`footprint`: ground projection → nodraw-marker draft MultiEdit opens
  directly, e.g. wall 4x4/11 cells). The runtime
  (`src/Assets/SplatPlyParser.cs`, `src/Render/Splats/`) parses the levels,
  picks one by projected pixel radius and draws full gaussian ellipses:
  each splat's rotated 3D covariance projected to a screen-space ellipse
  (eigen-decomposed, depth-sorted back-to-front), baked into tile-space
  mesh quads the batcher draws itself (`DrawSplatMesh`, front view, Y-up).
  Isotropic billboards were tried first and read as coarse blobs live;
  the wall PLY (thin-Z, freely rotated gaussians) proved the ellipse path.
  The live scene draws staged placements every frame (`GameScene.Splats`);
  MultiEdit opens the footprint draft; `ComfyContentProbe` replays the
  batcher path headless; the batcher probe pixel-checks a red gaussian.
* `stage.py` packs audio into a verified `guo/store-pack@2` (sound/music by
  numeric id, CC0-1.0 local-test default, provenance per artifact) and stages
  splat LODs beside it (`splats.json`, `guo/comfy-splats@1`). `probe.py`
  mounts the pack in the real headless client and reads everything back
  (`ComfyContentProbe`: SFX bytes, music consumer, LOD counts).
* Live site proof: `--walk-to x,y[,z]` logs in, pathfinds there (waiting for
  an admin teleport when no walkable path exists, e.g. off an island), and
  screenshots it. Proven on a live shard: login, ServUO-bridge teleport,
  arrival 1 tile off, pack mounted (`content mounted` in the log).
* Ollama (already in the editor, `godot/GUO/addons/guo_editor/AI/ChatProviders.cs`):
  default `http://127.0.0.1:11434`, or `OLLAMA_HOST`. The ComfyUI
  `comfyui-ollama` pack (`OllamaGenerateV2`) hits the same server.

## Proven (local RTX 4070 box)

| What | Workflow | Result |
|---|---|---|
| image | built-in txt2img (DreamShaper_8) | `build/comfy/proof.png` 512×512 |
| music | WORKINGAUDIO (reprompt off) | 360 KB MP3, valid ID3 |
| SFX | FASTAUDIOTGEN, reprompt on | 176 KB MP3 `church_bells_`, valid ID3 |
| texture | SEAMLESS | Albedo/Normal/Height/Curvature PNGs |
| 3D | MultisMaker1, JarJarSubmit3D dropped | 4.4 MB gaussian PLY, 64,800 splats |
| LOD | splatlod on the armoire | 64800/16200/4050/1012, mass scales linearly |
| pack | stage.py | `guo-comfy-gen.zip`, asset-store `policy pass` |
| client | probe.py headless | SFX 2100 bytes + music 101 + splat chain parsed |

Notes: FASTAUDIOTGEN as saved fails with reprompt on — its `qwen_clip`
widget (`minimax_music3_text_encoder…`) crashes `TextGenerate` (matmul
1341×4096 vs 1024×4096); `qwen3.5_2b_bf16.safetensors` (as in WORKINGAUDIO)
works, and the GUO variants bake that in. MultisMaker1's `SplatToFile3D` is
convert-only; without a save node the PLY exists only as a preview temp file.
