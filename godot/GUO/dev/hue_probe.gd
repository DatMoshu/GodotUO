# Temporary probe for ADR-0002: how much data survives a canvas item's
# per-quad modulate colour, and where can it be read?
#
# ADR-0002 wants to carry ClassicUO's per-sprite hue vector -- (hue index,
# shader mode, alpha) -- in the modulate rather than building a vertex format.
# Two things have to be true and neither can be assumed on Godot 4.7.2, which
# is past the assistant's knowledge cutoff:
#
#   1. the raw modulate must be readable in the shader, unmultiplied by the
#      texture, and
#   2. it must carry enough bits for a hue index, which runs to ~3000 and so
#      needs 12.
#
# Run: godot-console --path godot/GUO --script res://dev/hue_probe.gd
# (NOT --headless: the dummy renderer never produces a frame.)

extends SceneTree

const SHADER_PASSTHROUGH := """
shader_type canvas_item;

// COLOR in fragment() is ALREADY multiplied by the texture -- measured, this
// probe's first version failed on exactly that. vertex() still sees the raw
// vertex colour, so carry it across in a varying.
varying vec4 raw;

void vertex() {
	raw = COLOR;
}

void fragment() {
	COLOR = vec4(raw.r, raw.g, raw.b, 1.0);
}
"""


func _render(modulate: Color) -> Color:
	var mat := ShaderMaterial.new()
	var shader := Shader.new()
	shader.code = SHADER_PASSTHROUGH
	mat.shader = shader

	var img := Image.create_empty(1, 1, false, Image.FORMAT_RGBA8)
	img.set_pixel(0, 0, Color(0.5, 0.5, 0.5, 1.0))

	var sprite := Sprite2D.new()
	sprite.texture = ImageTexture.create_from_image(img)
	sprite.centered = false
	sprite.material = mat
	sprite.scale = Vector2(4, 4)
	sprite.modulate = modulate

	var vp := SubViewport.new()
	vp.size = Vector2i(4, 4)
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	vp.add_child(sprite)
	root.add_child(vp)

	await process_frame
	await RenderingServer.frame_post_draw

	var px: Color = vp.get_texture().get_image().get_pixel(2, 2)
	vp.queue_free()
	return px


func _initialize() -> void:
	var failures := 0

	# --- 1. is the raw modulate readable at all? ---
	var px: Color = await _render(Color(0.25, 0.5, 0.75, 1.0))
	var raw_ok := absf(px.r - 0.25) < 0.01 and absf(px.b - 0.75) < 0.01
	print("[probe] raw modulate readable via varying : %s  (got %.3f, %.3f, %.3f)"
			% ["yes" if raw_ok else "NO", px.r, px.g, px.b])
	if not raw_ok:
		failures += 1

	# --- 2. how many bits per channel survive? ---
	# Walk values one 8-bit step apart and one 16-bit step apart. If the
	# channel is 8-bit the second pair comes back identical.
	var a: Color = await _render(Color(100.0 / 255.0, 0, 0, 1))
	var b: Color = await _render(Color(101.0 / 255.0, 0, 0, 1))
	var c: Color = await _render(Color(100.2 / 255.0, 0, 0, 1))
	var step8 := absf(a.r - b.r)
	var sub8 := absf(a.r - c.r)
	print("[probe] one 8-bit step apart  -> delta %.5f" % step8)
	print("[probe] sub-8-bit step apart  -> delta %.5f" % sub8)
	var eight_bit := step8 > 0.002 and sub8 < 0.0005
	print("[probe] channel precision            : %s"
			% ["8-bit" % [] if eight_bit else "better than 8-bit"])

	# --- 3. does a 12-bit hue index survive a two-channel split? ---
	# hi = index >> 8 (4 bits used), lo = index & 0xFF.
	var worst := 0
	for index in [0, 1, 255, 256, 999, 2048, 3000, 4095]:
		var hi := float(index >> 8) / 255.0
		var lo := float(index & 0xFF) / 255.0
		var out: Color = await _render(Color(hi, lo, 0, 1))
		var decoded := int(round(out.r * 255.0)) * 256 + int(round(out.g * 255.0))
		var err: int = absi(decoded - index)
		worst = maxi(worst, err)
		if err != 0:
			print("[probe]   index %d decoded as %d" % [index, decoded])
	print("[probe] 12-bit index, 2-channel split: worst error %d" % worst)
	if worst != 0:
		failures += 1

	# --- 4. and in a single channel, for comparison? ---
	var single_worst := 0
	for index in [0, 1, 255, 256, 999, 3000]:
		var out: Color = await _render(Color(float(index) / 4095.0, 0, 0, 1))
		var decoded := int(round(out.r * 4095.0))
		single_worst = maxi(single_worst, absi(decoded - index))
	print("[probe] 12-bit index, 1 channel      : worst error %d" % single_worst)

	print("[probe] %s" % ["PASS" if failures == 0 else "FAIL"])
	quit(0 if failures == 0 else 1)
