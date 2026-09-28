// Posterize: fewer colour levels per channel (ADR-0023 starter effect).
// Pass convention: read `source` (the pass before), write with blend_disabled,
// keep alpha. Every knob is a hinted uniform; the effects menu makes the slider.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float levels : hint_range(2.0, 32.0, 1.0) = 6.0;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 p = floor(c.rgb * (levels - 1.0) + 0.5) / (levels - 1.0);
	COLOR = vec4(mix(c.rgb, p, strength), c.a);
}
