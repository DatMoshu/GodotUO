// Edge-directed pixel-art upscaling for the pregame: a reduced, single-pass corner
// blend. GUO, BSD-2-Clause, with the edge rule after Hyllian's xBR (below).
//
// For the corner of the source pixel the output pixel sits in, compare the two
// diagonal "edge weights" of the 4x4 neighbourhood; where the edge runs across the
// corner, the corner's triangle takes the colour of the neighbour along it. It is
// not Hyllian's xBR itself (no level 2/3 pattern tables, no per-scale kernels):
// one pass, any scale, no state. Nearest sampling only (the source is pixel art).
// Budget: 12 texture reads a pixel; a few full-screen layers on the Deck.
//
// The edge rule (the Y/U/V weights 48/7/6, the equality threshold 15, and the
// wd1/wd2 edge weights) follows Hyllian's xBR shaders, as published in libretro's
// glsl-shaders (xbr/shaders/xbr-lv2.glsl) and slang-shaders repositories.
// Copyright (C) 2011-2016 Hyllian - sergiogdb@gmail.com, MIT licence, whose
// notice follows (docs/upstream/XBR-HYLLIAN-MIT.md):
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy
//   of this software and associated documentation files (the "Software"), to deal
//   in the Software without restriction, including without limitation the rights
//   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//   copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in
//   all copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//   THE SOFTWARE.

const vec3 XBR_YUV = vec3(0.299, 0.587, 0.114);

float xbr_d(vec4 a, vec4 b) {
	// Luma-weighted distance, alpha counted (sprite edges against transparency).
	vec3 ya = vec3(dot(a.rgb, XBR_YUV), dot(a.rgb, vec3(-0.169, -0.331, 0.5)), dot(a.rgb, vec3(0.5, -0.419, -0.081)));
	vec3 yb = vec3(dot(b.rgb, XBR_YUV), dot(b.rgb, vec3(-0.169, -0.331, 0.5)), dot(b.rgb, vec3(0.5, -0.419, -0.081)));
	return dot(abs(ya - yb), vec3(48.0, 7.0, 6.0)) + abs(a.a - b.a) * 48.0;
}

bool xbr_eq(vec4 a, vec4 b) {
	return xbr_d(a, b) < 15.0;
}

// uv in [0,1] over the region [uv0, uv0 + uvsize] of tex, whose texel is px.
vec4 xbr_sample(sampler2D tex, vec2 uv, vec2 px) {
	vec2 p = uv / px;
	vec2 ip = floor(p);
	vec2 fp = p - ip;
	vec2 c = (ip + 0.5) * px;
	vec2 dx = vec2(fp.x < 0.5 ? -px.x : px.x, 0.0);
	vec2 dy = vec2(0.0, fp.y < 0.5 ? -px.y : px.y);

	vec4 cE = texture(tex, c);
	vec4 cF = texture(tex, c + dx);
	vec4 cH = texture(tex, c + dy);
	vec4 cI = texture(tex, c + dx + dy);
	vec4 cB = texture(tex, c - dy);
	vec4 cD = texture(tex, c - dx);
	vec4 cC = texture(tex, c + dx - dy);
	vec4 cG = texture(tex, c - dx + dy);
	vec4 cF4 = texture(tex, c + 2.0 * dx);
	vec4 cH5 = texture(tex, c + 2.0 * dy);
	vec4 cI4 = texture(tex, c + 2.0 * dx + dy);
	vec4 cI5 = texture(tex, c + dx + 2.0 * dy);

	float wd1 = xbr_d(cE, cC) + xbr_d(cE, cG) + xbr_d(cI, cF4) + xbr_d(cI, cH5) + 4.0 * xbr_d(cH, cF);
	float wd2 = xbr_d(cH, cD) + xbr_d(cH, cI5) + xbr_d(cF, cI4) + xbr_d(cF, cB) + 4.0 * xbr_d(cE, cI);
	bool edge = wd1 < wd2 && !xbr_eq(cE, cF) && !xbr_eq(cE, cH);

	// Distance into the corner: 0 at the pixel's centre, 2 at the corner itself.
	vec2 q = abs(fp - 0.5) * 2.0;
	float into = q.x + q.y;
	vec4 along = xbr_d(cE, cF) <= xbr_d(cE, cH) ? cF : cH;
	// The diagonal through the corner, with a one-texel-wide antialiased step.
	float k = edge ? smoothstep(0.95, 1.25, into) : 0.0;
	return mix(cE, along, k);
}
