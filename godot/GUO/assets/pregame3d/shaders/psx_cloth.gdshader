// PSX surface with a slow sway, for the velvet Cloth mesh. GUO, BSD-2-Clause.
// See psx_common.gdshaderinc; CLOTH_SWAY adds the vertex ripple.
shader_type spatial;

#define CLOTH_SWAY
#include "res://assets/pregame3d/shaders/psx_common.gdshaderinc"
