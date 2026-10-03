extends Node
## GUO tools for Ultima Online art (ADR-0029). MIT.
##
## Adds a set of items to Pixelorama's Project menu. The exchange folder with the
## GUO editor comes from the GUO_ART_EXCHANGE environment variable; the asset
## that was opened from GUO is described by the sidecar JSON in GUO_ART_SIDECAR
## ({kind, id, hue?, size, provenance}). Nothing here touches the UO client install.

const UoCheck := preload("res://src/Extensions/GUOTools/UoCheck.gd")
const PROJECT_MENU := 3  # ExtensionsApi.menu.PROJECT (the enum is unnamed)

class MenuItem:
	extends RefCounted
	var action: Callable

	func menu_item_clicked() -> void:
		action.call()

var _api: Node
var _items: Array = []  # [menu_id, MenuItem]
var _kind := ""  # what the open image is: land, static, gump, anim
var _sidecar := {}


func _enter_tree() -> void:
	_api = get_node_or_null("/root/ExtensionsApi")
	if _api == null:
		return
	_add("GUO: import UO hue palettes", _import_hues)
	_add("GUO: new land tile (44x44)", _new_land)
	_add("GUO: new static (foot line)", _new_static)
	_add("GUO: new gump", _new_gump)
	_add("GUO: new animation frame (centre)", _new_anim)
	_add("GUO: check size and transparency", _check_current)
	_add("GUO: save back to GUO", _save_back)
	_load_sidecar()


func _exit_tree() -> void:
	if _api == null:
		return
	for entry in _items:
		_api.menu.remove_menu_item(PROJECT_MENU, entry[0])
	_items.clear()


func _add(title: String, action: Callable) -> void:
	var item := MenuItem.new()
	item.action = action
	var id: int = _api.menu.add_menu_item(PROJECT_MENU, title, item)
	_items.append([id, item])


func exchange_dir() -> String:
	return OS.get_environment("GUO_ART_EXCHANGE")


func _load_sidecar() -> void:
	var path := OS.get_environment("GUO_ART_SIDECAR")
	if path.is_empty() or not FileAccess.file_exists(path):
		return
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	if typeof(parsed) == TYPE_DICTIONARY:
		_sidecar = parsed
		_kind = str(_sidecar.get("kind", ""))


func _say(text: String) -> void:
	_api.dialog.show_error(text)


# --- hue palettes -------------------------------------------------------------

## GUO writes <exchange>/hues.json from the user's hues.mul:
## {"format":1, "hues":[{"id":1, "name":"...", "colors":["RRGGBB", ...32]}]}
func _import_hues() -> void:
	var path := exchange_dir().path_join("hues.json")
	if exchange_dir().is_empty() or not FileAccess.file_exists(path):
		_say("No hues.json in the GUO exchange folder. Use 'Edit in Pixelorama' from the GUO editor first; it writes your hues there.")
		return
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	if typeof(parsed) != TYPE_DICTIONARY or not parsed.has("hues"):
		_say("hues.json is not a GUO hue table.")
		return
	var hues: Array = parsed["hues"]
	# One palette with every hue's brightest colour, and one per hue the open asset names.
	var overview := []
	for h in hues:
		var cols: Array = h["colors"]
		overview.append(_entry(overview.size(), Color.html(str(cols[cols.size() - 1]))))
	_make_palette("UO hues (all, brightest)", overview, 40)
	var wanted := int(_sidecar.get("hue", 0))
	for h in hues:
		if int(h["id"]) == wanted and wanted > 0:
			var ramp := []
			for c in h["colors"]:
				ramp.append(_entry(ramp.size(), Color.html(str(c))))
			_make_palette("UO hue %d %s" % [wanted, str(h.get("name", ""))], ramp, 8)
	_say("UO hue palettes added: %d hues." % hues.size())


func _entry(index: int, c: Color) -> Dictionary:
	return {"color": str(c), "index": index}


func _make_palette(palette_name: String, entries: Array, width: int) -> void:
	var height := int(ceil(float(entries.size()) / width))
	_api.palette.create_palette_from_data(
		palette_name,
		{"colors": entries, "comment": "From the user's own hues.mul via GUO. Not redistributable.", "width": width, "height": height}
	)


# --- templates ----------------------------------------------------------------

func _new_project(title: String, size: Vector2i, kind: String) -> void:
	_kind = kind
	var proj = _api.project.new_project([], title, Vector2(size), Color.TRANSPARENT)
	_api.project.current_project = proj


func _guide(size: Vector2i, draw: Callable) -> void:
	# A layer above the drawing, named "guide": Save back leaves it out.
	_api.project.add_new_layer(0, "guide")
	var img := Image.create(size.x, size.y, false, Image.FORMAT_RGBA8)
	draw.call(img)
	_api.project.set_pixelcel_image(img, 0, 1)


