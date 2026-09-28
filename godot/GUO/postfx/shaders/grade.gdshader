// Colour grading by numbers: exposure, contrast, saturation, temperature,
// tint, and shadow/highlight lift and gain (ADR-0023). For a hand-made look,
// the "lut" pass takes a LUT image instead.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float exposure : hint_range(-2.0, 2.0, 0.01) = 0.0;
uniform float contrast : hint_range(0.5, 2.0, 0.01) = 1.0;
uniform float saturation : hint_range(0.0, 2.0, 0.01) = 1.0;
uniform float temperature : hint_range(-1.0, 1.0, 0.01) = 0.0;   // cool .. warm
uniform float tint : hint_range(-1.0, 1.0, 0.01) = 0.0;          // green .. magenta
uniform float lift : hint_range(-0.2, 0.2, 0.005) = 0.0;
uniform float gain : hint_range(0.5, 1.5, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 g = c.rgb * exp2(exposure);
	g += vec3(temperature * 0.08, tint * -0.06, -temperature * 0.08);
	g = (g - 0.5) * contrast + 0.5;
	float l = dot(g, vec3(0.299, 0.587, 0.114));
	g = mix(vec3(l), g, saturation);
	g = g * gain + lift * (1.0 - g);
	COLOR = vec4(clamp(g, 0.0, 1.0), c.a);
}
