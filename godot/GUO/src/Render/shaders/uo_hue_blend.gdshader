// The effect sprite path: hue the texel, then blend it by hand.
//
// ClassicUO's effects ask for five blend states and Godot's fixed canvas
// blend modes cover two of them. normalHalf is 2*src*dst, shadowBlue is a
// reverse subtract, and XNA's (One, One) additive is not Godot's blend_add
// whenever alpha is below 1. So rather than approximate three of five, this
// turns hardware blending off and reimplements XNA's fixed-function blend
// unit: any (source factor, destination factor, function) triple, taken
// straight off the BlendState the ported code already constructs.
//
// The destination is read back through hint_screen_texture, which needs the
// batcher to request a back-buffer copy before the draw. Verified working
// from a RenderingServer canvas item by launchers\dev\blend_probe.bat.
//
// This costs a screen read per effect sprite and breaks batching across one.
// That is affordable because effects are a handful on screen at once, and it
// is the only way to get the equation right rather than close.

shader_type canvas_item;
render_mode blend_disabled, unshaded;

#include "uo_hue_core.gdshaderinc"

uniform sampler2D screen_texture : hint_screen_texture, filter_nearest;

// Microsoft.Xna.Framework.Graphics.Blend, in its own declaration order.
const int BLEND_ONE = 0;
const int BLEND_ZERO = 1;
const int BLEND_SOURCE_COLOR = 2;
const int BLEND_INVERSE_SOURCE_COLOR = 3;
const int BLEND_SOURCE_ALPHA = 4;
const int BLEND_INVERSE_SOURCE_ALPHA = 5;
const int BLEND_DESTINATION_COLOR = 6;
const int BLEND_INVERSE_DESTINATION_COLOR = 7;
const int BLEND_DESTINATION_ALPHA = 8;
const int BLEND_INVERSE_DESTINATION_ALPHA = 9;

// Microsoft.Xna.Framework.Graphics.BlendFunction.
const int FUNC_ADD = 0;
const int FUNC_SUBTRACT = 1;
const int FUNC_REVERSE_SUBTRACT = 2;
const int FUNC_MIN = 3;
const int FUNC_MAX = 4;

uniform int color_source_blend = 0;
uniform int color_destination_blend = 1;
uniform int color_blend_function = 0;

uniform int alpha_source_blend = 0;
uniform int alpha_destination_blend = 1;
uniform int alpha_blend_function = 0;

vec4 blend_factor(int which, vec4 src, vec4 dst) {
	if (which == BLEND_ONE) {
		return vec4(1.0);
	} else if (which == BLEND_ZERO) {
		return vec4(0.0);
	} else if (which == BLEND_SOURCE_COLOR) {
		return src;
	} else if (which == BLEND_INVERSE_SOURCE_COLOR) {
		return vec4(1.0) - src;
	} else if (which == BLEND_SOURCE_ALPHA) {
		return vec4(src.a);
	} else if (which == BLEND_INVERSE_SOURCE_ALPHA) {
		return vec4(1.0 - src.a);
	} else if (which == BLEND_DESTINATION_COLOR) {
		return dst;
	} else if (which == BLEND_INVERSE_DESTINATION_COLOR) {
		return vec4(1.0) - dst;
	} else if (which == BLEND_DESTINATION_ALPHA) {
		return vec4(dst.a);
	} else if (which == BLEND_INVERSE_DESTINATION_ALPHA) {
		return vec4(1.0 - dst.a);
	}

	return vec4(1.0);
}

vec4 combine(int func, vec4 s, vec4 d) {
	if (func == FUNC_SUBTRACT) {
		return s - d;
	} else if (func == FUNC_REVERSE_SUBTRACT) {
		return d - s;
	} else if (func == FUNC_MIN) {
		return min(s, d);
	} else if (func == FUNC_MAX) {
		return max(s, d);
	}

	return s + d;
}

void vertex() {
	// COLOR is still the untouched modulate here; by fragment() the
	// texture has already been multiplied in. See the include's header.
	packed_hue = COLOR;
	// Not a mesh, so no per-vertex normal and no land here.
	land_light = 1.0;
}

void fragment() {
	vec4 src = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
	vec4 dst = texture(screen_texture, SCREEN_UV);

	vec4 rgb = combine(
		color_blend_function,
		src * blend_factor(color_source_blend, src, dst),
		dst * blend_factor(color_destination_blend, src, dst));

	vec4 a = combine(
		alpha_blend_function,
		src * blend_factor(alpha_source_blend, src, dst),
		dst * blend_factor(alpha_destination_blend, src, dst));

	// The hardware clamps after blending; doing it by hand means doing that too.
	COLOR = clamp(vec4(rgb.rgb, a.a), 0.0, 1.0);
}
