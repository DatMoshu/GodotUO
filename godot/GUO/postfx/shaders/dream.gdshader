// Dream: soft double-image wobble with a bright hazy falloff (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.5;

void fragment() {
	vec2 uv = SCREEN_UV;
	uv.x += sin(uv.y * 10.0 + TIME * 2.0) * 0.02 * intensity;
	vec4 c1 = texture(source, uv);
	vec4 c2 = texture(source, uv + vec2(0.01, 0.005) * intensity);
	vec4 c = texture(source, SCREEN_UV);
	vec3 d = mix(c1.rgb, c2.rgb, 0.3) + intensity * 0.1;
	vec2 ct = SCREEN_UV - 0.5;
	d *= 1.0 - dot(ct, ct) * intensity;
	COLOR = vec4(d, c.a);
}
