// Colour-blind help (ADR-0023): daltonize shifts the colours a given kind of
// colour blindness cannot tell apart into ones it can; simulate shows what
// that viewer sees (for checking a look). The matrices are the usual
// LMS-space ones (Viénot, Brettel and Mollon 1999; Fidaner et al. daltonization).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform int kind : hint_enum("Protanopia", "Deuteranopia", "Tritanopia") = 1;
uniform int simulate : hint_enum("Correct the colours", "Show what they see") = 0;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 1.0;

const mat3 RGB2LMS = mat3(vec3(17.8824, 3.45565, 0.0299566), vec3(43.5161, 27.1554, 0.184309),
	vec3(4.11935, 3.86714, 1.46709));
const mat3 LMS2RGB = mat3(vec3(0.0809444479, -0.0102485335, -0.000365296938),
	vec3(-0.130504409, 0.0540193266, -0.00412161469), vec3(0.116721066, -0.113614708, 0.693511405));

vec3 simulate_cvd(vec3 rgb) {
	vec3 lms = RGB2LMS * rgb;
	vec3 s;
	if (kind == 0) {
		s = vec3(2.02344 * lms.y - 2.52581 * lms.z, lms.y, lms.z);
	} else if (kind == 1) {
		s = vec3(lms.x, 0.494207 * lms.x + 1.24827 * lms.z, lms.z);
	} else {
		s = vec3(lms.x, lms.y, -0.395913 * lms.x + 0.801109 * lms.y);
	}
	return clamp(LMS2RGB * s, 0.0, 1.0);
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec3 sim = simulate_cvd(c.rgb);
	vec3 outc;
	if (simulate == 1) {
		outc = sim;
	} else {
		vec3 err = c.rgb - sim;
		// Move what the viewer cannot see onto the channels they can:
		// red-green loss (protan, deutan) into green and blue (Fidaner et al.);
		// blue-yellow loss (tritan) into red and green.
		vec3 shift = kind == 2 ? vec3(err.b * 0.7 + err.r, err.b * 0.7 + err.g, 0.0)
			: vec3(0.0, err.r * 0.7 + err.g, err.r * 0.7 + err.b);
		outc = clamp(c.rgb + shift, 0.0, 1.0);
	}
	COLOR = vec4(mix(c.rgb, outc, strength), c.a);
}
