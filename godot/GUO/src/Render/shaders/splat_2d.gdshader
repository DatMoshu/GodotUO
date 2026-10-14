// One gaussian splat as a screen-aligned quad with a radial falloff.
//
// The batcher writes per-vertex colour (DC term mapped the standard way:
// rgb = 0.5 + 0.2829 * f_dc) straight (not premultiplied), and UVs spanning
// 0..1 across the quad. No texture is sampled, so there is nothing to filter:
// nearest-neighbour (project rule 7) holds trivially.
//
// blend_mix (normal alpha blending) like the reference renderer, whose soft
// disc texture this falloff matches exactly (zero at the quad edge).
//
// Victim hue (SplatObject.Draw): SplatPlacement carries no victim graphic or
// hue at bake time (see SplatStage.cs), so the hue rides here as uniforms
// instead of baked vertex colours. Convention follows
// ShaderHueTranslator.GetHueVector: splat_hue is the palette row (0 = unhued),
// splat_hue_mode 0 none / 1 hued / 2 partial-hued. The mapping follows
// uo_hue_core.gdshaderinc get_rgb (red channel as the palette index).
//
// Circle of transparency: same uniforms and branch shape as uo_hue_core. The
// flag rides in splat_cot (per-draw, not baked: baking it would rebake the
// whole level whenever the player moves).

shader_type canvas_item;
// NOTE: canvas_item has no cull_disabled mode; the quad winding below matches
// MeshLayer.WriteTriangles (0 1 2, 1 3 2), whose meshes render.
render_mode blend_mix, unshaded;

uniform sampler2D hue_texture : filter_nearest, repeat_disable;
uniform float splat_hue = 0.0;
uniform float splat_hue_mode = 0.0;
uniform float circle_of_transparency_radius = 0.0;
uniform vec2 circle_of_transparency_center = vec2(0.0, 0.0);
uniform float splat_cot = 0.0;

const float HUE_ROWS = 1024.0;
const float HUE_COLUMNS = 16.0;
const float HUE_WIDTH = 32.0;
const float HUES_PER_TEXTURE = HUE_ROWS * HUE_COLUMNS;

vec3 splat_get_rgb(float gray, float hue) {
	float half_pixel_x = (1.0 / (HUE_COLUMNS * HUE_WIDTH)) * 0.5;
	float hue_column_width = 1.0 / HUE_COLUMNS;
	float hue_start = fract(hue / HUE_COLUMNS);

	float x_pos = hue_start + gray / HUE_COLUMNS;
	x_pos = clamp(x_pos, hue_start + half_pixel_x,
			hue_start + hue_column_width - half_pixel_x);

	float y_pos = mod(hue, HUES_PER_TEXTURE) / (HUES_PER_TEXTURE - 1.0);

	return texture(hue_texture, vec2(x_pos, y_pos)).rgb;
}

void fragment() {
	// d = 0 at the centre, 1 at the quad edge: the gaussian, zero at the
	// edge exactly (matches the reference soft texture).
	vec2 p = UV * 2.0 - 1.0;
	float d2 = dot(p, p);
	float falloff = (exp(-4.0 * d2) - exp(-4.0)) / (1.0 - exp(-4.0));
	vec4 col = vec4(COLOR.rgb, COLOR.a * falloff);
	if (splat_hue_mode > 0.5 && splat_hue > 0.0) {
		if (splat_hue_mode < 1.5) {
			col.rgb = splat_get_rgb(col.r, splat_hue);
		} else if (col.r == col.g && col.r == col.b) {
			col.rgb = splat_get_rgb(col.r, splat_hue);
		}
	}
	if (splat_cot > 0.5 && circle_of_transparency_radius > 0.0) {
		float ratio = length(SCREEN_UV * (1.0 / SCREEN_PIXEL_SIZE)
				- circle_of_transparency_center)
				/ circle_of_transparency_radius;

		if (ratio < 0.85) {
			discard;
		}

		if (ratio < 1.0) {
			float t = (ratio - 0.85) / 0.15;
			col.a *= t * t * t;

			if (col.a < 0.02) {
				discard;
			}
		}
	}
	COLOR = col;
}
