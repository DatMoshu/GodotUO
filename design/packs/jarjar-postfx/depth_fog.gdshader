// Depth fog: distance haze from a luminance proxy for depth (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform vec4 fog_color : source_color = vec4(0.5, 0.6, 0.7, 1.0);
uniform float start : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform float density : hint_range(0.1, 3.0, 0.05) = 1.0;
uniform float light_reaction : hint_range(0.0, 1.0, 0.01) = 0.5;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float lum = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	float d = smoothstep(start, 1.0, 1.0 - lum);
	d = pow(d, 1.0 / max(density, 0.1));
	float amt = d * intensity;
	vec3 fcol = fog_color.rgb + fog_color.rgb * lum * light_reaction * 0.4;
	vec3 outc = mix(c.rgb, fcol, clamp(amt, 0.0, 1.0));
	COLOR = vec4(outc, c.a);
}
