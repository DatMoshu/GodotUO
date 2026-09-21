// A texture handle in GUO is a Godot texture.
//
// Upstream types a great many things as FNA's `Texture2D`: gump art, an atlas
// page, a 1x1 solid colour, a font glyph sheet. In nearly all of them the type
// is used as an opaque handle -- stored, passed, compared to null and handed
// to a draw call -- and never for anything FNA-specific. Godot's Texture2D
// fills that role exactly, so aliasing the name means those files port with
// the type they already name.
//
// The alternative was a GUO-owned texture wrapper. That was rejected for the
// reason ADR-0001 rejects a full IRenderBackend: it re-creates the FNA layer
// this port exists to remove, and every wrapper needs unwrapping before Godot
// can draw it.
//
// This lives in src/Render rather than beside the Compat aliases on purpose.
// GUO.Compat holds VALUE TYPES ONLY -- that rule is what keeps four fifths of
// the port mechanical, and a reference type that owns GPU memory does not
// belong there even as an alias. The decision is a renderer decision, so it
// sits with the renderer.
//
// Scope note: this aliases the NAME, not the semantics. Godot's Texture2D is a
// RefCounted Resource, so lifetime is the engine's rather than a Dispose() the
// caller owns. Ported code that disposed a texture explicitly should drop the
// reference instead; the compiler will point at each one.

global using Texture2D = Godot.Texture2D;
