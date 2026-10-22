shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, skip_vertex_transform, shadows_disabled, fog_disabled;

#include "res://src/Render/shaders/world_depth_key.gdshaderinc"

// Synthetic exhaustive hardware depth test, NOT a client rendering mode.
// Each pixel tests a different adjacent pair. Two overlapping instances/pages
// supply its far and near values. R identifies the winner; G identifies the
// 4M-key batch and B the column. CPU readback verifies the range stamps too.
uniform int first_key = 0;
uniform int encoding = 0;
uniform int width = 2048;
uniform sampler2D page : filter_nearest, repeat_disable;
varying flat uint increment;

vec3 srgb_to_linear(vec3 c) {
	return mix(pow((c + vec3(0.055)) * (1.0 / 1.055), vec3(2.4)), c * (1.0 / 12.92), lessThan(c, vec3(0.04045)));
}

void vertex() {
	increment = uint(MODEL_MATRIX[3].z);
	POSITION = vec4(VERTEX.xy, 0.5, 1.0);
}

void fragment() {
	uint pair = uint(first_key) + uint(FRAGCOORD.y) * uint(width) + uint(FRAGCOORD.x);
	if (pair >= 268435455u) {
		discard;
	}
	uint key = pair + increment;
	// Alternative B deliberately leaves the low 9 bits to submission order.
	DEPTH = encoding == 0 ? world_depth_key(key) : float((key >> 9u) + 1u) * (1.0 / 524289.0);
	float near_marker = texture(page, vec2(0.25 + float(increment) * 0.5, 0.5)).g;
	ALBEDO = srgb_to_linear(vec3(near_marker, float((pair >> 22u) * 4u) / 255.0, float(pair & 255u) / 255.0));
}
