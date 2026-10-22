# Private effect review clips

`EffectReviewVideo.tscn` is an editor-only capture fixture, compiled only with
`GuoLabs=true` in Debug. It draws the production weather primitives, ambient
particles or water shader/wave field on synthetic ground. It does not load a
shard, change a player profile or demonstrate live integration.

Build `dotnet build godot/GUO/GUO.csproj -p:GuoLabs=true`, set
`GUO_REVIEW_MODE` to `weather`, `water` or `particles`, and `GUO_REVIEW_FRAMES`
to an evidence folder outside the checkout. Run `godot-console --path godot/GUO
--fixed-fps 20 res://addons/guo_editor/Review/EffectReviewVideo.tscn`.
It writes 240 actual renderer frames (12 seconds at 20 fps). Encode externally
with `ffmpeg -framerate 20 -i <folder>/%04d.png -c:v libx264 -pix_fmt yuv420p <clip>.mp4`.

The native `--uoplus-ui-probe` can also capture staged edits, Apply, Defaults,
Cancel and controller navigation by setting `GUO_REVIEW_UI_FRAMES` to an
evidence folder. Use `--fixed-fps 20` and encode those frames at 20 fps.
Without this variable the probe retains its usual timing. It uses an unsaved
fixture profile; the video is scripted UI proof, not physical-controller proof.
