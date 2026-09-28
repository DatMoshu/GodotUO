// CRT (ADR-0023): scanlines, an aperture-grille mask and a soft edge falloff,
// at the art's own pixel rows. No curvature by default (a curved screen
// resamples the art); raise `curvature` to taste.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float scanlines : hint_range(0.0, 1.0, 0.01) = 0.35;
uniform float line_pixels : hint_range(1.0, 4.0, 1.0) = 2.0;
uniform float mask : hint_range(0.0, 1.0, 0.01) = 0.2;
uniform float curvature : hint_range(0.0, 0.3, 0.01) = 0.0;
uniform float edge : hint_range(0.0, 1.0, 0.01) = 0.25;
uniform float brightness : hint_range(0.8, 1.6, 0.01) = 1.15;

void fragment() {
	vec2 uv = SCREEN_UV;
	if (curvature > 0.0) {
		vec2 cc = uv * 2.0 - 1.0;
		cc *= 1.0 + curvature * dot(cc.yx, cc.yx) * 0.25;
		uv = cc * 0.5 + 0.5;
	}
	vec4 c = texture(source, uv);
	vec2 px = uv / SCREEN_PIXEL_SIZE;
	float line = mod(floor(px.y), line_pixels) < line_pixels * 0.5 ? 1.0 : 1.0 - scanlines;
	int col = int(mod(floor(px.x), 3.0));
	vec3 m = col == 0 ? vec3(1.0, 1.0 - mask, 1.0 - mask) : col == 1 ? vec3(1.0 - mask, 1.0, 1.0 - mask)
		: vec3(1.0 - mask, 1.0 - mask, 1.0);
	vec2 e = abs(uv - 0.5) * 2.0;
	float fall = 1.0 - edge * pow(max(e.x, e.y), 6.0);
	vec3 outc = c.rgb * line * m * brightness * fall;
	bool outside = uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0;
	COLOR = outside ? vec4(0.0, 0.0, 0.0, c.a) : vec4(clamp(outc, 0.0, 1.0), c.a);
}
