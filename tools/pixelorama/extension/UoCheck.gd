extends RefCounted
## Size and transparency rules for Ultima Online art. Pure functions on an Image,
## so they can be tested without Pixelorama's UI. Mirrors GUO's AssetOverlay
## (godot/GUO/addons/guo_editor/Overlay/AssetOverlay.cs): land is the 44x44
## diamond, statics are at most 1024 square, gumps at most 2048 square, and
## transparency is one bit (UO has no partial alpha).

const LAND_SIZE := 44
const MAX_STATIC := 1024
const MAX_GUMP := 2048


## The bounded animation sheet, with paint confined to each original untrimmed frame.
static func animation_sheet_problem(img: Image, side: Dictionary) -> String:
	if img == null or img.is_empty():
		return "no animation sheet image"
	var meta: Dictionary = side.get("animation", {})
	var frames: Array = meta.get("frames", [])
	var cell: Array = meta.get("cell_size", [])
	if frames.is_empty() or frames.size() > 256 or cell.size() != 2:
		return "invalid animation frame metadata"
	var w := int(cell[0])
	var h := int(cell[1])
	var columns := int(meta.get("columns", 0))
	var fps := float(meta.get("fps", 0))
	if is_nan(fps) or is_inf(fps) or fps < 1 or fps > 60:
		return "animation uses 1..60 FPS"
	if w < 1 or h < 1 or w > 4096 or h > 4096 or columns < 1 or columns > frames.size():
		return "invalid animation cell layout"
	var rows := int(ceil(float(frames.size()) / columns))
	if img.get_width() != w * columns or img.get_height() != h * rows \
		or img.get_width() > 16384 or img.get_height() > 16384 \
		or img.get_width() * img.get_height() > 33554432:
		return "animation sheet dimensions do not match its bounded layout"
	for i in frames.size():
		var r: Array = frames[i].get("rect", [])
		var center: Array = frames[i].get("center", [])
		if r.size() != 4 or center.size() != 2:
			return "animation frame needs its rectangle and centre"
		if abs(float(center[0])) > 4096 or abs(float(center[1])) > 4096:
			return "animation centre is outside the supported range"
		var region := Rect2i(int(r[0]), int(r[1]), int(r[2]), int(r[3]))
		if region.position.x < 0 or region.position.y < 0 or region.size.x < 1 or region.size.y < 1 \
			or region.size.x > MAX_STATIC or region.size.y > MAX_STATIC \
			or region.end.x > w or region.end.y > h:
			return "animation frame rectangle is outside its cell"
		for y in h:
			for x in w:
				if not region.has_point(Vector2i(x, y)) \
					and img.get_pixel(i % columns * w + x, i / columns * h + y).a >= 0.5:
					return "animation edits extend outside an original frame rectangle"
	for i in range(frames.size(), rows * columns):
		for y in h:
			for x in w:
				if img.get_pixel(i % columns * w + x, i / columns * h + y).a >= 0.5:
					return "animation sheet has paint in an unused cell"
	return ""


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