func _new_land() -> void:
	_new_project("land_tile", Vector2i(44, 44), "land")
	_guide(Vector2i(44, 44), func(img: Image) -> void:
		for y in 44:
			for x in 44:
				if UoCheck.in_diamond(x, y):
					# the diamond's edge pixels, to draw inside
					var edge: bool = not UoCheck.in_diamond(x - 1, y) or not UoCheck.in_diamond(x + 1, y) \
						or y == 0 or y == 43
					if edge:
						img.set_pixel(x, y, Color(1, 0, 1, 0.6)))


func _new_static() -> void:
	# A static is drawn so its footprint is the bottom 44x44, centred: ClassicUO places it at
	# x - (width/2 - 22), y - (height - 44). The foot line is the diamond's lower edge.
	var size := Vector2i(44, 88)
	_new_project("static", size, "static")
	_guide(size, func(img: Image) -> void:
		var top := size.y - 44
		for y in 44:
			for x in 44:
				if UoCheck.in_diamond(x, y) and (not UoCheck.in_diamond(x - 1, y) or not UoCheck.in_diamond(x + 1, y)):
					img.set_pixel(x + (size.x - 44) / 2, top + y, Color(0, 1, 1, 0.6))
		for x in size.x:
			img.set_pixel(x, size.y - 1, Color(1, 1, 0, 0.6)))


func _new_gump() -> void:
	_new_project("gump", Vector2i(128, 128), "gump")


func _new_anim() -> void:
	var size := Vector2i(96, 96)
	_new_project("anim_frame", size, "anim")
	_guide(size, func(img: Image) -> void:
		# The centre point (GUO anchors frames at the creature's feet, middle of the bottom third).
		var cx := size.x / 2
		var cy := size.y * 2 / 3
		for d in range(-4, 5):
			img.set_pixel(cx + d, cy, Color(1, 0, 0, 0.8))
			img.set_pixel(cx, cy + d, Color(1, 0, 0, 0.8)))


# --- checks and save back -------------------------------------------------------

## The visible drawing layers of the current frame, flattened. Layers named "guide" are left out.
func flatten() -> Image:
	var proj = _api.project.current_project
	var out := Image.create(proj.size.x, proj.size.y, false, Image.FORMAT_RGBA8)
	var frame = proj.frames[proj.current_frame]
	for i in proj.layers.size():
		var layer = proj.layers[i]
		if not layer.visible or layer.name.to_lower().begins_with("guide"):
			continue
		var cel = frame.cels[i]
		if not cel.has_method("get_image"):
			continue
		var img: Image = cel.get_image()
		if img == null:
			continue
		var src := Image.new()
		src.copy_from(img)
		src.convert(Image.FORMAT_RGBA8)
		out.blend_rect(src, Rect2i(Vector2i.ZERO, src.get_size()), Vector2i.ZERO)
	return out


func _kind_or_guess(img: Image) -> String:
	if not _kind.is_empty():
		return _kind
	return "land" if img.get_size() == Vector2i(44, 44) else "static"


func _check_current() -> void:
	var img := flatten()
	var kind := _kind_or_guess(img)
	var found := UoCheck.problems(img, kind)
	if found.is_empty():
		_say("OK for UO %s art (%dx%d)." % [kind, img.get_width(), img.get_height()])
	else:
		_say("For UO %s art:\n- %s" % [kind, "\n- ".join(found)])


func _save_back() -> void:
	var dir := exchange_dir()
	if dir.is_empty() or _sidecar.is_empty():
		_say("Not opened from GUO. Use 'Edit in Pixelorama' on an asset in the GUO editor; it sets the folder and the asset this saves back to.")
		return
	var img := flatten()
	var found := UoCheck.problems(img, _kind_or_guess(img))
	# Size problems stop the save; transparency notes do not (GUO's import applies the key itself).
	for p in found:
		if "is at most" in p or "is 44x44" in p or "fully transparent" in p:
			_say("Not saved: " + p)
			return
	var stem := str(_sidecar.get("stem", "%s_0x%04X" % [_kind, int(_sidecar.get("id", 0))]))
	var inbox := dir.path_join("in")
	DirAccess.make_dir_recursive_absolute(inbox)
	var side := _sidecar.duplicate(true)
	side["size"] = [img.get_width(), img.get_height()]
	var prov: Dictionary = side.get("provenance", {})
	prov["tool"] = "pixelorama"
	prov["workflow"] = "GUOTools save back"
	side["provenance"] = prov
	# The sidecar goes first: the PNG's arrival is what GUO's watcher reacts to.
	var jf := FileAccess.open(inbox.path_join(stem + ".json"), FileAccess.WRITE)
	jf.store_string(JSON.stringify(side, "  "))
	jf.close()
	img.save_png(inbox.path_join(stem + ".png"))
	_say("Saved back to GUO: %s.png" % stem)
