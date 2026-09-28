# trailer

Cuts the going-public trailer from the clips the agents recorded (read only)
plus procedural parts: the boot splash, the Store loops, a screensaver and the
end card. Music is a procedural pad (CC0). Nothing ripped, no UO logos.

```
python tools/trailer/run.py [--clips <worktrees dir>]
```

Writes `build/trailer/GodotUO_trailer_1080p.mp4` (the master, 1080p30, x264
crf 16) and `build/trailer/GodotUO_trailer_discord.mp4` (720p, two-pass,
under 9.5 MB). Frames stream to ffmpeg, so memory stays small.

The splash frames come from the game itself (1920x1080, every frame, engine time):

```
godot-console --path godot/GUO --fixed-fps 60 res://src/Bootstrap/SplashProof.tscn -- --out build/trailer/work/splash --size 1920x1080 --plain --all
```

Captions say only what the footage shows. The browser shot is GUOWeb's run
in Chrome on the shard (from after its login screen). There is no Windows or
Steam Deck gameplay capture yet, so the title card only names them.
