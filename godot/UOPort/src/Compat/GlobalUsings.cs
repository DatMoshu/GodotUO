// Project-wide type aliases for the XNA compatibility layer.
//
// Three of the FNA maths types the ported code uses are already present in
// Godot with a matching public surface: float X/Y/Z/W fields, the same
// operators, and the same Zero/One constants. Aliasing straight to Godot's
// versions is strictly better than writing our own copies -- ported code
// compiles, and no conversion is needed when the value reaches the engine.
//
// Vector4 is included for completeness; the audit counts a single use of it
// upstream.
//
// Matrix is deliberately NOT aliased. Godot splits that role across
// Transform2D, Transform3D and Projection, none of which is a drop-in
// replacement, and all 36 upstream uses sit inside files already classified
// `rewrite`. Handle them there rather than pretending a shim exists.
//
// The port's own byte-based Color, integer Point and integer Rectangle live
// beside this file; they are NOT aliased, because Godot's equivalents differ
// in storage and semantics. See README.md in this folder.

global using Vector2 = Godot.Vector2;
global using Vector3 = Godot.Vector3;
global using Vector4 = Godot.Vector4;
