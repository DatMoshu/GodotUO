// HD heat shimmer, masked (ADR-0023, HD light Phase 1): heat.gdshader's wobble, gated by the light buffer.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform float amount : hint_range(0.0, 0.05, 0.001) = 0.005;
uniform float speed : hint_range(0.0, 10.0, 0.1) = 2.4;
uniform float threshold : hint_range(0.0, 2.0, 0.01) = 1.0;
uniform float rise : hint_range(0.0, 0.1, 0.001) = 0.02;

float heat_mask(vec2 uv) {
	vec3 lt = texture(light_tex, uv).rgb;
	float l = max(max(lt.r, lt.g), lt.b);
	return smoothstep(threshold, threshold + 0.35, l);
}

void fragment() {
	// The mask is read BELOW the pixel so the column ABOVE a torch or lava
	// pool shimmers, not the flame itself.
	float m = heat_mask(SCREEN_UV + vec2(0.0, rise));
	vec2 uv = SCREEN_UV;
	uv.x += sin(uv.y * 34.0 + TIME * speed + float(m) * 2.0) * amount * m;
	uv.y += cos(uv.x * 28.0 - TIME * speed * 1.3) * amount * 0.4 * m;
	vec4 c = texture(source, uv);
	COLOR = vec4(c.rgb, c.a);
}
