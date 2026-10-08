extends Node
## Original artwork only. Drives Pixelorama's actual project Save twice, not a mocked signal.
const Extension = preload("res://src/Extensions/GUOTools/GUOTools.gd")

func _ready() -> void:
	call_deferred("_run")

func _run() -> void:
	for i in 10:
		await get_tree().process_frame
	var exchange := OS.get_environment("GUO_SAVE_CHECK")
	var source := exchange.path_join("source.png")
	DirAccess.make_dir_recursive_absolute(exchange)
	var image := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	image.fill(Color.RED)
	image.save_png(source)
	FileAccess.open(exchange.path_join("source.json"), FileAccess.WRITE).store_string('{"kind":"static","id":70,"hue":33,"stem":"static_0x0046","provenance":{"derived_from_client_art":false}}')
	OS.set_environment("GUO_ART_EXCHANGE", exchange)
	OS.set_environment("GUO_ART_SIDECAR", exchange.path_join("source.json"))
	OS.set_environment("GUO_ART_SOURCE_PNG", source)
	OpenSave.open_image_as_new_tab(source, image)
	var extension := Extension.new()
	get_tree().root.add_child(extension)
	for i in 3:
		await get_tree().process_frame
	assert(extension._bound_project.get_ref() == Global.current_project, "Source project must bind")
	for iteration in 2:
		image.fill(Color.RED if iteration == 0 else Color.BLUE)
		ExtensionsApi.project.set_pixelcel_image(image, 0, 0)
		var destination: String = Global.current_project.save_path
		assert(not destination.is_empty(), "Save must have a native project destination")
		assert(OpenSave.save_pxo_file(destination, false), "Native Pixelorama project Save must succeed")
		var pair := exchange.path_join("in/static_0x0046")
		assert(FileAccess.file_exists(pair + ".png"), "Native project Save must emit UO image")
		var json := FileAccess.get_file_as_string(pair + ".json")
		assert('"id": 70' in json and not '70.0' in json, "Native Save must emit integer ID")
		var saved := Image.load_from_file(pair + ".png")
		assert(saved.get_pixel(0, 0) == (Color.RED if iteration == 0 else Color.BLUE), "Repeated Save must emit refreshed pixels")
		var proof := exchange.path_join("save%d" % (iteration + 1))
		DirAccess.make_dir_recursive_absolute(proof)
		DirAccess.rename_absolute(pair + ".png", proof.path_join("static_0x0046.png"))
		DirAccess.rename_absolute(pair + ".json", proof.path_join("static_0x0046.json"))
	assert(FileAccess.file_exists(Global.current_project.save_path), "Editable pxo must exist")
	var project_path: String = Global.current_project.save_path
	OpenSave.open_pxo_file(project_path, false, false)
	for i in 3:
		await get_tree().process_frame
	assert(Global.current_project.frames[0].cels[0].get_image().get_pixel(0, 0) == Color.BLUE, "Native project reopen must retain edited pixels")
	print("[pixelorama] native repeated Save: passed (project, signal, pixels, integer ID)")
	extension.queue_free()
	await get_tree().process_frame
	get_tree().quit(0)
