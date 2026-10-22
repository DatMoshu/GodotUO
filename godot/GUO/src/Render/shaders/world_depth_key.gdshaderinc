// ADR-0007: positive normal float bits preserve the full 28-bit integer order.
// Range [2^-32, 1), no zero, subnormal, NaN, infinity, or depth-clamp endpoint.
float world_depth_key(uint key) {
	return uintBitsToFloat(0x2f800000u + key);
}

// The frame-relative key from an instance's absolute parts, the same formula
// as WorldDepthKey.Relative: the depth part less the frame's origin, the tie
// part less the first chunk column that can show on the instance's diagonal.
// All in uint; nothing is converted to float before the bit cast. False when
// the instance is outside the frame's range (the caller collapses it).
const uint WORLD_TIE_BITS = 13u;
const uint WORLD_SLOT_BITS = 4u;
const uint WORLD_DEPTH_RANGE = 1u << 15u;
const uint WORLD_TIE_RANGE = 1u << 13u;

bool world_depth_relative(uint depth_abs, uint tie_abs, int s, uint depth_origin, int min_diff, out uint key) {
	key = 0u;
	if (depth_abs < depth_origin) {
		return false;
	}
	uint depth = depth_abs - depth_origin;
	if (depth >= WORLD_DEPTH_RANGE) {
		return false;
	}
	int x_min = (s + min_diff) >> 1;
	int column = x_min < 0 ? 0 : (x_min >> 3);
	uint tie_origin = (uint(column) * 8u) << WORLD_SLOT_BITS;
	if (tie_abs < tie_origin) {
		return false;
	}
	uint tie = tie_abs - tie_origin;
	if (tie >= WORLD_TIE_RANGE) {
		return false;
	}
	key = (depth << WORLD_TIE_BITS) | tie;
	return true;
}
