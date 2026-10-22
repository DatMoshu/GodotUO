shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, skip_vertex_transform, shadows_disabled, fog_disabled;

// The opaque pass of the world depth pass (ADR-0007, GPU-01/02): persistent
// chunk statics and queued sprites whose key is unique this frame. Order
// among them is the depth test's alone, so their buffers may draw in any
// order. Everything is in world_sprite.gdshaderinc.

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
	// Flat, explicit depth avoids rasterizer interpolation rounding of adjacent
	// representable values. Unconditional: every surviving fragment writes it.
	DEPTH = fragment_depth;
}
