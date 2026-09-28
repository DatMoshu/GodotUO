// Object outline (ADR-0023): ink around each object's own silhouette, from the
// object-id buffer, not from colour. Characters, items, spell effects and
// statics each have their own id; land is background. So an edge is drawn
// only where one object ends, never across the texture inside it, and a
// character standing on a same-coloured floor is still outlined.
// postfx: heavy   (8 or more taps a pixel; half resolution on the mobile tier, ADR-0023)
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D id_tex : filter_nearest;
uniform int edges : hint_enum("Around each object", "Only against the ground") = 0;
uniform float thickness : hint_range(1.0, 3.0, 1.0) = 1.0;
uniform vec4 ink : source_color = vec4(0.06, 0.05, 0.04, 1.0);
uniform float strength : hint_range(0.0, 1.0, 0.01) = 0.9;

vec3 id_at(vec2 uv) { return texture(id_tex, uv).rgb; }
bool background(vec3 id) { return dot(id, vec3(1.0)) < 0.5 / 255.0; }

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 me = id_at(SCREEN_UV);
	float edge = 0.0;
	if (!background(me)) {
		vec2 px = SCREEN_PIXEL_SIZE * thickness;
		vec2 dirs[8] = vec2[8](vec2(1, 0), vec2(-1, 0), vec2(0, 1), vec2(0, -1),
			vec2(1, 1), vec2(-1, 1), vec2(1, -1), vec2(-1, -1));
		for (int i = 0; i < 8; i++) {
			vec3 other = id_at(SCREEN_UV + dirs[i] * px);
			bool differs = any(greaterThan(abs(other - me), vec3(0.5 / 255.0)));
			if (edges == 1 ? background(other) : differs) {
				edge = 1.0;
				break;
			}
		}
	}
	float k = edge * strength * ink.a;
	COLOR = vec4(mix(c.rgb, ink.rgb, k), c.a);
}
