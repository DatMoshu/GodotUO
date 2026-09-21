# Can a RenderingServer canvas item read what was drawn underneath it?
#
# ClassicUO's effects use five custom blend states, and three of them have no
# equivalent among Godot's fixed canvas blend modes: normalHalf is 2*src*dst,
# shadowBlue is a reverse subtract, and XNA's (One, One) additive is not
# Godot's blend_add whenever alpha is below 1. The only way to express an
# arbitrary blend equation is to turn blending off and do the arithmetic in
# the shader against the destination -- which means the shader has to be able
# to READ the destination.
#
# Godot exposes that as hint_screen_texture plus a back-buffer copy. The
# batcher builds its canvas items through RenderingServer rather than as
# nodes, so what matters is whether canvas_item_set_copy_to_backbuffer works,
# not whether a BackBufferCopy node does. That is what this measures.
#
# Run: godot-console --path godot/GUO --script res://dev/blend_probe.gd
# (NOT --headless: the dummy renderer never produces a frame.)

extends SceneTree

const READ_BACK := """
shader_type canvas_item;
render_mode blend_disabled, unshaded;

uniform sampler2D screen : hint_screen_texture, filter_nearest;

void fragment() {
	// Reverse subtract, the one upstream blend Godot has no mode for:
	// dst * (1 - src) - src * src, evaluated by hand.
	vec4 dst = texture(screen, SCREEN_UV);
	vec4 src = COLOR;
	COLOR = vec4(dst.rgb * (1.0 - src.rgb) - src.rgb * src.rgb, 1.0);
}
"""

var failures := 0


func _check(label: String, want: Color, got: Color) -> void:
	var ok := absf(want.r - got.r) < 0.01 and absf(want.g - got.g) < 0.01 \
		and absf(want.b - got.b) < 0.01
	print("[blend-probe] %-28s want (%.3f %.3f %.3f)  got (%.3f %.3f %.3f)  %s"
		% [label, want.r, want.g, want.b, got.r, got.g, got.b, "ok" if ok else "MISMATCH"])
	if not ok:
		failures += 1


func _init() -> void:
	var vp := SubViewport.new()
	vp.size = Vector2i(8, 8)
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)

	var white := ImageTexture.create_from_image(
		Image.create_empty(8, 8, false, Image.FORMAT_RGBA8))
	var img := white.get_image()
	img.fill(Color(1, 1, 1, 1))
	white.update(img)

	var mat := ShaderMaterial.new()
	var sh := Shader.new()
	sh.code = READ_BACK
	mat.shader = sh

	# Underneath: a flat colour, drawn normally.
	var under := RenderingServer.canvas_item_create()
	RenderingServer.canvas_item_set_parent(under, vp.find_world_2d().canvas)
	RenderingServer.canvas_item_add_texture_rect(
		under, Rect2(0, 0, 8, 8), white.get_rid(), false, Color(0.8, 0.6, 0.4, 1.0))

	# The copy has to sit between them, on its own item, or the reader sees
	# whatever was in the back buffer last frame.
	var copier := RenderingServer.canvas_item_create()
	RenderingServer.canvas_item_set_parent(copier, vp.find_world_2d().canvas)
	RenderingServer.canvas_item_set_draw_index(copier, 1)
	RenderingServer.canvas_item_set_copy_to_backbuffer(copier, true, Rect2(0, 0, 8, 8))

	# On top: reads the back buffer and blends by hand.
	var over := RenderingServer.canvas_item_create()
	RenderingServer.canvas_item_set_parent(over, vp.find_world_2d().canvas)
	RenderingServer.canvas_item_set_draw_index(over, 2)
	RenderingServer.canvas_item_set_material(over, mat.get_rid())
	RenderingServer.canvas_item_add_texture_rect(
		over, Rect2(0, 0, 8, 8), white.get_rid(), false, Color(0.25, 0.5, 0.0, 1.0))

	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw

	var got: Color = vp.get_texture().get_image().get_pixel(4, 4)

	# dst*(1-src) - src*src, per channel.
	var dst := Vector3(0.8, 0.6, 0.4)
	var src := Vector3(0.25, 0.5, 0.0)
	var want := Color(
		maxf(dst.x * (1.0 - src.x) - src.x * src.x, 0.0),
		maxf(dst.y * (1.0 - src.y) - src.y * src.y, 0.0),
		maxf(dst.z * (1.0 - src.z) - src.z * src.z, 0.0))

	_check("reverse subtract by hand", want, got)

	print("[blend-probe] %s" % ["PASS" if failures == 0 else "FAIL"])
	quit(0 if failures == 0 else 1)
