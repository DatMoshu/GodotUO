# Bridge for calling the DLSS5 GDExtension class from C# (or anywhere that
# can load a GDScript but can't reference unregistered ClassDB natives).
extends RefCounted


func make() -> DLSS5:
	return DLSS5.new()

# Temporal path (V1 frame hook): the DLSS5 instance from make() already
# serves it, so no extra bridge funcs are needed:
#   dlss.temporal_reset()
#   dlss.enhance_temporal(img320_rgb8, motion320_rgf, blend_scale)
# img320 must be 320x320 RGB8 (see Dlss5Service.PrepareSquare320);
# motion320 must be 320x320 FORMAT_RGF, pixel current->previous
# (Dlss5Service.ZeroMotion320 for a static frame). fp32 graph only: the
# fp16 still graph rejects temporal natively.
