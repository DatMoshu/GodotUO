// ADR-0007: positive normal float bits preserve the full 28-bit integer order.
// Range [2^-32, 1), no zero, subnormal, NaN, infinity, or depth-clamp endpoint.
float world_depth_key(uint key) {
	return uintBitsToFloat(0x2f800000u + key);
}
