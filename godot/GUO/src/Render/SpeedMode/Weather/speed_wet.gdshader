// GUO wet floors and puddles, ADR-0048. No client-art filtering or tile UVs.
shader_type canvas_item;
render_mode unshaded;
#include "res://src/Render/SpeedMode/Weather/speed_reflection.gdshaderinc"
uniform sampler2D scene_color : hint_screen_texture, filter_nearest, repeat_disable;
uniform sampler2D surface_buffer : filter_nearest, repeat_disable;
uniform sampler2D light_target : filter_linear, repeat_disable;
uniform vec2 target_size = vec2(1280.0, 720.0);
uniform vec2 surface_origin = vec2(0.0);
uniform float wetness : hint_range(0.0, 1.0) = 0.0;
uniform float rain_intensity : hint_range(0.0, 1.0) = 0.0;
uniform float seconds = 0.0;
// Four-tap geometric reflection; gated by the mode's Reflections option.
uniform bool reflections_enabled = false;
uniform float reflection_art_scale = 1.0;

float wet_hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}
float wet_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(wet_hash(i), wet_hash(i + vec2(1.0, 0.0)), f.x),
		mix(wet_hash(i + vec2(0.0, 1.0)), wet_hash(i + 1.0), f.x), f.y);
}
void fragment() {
	vec2 uv = SCREEN_UV;
	vec4 surface = texture(surface_buffer, uv);
	int data = int(round(surface.w));
	bool floor_pixel = (data & 15) == 1;
	bool water_pixel = (data & 15) == 6;
	bool covered = ((data / 16) & 1) != 0;
	vec3 color = texture(scene_color, uv).rgb;
	float alpha = 0.0;
	if (floor_pixel && !covered && wetness > 0.0) {
		vec2 world = surface.xy + surface_origin;
		float noise = wet_noise(world * 1.6) * 0.7 + wet_noise(world * 5.7) * 0.3;
		bool candidate = ((data / 16) & 2) != 0;
		float puddle = candidate ? smoothstep(1.0 - wetness * 0.7, 1.08 - wetness * 0.7, noise) : 0.0;
		vec3 light = max(texture(light_target, uv).rgb, vec3(0.0));
		// The light target already contains each lamp's reach and shadow. A
		// local gradient supplies an approximate direction toward brighter light;
		// this is a stylised wet glint, not a physically traced reflection.
		vec2 texel = 1.0 / target_size;
		vec3 dx = texture(light_target, uv + vec2(texel.x, 0.0)).rgb
			- texture(light_target, uv - vec2(texel.x, 0.0)).rgb;
		vec3 dy = texture(light_target, uv + vec2(0.0, texel.y)).rgb
			- texture(light_target, uv - vec2(0.0, texel.y)).rgb;
		vec3 luma = vec3(0.2126, 0.7152, 0.0722);
		vec2 direction = vec2(dot(dx, luma), dot(dy, luma));
		vec2 slope = vec2(sin(world.x * 1.7 + world.y * 2.3), cos(world.x * 2.1 - world.y * 1.3)) * 0.18;
		float sheen = pow(max(0.0, dot(normalize(vec3(slope, 1.0)), normalize(vec3(-direction * 8.0, 1.0)))), 64.0);
		color *= 1.0 - wetness * (0.18 + puddle * 0.08);
		color += light * wetness * (0.015 + sheen * 0.055 + puddle * 0.075);
		vec2 cell = floor(world * 2.0);
		float phase = fract(seconds * 1.7 + wet_hash(cell));
		vec2 local = fract(world * 2.0) - 0.5;
		float radius = length(local * vec2(1.0, 1.4));
		float ring = (1.0 - smoothstep(0.015, 0.045, abs(radius - phase * 0.55))) * (1.0 - phase);
		color += vec3(0.12, 0.15, 0.17) * ring * puddle * rain_intensity;
		if (reflections_enabled && wetness > 0.3 && puddle > 0.0) {
			vec4 reflection = speed_reflection(surface_buffer, scene_color, uv,
				target_size, surface.xyz, surface_origin, reflection_art_scale, seconds);
			color = mix(color, reflection.rgb, reflection.a * puddle * wetness * 0.22);
		}
		alpha = 1.0;
	}
	if (water_pixel && !covered && reflections_enabled) {
		vec4 reflection = speed_reflection(surface_buffer, scene_color, uv,
			target_size, surface.xyz, surface_origin, reflection_art_scale, seconds);
		color = mix(color, reflection.rgb, reflection.a * 0.35);
		alpha = reflection.a > 0.0 ? 1.0 : 0.0;
	}
	COLOR = vec4(color, alpha);
}
