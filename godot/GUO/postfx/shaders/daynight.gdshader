// Day/night cycle: time-of-day tint with brightness and shadow darkening (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float cycle : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.85;
uniform float brightness : hint_range(0.5, 1.5, 0.01) = 1.0;
uniform float warmth : hint_range(-1.0, 1.0, 0.01) = 0.0;
uniform float shadow_intensity : hint_range(0.0, 1.0, 0.01) = 0.3;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 mn = vec3(0.65, 0.65, 0.8);
	vec3 sr = vec3(0.9, 0.75, 0.55);
	vec3 nn = vec3(1.0, 1.0, 0.97);
	vec3 ss = vec3(0.85, 0.65, 0.45);
	vec3 dc;
	if (cycle < 0.25) {
		dc = mix(mn, sr, cycle / 0.25);
	} else if (cycle < 0.5) {
		dc = mix(sr, nn, (cycle - 0.25) / 0.25);
	} else if (cycle < 0.75) {
		dc = mix(nn, ss, (cycle - 0.5) / 0.25);
	} else {
		dc = mix(ss, mn, (cycle - 0.75) / 0.25);
	}
	vec3 warm = vec3(1.05, 0.95, 0.85);
	vec3 cool = vec3(0.9, 0.95, 1.1);
	vec3 wt = warmth >= 0.0 ? mix(vec3(1.0), warm, warmth) : mix(vec3(1.0), cool, -warmth);
	vec3 tinted = dc * brightness * wt;
	vec3 o = c.rgb * mix(vec3(1.0), tinted, intensity);
	float sun_arc = sin(cycle * 3.14159);
	o *= 1.0 - shadow_intensity * (1.0 - clamp(sun_arc, 0.0, 1.0)) * 0.5;
	COLOR = vec4(o, c.a);
}
