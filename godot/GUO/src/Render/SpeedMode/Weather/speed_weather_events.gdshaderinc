// SPDX-License-Identifier: BSD-2-Clause
// World-cell/event-index hash shared by rain splashes and wet-floor rings.
// Integer mixing avoids large-coordinate sin precision; no frame counter/UV input.
float weather_event_hash(vec2 cell, float event_index, uint stream) {
	uint h = uint(int(cell.x)) * 0x8da6b343u;
	h ^= uint(int(cell.y)) * 0xd8163841u;
	h ^= uint(int(event_index)) * 0xcb1ab31fu;
	h ^= stream * 0x9e3779b9u;
	h ^= h >> 16u;
	h *= 0x7feb352du;
	h ^= h >> 15u;
	h *= 0x846ca68bu;
	h ^= h >> 16u;
	return float(h & 0x00ffffffu) / 16777216.0;
}
