# Can a canvas item carry a per-vertex value beyond position, UV and colour?
#
# ClassicUO's stretched land is lit per vertex: IsometricWorld.fx runs
# get_light(IN.Normal), a scalar in [0.5, 1], and multiplies the tile by it.
# A canvas quad has no normal, and the batcher's three packed bytes already
# spend R+G on the hue index, B on the mode and A on alpha -- there is no
# channel left. Without a fourth per-vertex value, terrain shading is either
# gone or quantised into the spare bits of the hue index.
#
# canvas_item_add_triangle_array has no such slot. canvas_item_add_mesh takes
# an ArrayMesh, which does: ARRAY_CUSTOM0. Whether a canvas_item shader can
# actually READ CUSTOM0 from a mesh drawn this way is undocumented for the 2D
# renderer, and Godot 4.7.2 is past the assistant's knowledge cutoff, so it is
# measured here rather than assumed.
#
# The light value is a pure function of the normal and one uniform, so the
# per-vertex part can be computed on the CPU at mesh build time -- ONE float
# is all that has to reach the shader. RGBA8_UNORM is therefore enough, and
# this checks its precision too: 8 bits over the [0.5, 1] range is 1/512,
# well under a step of the 8-bit output.
#
# Run: godot-console --path godot/GUO --script res://dev/mesh_probe.gd
# (NOT --headless: the dummy renderer never produces a frame.)

extends SceneTree

const READ_CUSTOM := """
shader_type canvas_item;
render_mode unshaded;

varying float light;

void vertex() {
	light = CUSTOM0.r;
}

void fragment() {
	// Green carries the vertex colour so the ordinary channel is checked in
	// the same draw; red carries what CUSTOM0 delivered.
	COLOR = vec4(light, COLOR.g, 0.0, 1.0);
}
"""

var failures := 0


func _check(label: String, want: float, got: float) -> void:
	# One 8-bit step. The value crosses the vertex format as a normalised byte
	# and comes back through an 8-bit render target.
	var ok := absf(want - got) <= 1.5 / 255.0
	print("[mesh-probe] %-34s want %.4f  got %.4f  %s"
		% [label, want, got, "ok" if ok else "MISMATCH"])
	if not ok:
		failures += 1


func _build_mesh(light_tl: float, light_br: float, vertex_green: float) -> ArrayMesh:
	# One quad, 8x8, as two triangles. CUSTOM0.r ramps from light_tl at the
	# top-left corner to light_br at the bottom-right, so a shader that simply
	# dropped CUSTOM0 (or clamped it to a constant) reads differently at the
	# two ends rather than looking plausible at one.
	var corners := [Vector2(0, 0), Vector2(8, 0), Vector2(0, 8), Vector2(8, 8)]
	var lights := [light_tl, (light_tl + light_br) * 0.5,
			(light_tl + light_br) * 0.5, light_br]

	var verts := PackedVector2Array()
	var colors := PackedColorArray()
	var uvs := PackedVector2Array()
	var custom := PackedByteArray()

	for i in [0, 1, 2, 1, 3, 2]:
		verts.append(corners[i])
		colors.append(Color(0.0, vertex_green, 0.0, 1.0))
		uvs.append(Vector2(0, 0))
		# ARRAY_CUSTOM_RGBA8_UNORM: four bytes per vertex, only R used.
		custom.append(int(round(lights[i] * 255.0)))
		custom.append(0)
		custom.append(0)
		custom.append(255)

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_COLOR] = colors
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_CUSTOM0] = custom

	var flags := Mesh.ARRAY_CUSTOM_RGBA8_UNORM << Mesh.ARRAY_FORMAT_CUSTOM0_SHIFT

	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(
		Mesh.PRIMITIVE_TRIANGLES, arrays, [], {}, flags)

	return mesh


func _init() -> void:
	var vp := SubViewport.new()
	vp.size = Vector2i(8, 8)
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	vp.transparent_bg = false
	root.add_child(vp)

	var mat := ShaderMaterial.new()
	var sh := Shader.new()
	sh.code = READ_CUSTOM
	mat.shader = sh

	# The two ends of the range get_light can produce: 0.5 straight down from
	# the light, 1.0 facing it.
	var light_tl := 0.5
	var light_br := 1.0
	var vertex_green := 0.75

	var item := RenderingServer.canvas_item_create()
	RenderingServer.canvas_item_set_parent(item, vp.find_world_2d().canvas)
	RenderingServer.canvas_item_set_material(item, mat.get_rid())
	RenderingServer.canvas_item_set_default_texture_filter(
		item, RenderingServer.CANVAS_ITEM_TEXTURE_FILTER_NEAREST)

	var mesh := _build_mesh(light_tl, light_br, vertex_green)
	RenderingServer.canvas_item_add_mesh(item, mesh.get_rid())

	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw

	var img := vp.get_texture().get_image()

	# Corners, inset by half a pixel's worth, where the interpolant is nearly
	# the corner value.
	var tl := img.get_pixel(0, 0)
	var br := img.get_pixel(7, 7)

	_check("CUSTOM0 at the light end", light_tl + (light_br - light_tl) * (0.5 / 8.0), tl.r)
	_check("CUSTOM0 at the dark end", light_tl + (light_br - light_tl) * (7.5 / 8.0), br.r)
	_check("vertex colour in the same draw", vertex_green, tl.g)

	# Free the item before the mesh goes out of scope, or teardown prints
	# "Parameter mesh is null" and a leaked-RID warning over the result.
	RenderingServer.free_rid(item)

	print("[mesh-probe] %s" % ["PASS" if failures == 0 else "FAIL"])
	quit(0 if failures == 0 else 1)
