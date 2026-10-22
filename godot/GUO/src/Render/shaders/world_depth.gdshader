shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, skip_vertex_transform, shadows_disabled, fog_disabled;

#include "res://src/Render/shaders/world_depth_key.gdshaderinc"

// The world depth pass (ADR-0007, amendment 2026-10-19): one quad per
// MultiMesh instance, placed in the world render target's own pixels and
// ordered by the GPU depth test on a key derived from CalculateDepthZ().
//
// The instance carries no real transform. WorldDepthPass packs it:
//   MODEL_MATRIX[0].x, [1].y  the quad's size in world pixels
//   MODEL_MATRIX[3].xy        its top-left in world pixels
//   MODEL_MATRIX[3].z         the depth key, 0..1, larger is nearer
//   INSTANCE_CUSTOM           the source rect, normalised: (x, y, w, h)
//
// Every step below is the canvas renderer's own for a texture rect
// (renderer_rd/shaders/canvas.glsl, Godot 4.7.2), operation for operation:
// the corner from the rect, the item transform and the screen transform as
// mat4 products, the UV from the source rect. A pixel centre exactly on a
// quad edge or on a texel boundary (zoom 1.25 and 2.5 put integer positions
// on halves all the time) is decided by these roundings, so they must be
// the same ones. The mesh's corner order and diagonal are the canvas's too.

// Raw: decoded from sRGB below, exactly.
uniform sampler2D page : filter_nearest, repeat_disable;
uniform vec2 view_x = vec2(1.0, 0.0);
uniform vec2 view_y = vec2(0.0, 1.0);
uniform vec2 view_origin = vec2(0.0);
// The canvas screen transform: scale 2 / size, offset -(size / 2) * scale,
// both rounded to float on the CPU as the canvas renderer does.
uniform vec4 screen = vec4(1.0, 1.0, 0.0, 0.0);
// Clip y sign; the depth probe finds the renderer's.
uniform float flip_y = 1.0;
// 1: clip z is the key (reverse Z, larger nearer); 0: 1 - key.
uniform float key_reversed = 1.0;
varying flat float fragment_depth;

void vertex() {
	vec2 vertex_base = VERTEX.xy;
	vec2 uv = INSTANCE_CUSTOM.xy + abs(INSTANCE_CUSTOM.zw) * vertex_base;
	vec2 vertex = MODEL_MATRIX[3].xy + abs(vec2(MODEL_MATRIX[0].x, MODEL_MATRIX[1].y)) * vertex_base;
	mat4 model_matrix = mat4(vec4(view_x, 0.0, 0.0), vec4(view_y, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(view_origin, 0.0, 1.0));
	vertex = (model_matrix * vec4(vertex, 0.0, 1.0)).xy;
	mat4 screen_transform = mat4(vec4(screen.x, 0.0, 0.0, 0.0), vec4(0.0, screen.y, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(screen.zw, 0.0, 1.0));
	vec2 ndc = (screen_transform * vec4(vertex, 0.0, 1.0)).xy;
	ndc.y *= -flip_y;
	UV = uv;
	float key = MODEL_MATRIX[3].z;
	// The third row is spare: high 19 bits, low 9 bits, integer-key flag.
	// Never convert the full uint to float numerically before the bit cast.
	if (MODEL_MATRIX[2].z > 0.5) {
		uint full_key = (uint(MODEL_MATRIX[0].z) << 9u) | uint(MODEL_MATRIX[1].z);
		key = world_depth_key(full_key);
	}
	fragment_depth = key_reversed > 0.5 ? key : 1.0 - key;
	POSITION = vec4(ndc, fragment_depth, 1.0);
}

// sRGB to linear, exactly, so the renderer's encode on output gives the
// texel's bytes back (the canvas writes them untouched).
vec3 srgb_to_linear(vec3 c) {
	return mix(pow((c + vec3(0.055)) * (1.0 / 1.055), vec3(2.4)), c * (1.0 / 12.92), lessThan(c, vec3(0.04045)));
}

void fragment() {
	vec4 c = texture(page, UV);
	if (c.a < 0.5) {
		discard;
	}
	ALBEDO = srgb_to_linear(c.rgb);
	// Flat, explicit depth avoids rasterizer interpolation rounding of adjacent
	// representable values. This includes the raw-key path used by step 1.
	DEPTH = fragment_depth;
}
