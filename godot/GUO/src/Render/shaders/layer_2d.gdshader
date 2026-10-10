// One terrain layer quad: a registered PNG stretched over its tile rect.
//
// The batcher writes the texture across UV 0..1 and the layer opacity into
// the vertex colour. Plain texture times vertex colour, normal alpha
// blending, unshaded: what the PNG holds is what the tile shows (times the
// opacity the editor staged). Same quad winding as the splat meshes
// (0 1 2, 1 3 2), which render.

shader_type canvas_item;
render_mode blend_mix, unshaded;

void fragment() {
	vec4 t = texture(TEXTURE, UV);
	COLOR = vec4(t.rgb * COLOR.rgb, t.a * COLOR.a);
}
