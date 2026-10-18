// Heat distortion shimmer (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float amount : hint_range(0.0, 0.05, 0.001) = 0.01;
uniform float speed : hint_range(0.0, 10.0, 0.1) = 2.0;

void fragment() {
	vec2 uv = SCREEN_UV;
	uv.x += sin(uv.y * 20.0 + TIME * speed) * amount;
	vec4 c = texture(source, uv);
	COLOR = vec4(c.rgb, c.a);
}
