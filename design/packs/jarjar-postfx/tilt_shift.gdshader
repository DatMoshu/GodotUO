// Tilt-shift miniature with a horizontal focus band (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float focus_position : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float focus_width : hint_range(0.05, 1.0, 0.01) = 0.2;
uniform float blur_amount : hint_range(1.0, 10.0, 0.1) = 3.0;
uniform float gradient_size : hint_range(0.05, 0.5, 0.01) = 0.15;
uniform float saturation_boost : hint_range(0.0, 2.0, 0.01) = 1.1;
uniform float contrast_boost : hint_range(0.5, 2.0, 0.01) = 1.1;

void fragment() {
	vec2 uv = SCREEN_UV;
	float dist = abs(uv.y - focus_position);
	float half_width = focus_width * 0.5;
	float blur_factor = clamp((dist - half_width) / max(gradient_size, 0.001), 0.0, 1.0);
	vec4 c = texture(source, uv);
	vec3 rgb = c.rgb;
	if (blur_factor > 0.01) {
		vec2 px = SCREEN_PIXEL_SIZE * blur_factor * blur_amount;
		vec3 acc = rgb;
		acc += texture(source, uv + vec2(px.x, 0.0)).rgb;
		acc += texture(source, uv - vec2(px.x, 0.0)).rgb;
		acc += texture(source, uv + vec2(0.0, px.y)).rgb;
		acc += texture(source, uv - vec2(0.0, px.y)).rgb;
		rgb = mix(rgb, acc * 0.2, blur_factor);
	}
	rgb = (rgb - 0.5) * contrast_boost + 0.5;
	float luma = dot(rgb, vec3(0.299, 0.587, 0.114));
	rgb = mix(vec3(luma), rgb, saturation_boost);
	COLOR = vec4(clamp(rgb, 0.0, 1.0), c.a);
}
