shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, skip_vertex_transform, shadows_disabled, fog_disabled, blend_premul_alpha;

// The ordered pass of the world depth pass (ADR-0007, GPU-02): sprites that
// blend, and every later holder of a key another sprite already has this
// frame. Writing ALPHA puts the material in the renderer's transparent pass,
// which draws after the opaque pass and orders its materials by
// render_priority; WorldDepthPass gives each segment the next priority in
// submission order. Depth is tested and written, as upstream draws its
// translucent objects (DepthStencilState.Default). Premultiplied, under
// (One, InverseSourceAlpha), like uo_hue.gdshader.

#include "res://src/Render/shaders/world_sprite.gdshaderinc"

void vertex() {
	vec4 position;
	vec2 uv;
	vec4 hue;
	float depth;
	world_sprite_vertex(VERTEX.xy, MODEL_MATRIX, COLOR, INSTANCE_CUSTOM, position, uv, hue, depth);
	POSITION = position;
	UV = uv;
	packed_hue = hue;
	// Upstream's batcher gives every sprite the flat normal (0, 0, 1).
	land_normal = vec3(0.0, 0.0, 1.0);
	fragment_depth = depth;
}

void fragment() {
	vec4 c = world_sprite_shade(UV, SCREEN_UV, VIEWPORT_SIZE);
	ALBEDO = c.rgb;
	ALPHA = c.a;
	DEPTH = fragment_depth;
}
