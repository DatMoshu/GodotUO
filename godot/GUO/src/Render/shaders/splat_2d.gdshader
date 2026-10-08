// One gaussian splat as a screen-aligned quad with a radial falloff.
//
// The batcher writes per-vertex colour (DC term mapped the standard way:
// rgb = 0.5 + 0.2829 * f_dc) straight (not premultiplied), and UVs spanning
// 0..1 across the quad. No texture is sampled, so there is nothing to filter:
// nearest-neighbour (project rule 7) holds trivially.
//
// blend_mix (normal alpha blending) like the reference renderer, whose soft
// disc texture this falloff matches exactly (zero at the quad edge).

shader_type canvas_item;
// NOTE: canvas_item has no cull_disabled mode; the quad winding below matches
// MeshLayer.WriteTriangles (0 1 2, 1 3 2), whose meshes render.
render_mode blend_mix, unshaded;

void fragment() {
	// d = 0 at the centre, 1 at the quad edge: the gaussian, zero at the
	// edge exactly (matches the reference soft texture).
	vec2 p = UV * 2.0 - 1.0;
	float d2 = dot(p, p);
	float falloff = (exp(-4.0 * d2) - exp(-4.0)) / (1.0 - exp(-4.0));
	COLOR = vec4(COLOR.rgb, COLOR.a * falloff);
}
