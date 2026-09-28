// Cel: shade in flat bands of brightness, keeping each pixel's hue (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float bands : hint_range(2.0, 8.0, 1.0) = 4.0;
uniform float saturation : hint_range(0.0, 2.0, 0.01) = 1.15;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	float q = (floor(l * bands) + 0.5) / bands;
	// Move each pixel to its band by a brightness shift, not a scale: scaling
	// by q / l blows near-black pixels up into loud colour.
	vec3 shaded = clamp(c.rgb + (q - l), 0.0, 1.0);
	float g = dot(shaded, vec3(0.299, 0.587, 0.114));
	shaded = clamp(mix(vec3(g), shaded, saturation), 0.0, 1.0);
	COLOR = vec4(mix(c.rgb, shaded, strength), c.a);
}
