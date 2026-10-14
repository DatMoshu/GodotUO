# DLSS5 in-game integration probe (dev/dlss5_probe.gd).
#
# Loads the addon's fp16 graph, enhances a PNG through the C# service's
# native extension path, saves the result. Mirrors the other dev probes
# (SceneTree script, takes args after --).
#
# Run:
#   tools\godot\godot.cmd --path godot/GUO --headless \
#     --script res://dev/dlss5_probe.gd -- <in.png> <out.png> [intensity] [profile]
#
# Pure compute + file IO, so --headless is correct here (unlike the
# rendering probes).

extends SceneTree


func _initialize() -> void:
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() < 2:
		print("usage: --script res://dev/dlss5_probe.gd -- <in.png> <out.png> [intensity] [profile]")
		quit(1)
		return
	var src_path: String = args[0]
	var dst_path: String = args[1]
	var intensity := 1.0
	var profile := 0
	if args.size() >= 3 and args[2].is_valid_float():
		intensity = float(args[2])
	if args.size() >= 4 and args[3].is_valid_int():
		profile = int(args[3])

	var img := Image.load_from_file(src_path)
	if img == null:
		print("PROBE-FAIL cannot load ", src_path)
		quit(1)
		return
	var t0 := Time.get_ticks_msec()

	# Exercise the exact same entry point the game uses: the C# service.
	# (From GDScript we mirror its calls; the C# side is exercised by the
	# hook in GameController.TakeScreenshot.)
	var bridge := load("res://addons/dlss5/dlss5_bridge.gd") as GDScript
	var dlss: Object = bridge.new().call("make")
	var model: String = "res://addons/dlss5/models/dlssnr-core-320-fp16-vendorsm.onnx"
	var tables: String = "res://addons/dlss5/models/dlss_tables.bin"
	var ortdir: String = "res://addons/dlss5/bin"
	var rc: int = dlss.load_model(ProjectSettings.globalize_path(model),
		ProjectSettings.globalize_path(tables), ProjectSettings.globalize_path(ortdir))
	if rc != 0:
		print("PROBE-FAIL load: ", dlss.get_error())
		quit(1)
		return
	print("PROBE provider=", dlss.get_provider())

	var square: Image = img.duplicate()
	var w: int = img.get_width()
	var h: int = img.get_height()
	var side: int = mini(w, h)
	square = img.get_region(Rect2i((w - side) / 2, (h - side) / 2, side, side))
	square.resize(320, 320, Image.INTERPOLATE_BILINEAR)
	square.convert(Image.FORMAT_RGB8)

	dlss.set_params({
		"profile": profile,
		"intensity": intensity,
		"detail_strength": 1.0,
		"colour_strength": 1.0,
		"detail_radius": 4.0,
		"frame_index": 0,
	})
	var out_img: Image = dlss.enhance(square)
	if out_img == null:
		print("PROBE-FAIL enhance: ", dlss.get_error())
		quit(1)
		return
	out_img.resize(640, 640, Image.INTERPOLATE_LANCZOS)
	out_img.save_png(dst_path)
	print("PROBE-OK net_ms=%.0f provider=%s saved=%s wall_ms=%d" % [
		dlss.get_last_ms(), dlss.get_provider(), dst_path,
		Time.get_ticks_msec() - t0])
	quit(0)
