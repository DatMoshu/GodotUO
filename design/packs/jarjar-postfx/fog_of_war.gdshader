// Radial torch darkness with a soft light cone (ADR-0023).
// Approx: line-of-sight is NOT raymarched — walls, the occlusion map, spell/world lights and 3D height are all ignored, so light leaks through walls; JAR's 5-band shadow march plus height G-buffer would be needed for parity.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.85;
uniform float light_range : hint_range(0.05, 1.0, 0.01) = 0.6;
uniform float cone_angle : hint_range(0.0, 360.0, 1.0) = 360.0;
// Screen degrees, 0 = screen up; the preset binds this to the "facing" state
// so the cone follows the player. No new uniform: preset compat kept.
uniform float cone_direction : hint_range(0.0, 360.0, 1.0) = 0.0;
uniform float softness : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float min_visibility : hint_range(0.0, 1.0, 0.01) = 0.05;
uniform float center_x : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float center_y : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float ambient : hint_range(0.0, 1.0, 0.01) = 0.15;
uniform float ambient_radius : hint_range(0.05, 1.0, 0.01) = 0.3;
uniform vec4 fog_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 light_color : source_color = vec4(1.0, 0.95, 0.85, 1.0);

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 center = vec2(center_x, center_y);
	vec2 asp = vec2(SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x, 1.0);
	float dist = length((SCREEN_UV - center) * asp);
	float cone_vis = 1.0;
	if (cone_angle < 359.0) {
		float rad = cone_direction * 0.0174533;
		vec2 ldir = vec2(sin(rad), -cos(rad)) * asp;
		ldir = normalize(ldir);
		vec2 topx = (SCREEN_UV - center) * asp;
		float dd = max(length(topx), 0.0001);
		float co = dot(ldir, topx / dd);
		float half = cone_angle * 0.5 * 0.0174533;
		float cone_cos = cos(half);
		float soft_cos = cos(half * (1.0 - softness));
		cone_vis = clamp((co - cone_cos) / max(soft_cos - cone_cos, 0.0001), 0.0, 1.0);
	}
	float nd = dist / max(light_range, 0.01);
	float torch = clamp(1.0 - nd, 0.0, 1.0) * (1.0 - smoothstep(0.8, 1.0, nd));
	float amb = exp(-dist * 3.0 / max(ambient_radius, 0.01)) * ambient;
	float combined = clamp(torch * cone_vis + amb, 0.0, 1.0);
	combined = max(combined, min_visibility);
	float vis = mix(1.0, combined, intensity);
	float tinfl = clamp(torch * cone_vis, 0.0, 1.0);
	vec3 lit = c.rgb * mix(vec3(1.0), light_color.rgb, tinfl * 0.5);
	COLOR = vec4(mix(fog_color.rgb, lit, vis), c.a);
}
