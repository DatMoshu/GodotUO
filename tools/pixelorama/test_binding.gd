extends SceneTree
## Original in-memory fixtures; never reads client art or writes an exchange image.

const Extension = preload("res://src/Extensions/GUOTools/GUOTools.gd")

class SaveProbe extends Extension:
	var applies := 0
	func _save_back(_show_success := true) -> void:
		applies += 1

class TestProject extends RefCounted:
	var export_profile: Dictionary
	var save_path := "already-saved.pxo"
	var size := Vector2i(2, 2)
	var current_frame := 0
	var frames: Array = [{"cels": [TestCel.new()]}]
	var layers: Array = [{"visible": true, "name": "paint"}]
	func _init(directory: String, filename: String) -> void:
		export_profile = {"directory_path": directory, "file_name": filename}

class TestCel extends RefCounted:
	func get_image() -> Image:
		var img := Image.create(2, 2, false, Image.FORMAT_RGBA8)
		img.set_pixel(0, 0, Color.WHITE)
		return img

class TestApi extends Node:
	var project: Dictionary = {"current_project": null}
	var dialog: TestDialog = TestDialog.new()
	var signals: TestSignals = TestSignals.new()

class TestSignals extends RefCounted:
	func signal_project_saved(_callback: Callable, _disconnect := false) -> void:
		pass

class TestDialog extends RefCounted:
	var message := ""
	func show_error(text: String) -> void:
		message = text

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var api := TestApi.new()
	var ext = Extension.new()
	ext._api = api
	ext._source_png = "C:/fixture/source.png"
	ext._sidecar = JSON.parse_string('{"format":1,"kind":"static","id":70,"hue":33,"stem":"source"}')
	OS.set_environment("GUO_ART_EXCHANGE", "user://binding-test-never-written")
	var source := TestProject.new("C:\\fixture", "source")
	var unrelated := TestProject.new("C:/fixture", "other")
	api.project.current_project = unrelated
	ext._process(0.0)
	assert(ext._bound_project == null, "Unrelated project must not acquire sidecar")
	api.project.current_project = source
	ext._process(0.0)
	assert(ext._bound_project.get_ref() == source, "Imported path should bind project identity")
	api.project.current_project = unrelated
	ext._save_back()
	assert(api.dialog.message.begins_with("Not saved:"), "Other tab must refuse save-back")
	# Even a second document with the same path must not inherit the original identity.
	api.dialog.message = ""
	api.project.current_project = TestProject.new("C:/fixture", "source")
	ext._save_back()
	assert(api.dialog.message.begins_with("Not saved:"), "Same path is not same document")
	api.dialog.message = ""
	ext._bound_project = null
	ext._save_back()
	assert(api.dialog.message.begins_with("Not saved:"), "Unbound session must refuse save-back")
	var save_probe := SaveProbe.new()
	save_probe._api = api
	save_probe._bound_project = weakref(source)
	api.project.current_project = unrelated
	save_probe._on_project_saved()
	assert(save_probe.applies == 0, "Saving unrelated project must not apply UO asset")
	api.project.current_project = source
	save_probe._on_project_saved()
	assert(save_probe.applies == 1, "Saving bound project must apply UO asset once")
	save_probe.free()
	# Exercise real flatten/sidecar/PNG emission with original pixels in the scratch project.
	var exchange := ProjectSettings.globalize_path("res://exchange")
	OS.set_environment("GUO_ART_EXCHANGE", exchange)
	ext._bound_project = weakref(source)
	ext._on_project_saved()
	var applied := Image.load_from_file(exchange.path_join("in/source.png"))
	assert(applied != null and applied.get_pixel(0, 0) == Color.WHITE, "Save must emit the edited pixel")
	var side = JSON.parse_string(FileAccess.get_file_as_string(exchange.path_join("in/source.json")))
	assert(side.id == 70 and side.provenance.tool == "pixelorama", "Save must preserve asset identity")
	var serialized := FileAccess.get_file_as_string(exchange.path_join("in/source.json"))
	assert(not '70.0' in serialized and not '33.0' in serialized, "Integer schema fields must survive JSON parsing")
	ext.free()
	api.free()
	print("[pixelorama] project binding: 10 checks passed")
	quit(0)
