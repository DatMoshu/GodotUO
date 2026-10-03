// The client's own gump art over the painting (buttons, fields, frames):
// xBR upscaled like the painting, dimmed with it, and a gold glow for the
// pad's focus. GUO, BSD-2-Clause. Target: canvas_item, any renderer.
shader_type canvas_item;
// Pixel art is never filtered: this shader declares no sampler of its own, it reads the
// node's TEXTURE, so nearest is set on the nodes (TextureFilter = Nearest on the pregame's
// layers and on every GumpProp). Any sampler added here must say filter_nearest.

#include "res://assets/pregame/shaders/xbr.gdshaderinc"

uniform float glow = 0.0;
uniform vec3 glow_color : source_color = vec3(0.878, 0.690, 0.314); // Gold #e0b050
uniform float dim = 1.0;
uniform bool upscale = true;

void fragment() {
	vec4 c = upscale ? xbr_sample(TEXTURE, UV, TEXTURE_PIXEL_SIZE) : texture(TEXTURE, UV);
	c *= COLOR;

	if (glow > 0.0) {
		// A soft outline from the art's own alpha, two texels out.
		float a = 0.0;
		for (int x = -2; x <= 2; x++) {
			for (int y = -2; y <= 2; y++) {
				a = max(a, texture(TEXTURE, UV + vec2(float(x), float(y)) * TEXTURE_PIXEL_SIZE).a * (1.0 - 0.18 * float(abs(x) + abs(y))));
			}
		}
		vec4 g = vec4(glow_color, a * 0.85 * glow);
		c.rgb = mix(c.rgb, c.rgb + glow_color * 0.18, glow * c.a);
		c = mix(g, c, c.a);
	}

	c.rgb *= dim;
	COLOR = c;
}
