// Distance haze from estimated depth (ADR-0023).
// Approx: GUO has no G-buffer, so depth is guessed from screen-Y plus luminance (darker = nearer); JAR's Sobel-edge DepthEstimate, height map (s2) and occlusion-reactive DepthFog are all dropped.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform vec4 fog_color : source_color = vec4(0.5, 0.6, 0.7, 1.0);
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float density : hint_range(0.1, 4.0, 0.01) = 1.0;
uniform float start : hint_range(0.0, 0.9, 0.01) = 0.0;
uniform float height_scale : hint_range(0.0, 1.0, 0.01) = 0.15;
uniform float light_reaction : hint_range(0.0, 1.0, 0.01) = 0.5;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float luma = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	float iso = 1.0 - SCREEN_UV.y;
	float d = clamp(iso * (1.0 - height_scale) + luma * height_scale, 0.0, 1.0);
	float f = smoothstep(start, 1.0, d);
	f = pow(f, 1.0 / max(density, 0.1));
	float amt = clamp(f * intensity, 0.0, 1.0);
	vec3 fog = fog_color.rgb + fog_color.rgb * luma * light_reaction * 0.4;
	COLOR = vec4(mix(c.rgb, fog, amt), c.a);
}
