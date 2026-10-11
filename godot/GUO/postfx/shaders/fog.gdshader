// Volumetric-style fog: drifting value-noise bands, denser low on screen (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.25;
uniform vec4 fog_color : source_color = vec4(0.85, 0.9, 0.95, 1.0);
uniform float density : hint_range(0.0, 1.0, 0.01) = 0.2;
uniform float scale : hint_range(1.0, 16.0, 0.5) = 8.0;
uniform float speed : hint_range(0.0, 0.2, 0.005) = 0.04;
uniform float patchiness : hint_range(0.0, 1.0, 0.01) = 0.3;
uniform float fog_start : hint_range(0.0, 1.0, 0.01) = 0.4;
uniform float fog_end : hint_range(0.0, 1.0, 0.01) = 1.0;
uniform float light_reaction : hint_range(0.0, 1.0, 0.01) = 0.6;

// Stable lattice hash (no sin precision pitfalls) + smoothstep-faded value noise.
float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 uv = SCREEN_UV;
	float band = smoothstep(fog_start, fog_end, 1.0 - uv.y);
	vec2 asp = vec2(SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x, 1.0);
	vec2 fuv = uv * asp;
	vec2 drift = vec2(TIME * speed, TIME * speed * 0.6);
	float n1 = vnoise(fuv * scale + drift);
	float n2 = vnoise(fuv * scale * 2.1 + drift * 1.15 + 13.7);
	float cloud = n1 * 0.6 + n2 * 0.4;
	float threshold = 1.0 - patchiness;
	float fog_density = smoothstep(threshold * 0.5, threshold * 0.5 + 0.4, cloud) * density;
	float fog_amount = clamp(band * 0.3 + fog_density, 0.0, 1.0) * intensity;
	float scene_lum = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	vec3 lit_fog = fog_color.rgb * (1.0 + scene_lum * light_reaction * 0.5);
	float edge_soft = clamp(fog_density * 3.0, 0.0, 1.0);
	vec3 fog_blend = mix(fog_color.rgb * 0.85, lit_fog, edge_soft);
	vec3 outc = mix(c.rgb, fog_blend, clamp(fog_amount, 0.0, 1.0));
	COLOR = vec4(outc, c.a);
}
