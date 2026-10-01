// The boot splash's sigil (SplashIntro.cs): its fade, and one soft band of
// light that crosses the gold. The sigil is a painted image, not pixel art,
// so it is sampled smoothly, with mipmaps (AGENTS.md rule 7 does not apply).
shader_type canvas_item;

uniform sampler2D sigil : filter_linear_mipmap, repeat_disable;
uniform float alpha = 1.0;     // 0..1, the sigil's fade
uniform float sweep = -1.0;    // the band's position along the diagonal, -0.3..1.3; off outside that

void fragment() {
	vec4 c = texture(sigil, UV);
	// Only the gold catches the light: warm pixels, not the silver or the knotwork.
	float gold = clamp((c.r - c.b) * 2.4, 0.0, 1.0);
	float d = (UV.x * 0.8 + UV.y * 0.6) - (sweep * 1.4 - 0.2);
	float band = exp(-d * d / 0.012);
	vec3 rgb = c.rgb + band * gold * vec3(1.0, 0.86, 0.62) * 0.95;
	COLOR = vec4(rgb, c.a * alpha);
}
