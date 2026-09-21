# What does it cost to push one sprite into a texture atlas on Godot?
#
# ClassicUO's TextureAtlas packs sprites into big pages and uploads just the
# packed rectangle, via FNA's SetDataPointerEXT. Godot has no partial texture
# upload at all -- ImageTexture.update and RenderingServer.texture_2d_update
# both replace the whole image -- so a page's size is also the cost of adding
# one 44x44 sprite to it. This measures that, so the page size is a number
# with a reason behind it rather than upstream's 4096 copied across.
#
# Run: godot-console --path godot/GUO --script res://dev/atlas_probe.gd
# (NOT --headless: the dummy renderer never uploads anything, so it would
# measure nothing and say it was fast.)

extends SceneTree

const SPRITE := 44
const ADDS := 60


func _measure(size: int) -> Array:
	var page := Image.create_empty(size, size, false, Image.FORMAT_RGBA8)
	var tex := ImageTexture.create_from_image(page)

	var sprite := Image.create_empty(SPRITE, SPRITE, false, Image.FORMAT_RGBA8)
	sprite.fill(Color(1, 0, 0, 1))

	var region := Rect2i(0, 0, SPRITE, SPRITE)

	# Warm up: the first upload of a texture allocates, which is not the cost
	# being asked about.
	page.blit_rect(sprite, region, Vector2i(0, 0))
	tex.update(page)
	await RenderingServer.frame_post_draw

	var blit_us := 0
	var update_us := 0

	for i in ADDS:
		var at := Vector2i((i * SPRITE) % (size - SPRITE), 0)

		var t0 := Time.get_ticks_usec()
		page.blit_rect(sprite, region, at)
		var t1 := Time.get_ticks_usec()
		tex.update(page)
		var t2 := Time.get_ticks_usec()

		blit_us += t1 - t0
		update_us += t2 - t1

	await RenderingServer.frame_post_draw

	return [float(blit_us) / ADDS, float(update_us) / ADDS]


func _init() -> void:
	print("[atlas-probe] cost of adding one %dx%d sprite, by page size" % [SPRITE, SPRITE])
	print("[atlas-probe]   (Godot has no partial upload; each add rewrites the page)")

	for size in [512, 1024, 2048, 4096]:
		var r: Array = await _measure(size)
		print("[atlas-probe] %5d x %5d  %8.1f MB  blit %7.1f us   upload %8.1f us"
			% [size, size, size * size * 4.0 / 1048576.0, r[0], r[1]])

	quit(0)
