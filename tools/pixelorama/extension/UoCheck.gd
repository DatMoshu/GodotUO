extends RefCounted
## Size and transparency rules for Ultima Online art. Pure functions on an Image,
## so they can be tested without Pixelorama's UI. Mirrors GUO's AssetOverlay
## (godot/GUO/addons/guo_editor/Overlay/AssetOverlay.cs): land is the 44x44
## diamond, statics are at most 1024 square, gumps at most 2048 square, and
## transparency is one bit (UO has no partial alpha).

const LAND_SIZE := 44
const MAX_STATIC := 1024
const MAX_GUMP := 2048


## True for the 1,012 pixels of the 44x44 square a land tile stores.
static func in_diamond(x: int, y: int) -> bool:
	if y < 22:
		var start := 21 - y
		return x >= start and x < start + 2 * (y + 1)
	var i := y - 22
	return x >= i and x < i + 2 * (22 - i)


## Returns an array of human-readable problems; empty means the image is fine for [param kind]
## ("land", "static", "gump" or "anim").
static func problems(img: Image, kind: String) -> PackedStringArray:
	var out := PackedStringArray()
	var w := img.get_width()
	var h := img.get_height()
	match kind:
		"land":
			if w != LAND_SIZE or h != LAND_SIZE:
				out.append("land art is 44x44; this image is %dx%d" % [w, h])
		"static":
			if w > MAX_STATIC or h > MAX_STATIC:
				out.append("static art is at most 1024x1024; this image is %dx%d" % [w, h])
		"gump":
			if w > MAX_GUMP or h > MAX_GUMP:
				out.append("a gump is at most 2048x2048; this image is %dx%d" % [w, h])
		"anim":
			if w > MAX_STATIC or h > MAX_STATIC:
				out.append("an animation frame is at most 1024x1024; this image is %dx%d" % [w, h])
		_:
			out.append("unknown asset kind '%s'" % kind)
	var partial := 0
	var outside := 0
	var opaque := 0
	for y in h:
		for x in w:
			var a := img.get_pixel(x, y).a
			if a > 0.0 and a < 1.0:
				partial += 1
			if a > 0.0:
				opaque += 1
				if kind == "land" and w == LAND_SIZE and h == LAND_SIZE and not in_diamond(x, y):
					outside += 1
	if opaque == 0:
		out.append("the image is fully transparent")
	if partial > 0:
		out.append("%d pixels are partly transparent; UO has one-bit transparency (GUO keys alpha at 50%%)" % partial)
	if outside > 0:
		out.append("%d pixels lie outside the land diamond; GUO masks them off" % outside)
	return out
