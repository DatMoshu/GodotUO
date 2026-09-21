# Can Godot play ClassicUO's audio without an MP3 decoder and without a
# streaming push loop?
#
# Upstream builds both sound effects and music on FNA's
# DynamicSoundEffectInstance: the caller owns a PCM buffer, submits it, and
# refills on a BufferNeeded callback. UOSound submits one complete buffer and
# never refills; UOMusic decodes MP3 with MP3Sharp and tops the queue up to
# three chunks every frame.
#
# Godot has no DynamicSoundEffectInstance. It has AudioStreamGenerator, which
# would let that protocol be shimmed, and it also decodes MP3 itself. Whether
# the simpler route works is what this measures, because getting it wrong is
# expensive in both directions: shimming the generator means writing a push
# pump with nothing in the engine to tick it, and NOT shimming it means the
# three IO/Audio files stop being a verbatim port.
#
# Four questions, none of them assumed -- Godot 4.7.2 is past the assistant's
# knowledge cutoff:
#
#   1. Does AudioStreamWAV accept the raw bytes SoundsLoader already returns?
#      UO sound data is headerless 22050 Hz mono signed 16-bit PCM; if
#      AudioStreamWAV wants a RIFF container instead, the loader would have to
#      grow one.
#   2. Does it report the right length? Play() sets DurationTime from the
#      buffer duration, and IsPlaying(curTime) compares against it, so a wrong
#      length silently changes when a sound is considered finished.
#   3. Does an AudioStreamPlayer parented to the tree root actually run from a
#      plain SceneTree script -- playing goes true, the position advances, and
#      it stops on its own at the end?
#   4. Does AudioStreamMP3.load_from_buffer decode a real UO music file? This
#      one needs the client data, so it reports SKIP when UO_CLIENT_DATA is
#      unset. It reads the install in place and writes nothing.
#
# Run: godot-console --path godot/GUO --script res://dev/audio_probe.gd
# (NOT --headless: keep this consistent with the other probes, and the dummy
# audio driver is a different thing again.)

extends SceneTree

# What SoundsLoader.TryGetSound hands out, and what it validates .wav
# replacements against.
const MIX_RATE := 22050
const BYTES_PER_SAMPLE := 2

var failures := 0


func _check(label: String, ok: bool, detail: String) -> void:
	print("[audio-probe] %-40s %-22s %s"
		% [label, detail, "ok" if ok else "MISMATCH"])
	if not ok:
		failures += 1


func _skip(label: String, why: String) -> void:
	print("[audio-probe] %-40s %-22s skip" % [label, why])


# A quarter second of a quiet sine, as headerless signed 16-bit mono PCM --
# the exact shape ReadWave returns and the .mul path produces.
func _make_pcm(seconds: float) -> PackedByteArray:
	var samples := int(MIX_RATE * seconds)
	var data := PackedByteArray()
	data.resize(samples * BYTES_PER_SAMPLE)

	for i in samples:
		var value := int(sin(TAU * 440.0 * float(i) / MIX_RATE) * 8000.0)
		data.encode_s16(i * BYTES_PER_SAMPLE, value)

	return data


func _find_music_file() -> String:
	var base := OS.get_environment("UO_CLIENT_DATA")
	if base == "":
		return ""

	for folder in ["Music/Digital", "Music"]:
		var path := base.path_join(folder)
		var names := DirAccess.get_files_at(path)
		if names == null:
			continue
		for name in names:
			if name.to_lower().ends_with(".mp3"):
				return path.path_join(name)

	return ""


func _init() -> void:
	# The probe genuinely plays audio. Mute the master bus so running it does
	# not make a noise; muting a bus does not stop the mixer, so everything
	# measured below still happens.
	AudioServer.set_bus_mute(0, true)

	var seconds := 0.25
	var pcm := _make_pcm(seconds)

	var wav := AudioStreamWAV.new()
	wav.data = pcm
	wav.format = AudioStreamWAV.FORMAT_16_BITS
	wav.mix_rate = MIX_RATE
	wav.stereo = false
	wav.loop_mode = AudioStreamWAV.LOOP_DISABLED

	_check("AudioStreamWAV takes raw PCM",
		wav.data.size() == pcm.size(),
		"%d bytes" % wav.data.size())

	# Upstream computes DurationTime from the submitted buffer, so this number
	# is load-bearing rather than cosmetic.
	var length := wav.get_length()
	_check("...and reports the right length",
		absf(length - seconds) < 0.001,
		"want %.4fs got %.4fs" % [seconds, length])

	var player := AudioStreamPlayer.new()
	player.stream = wav
	root.add_child(player)

	# MEASURED: inside _init of a SceneTree script the root's children are not
	# yet "inside the tree", and play() refuses with
	#   "Playback can only happen when a node is inside the scene tree".
	# One frame is enough. The running client never hits this -- its tree is
	# already up when a sound is asked for -- but it is why AudioHost parents
	# the player before playing it rather than after, and why nothing about
	# that ordering is deferred.
	await process_frame

	# volume_linear is what the port maps Sound.Volume onto; the alternative is
	# converting to decibels by hand on every volume change.
	player.volume_linear = 0.5
	_check("volume_linear round-trips",
		absf(player.volume_linear - 0.5) < 0.001,
		"%.4f" % player.volume_linear)

	player.play()
	_check("playing goes true", player.playing, str(player.playing))

	# The mixer should have moved the playhead. If audio never advances under
	# a --script run, this is where it shows.
	await create_timer(0.05).timeout

	var position := player.get_playback_position()
	_check("the playhead advances", position > 0.0, "%.4fs" % position)

	# Well past the end. A non-looping stream stops itself, which is what
	# IsPlaying relies on once DurationTime has passed.
	await create_timer(seconds + 0.2).timeout
	_check("it stops at the end", not player.playing, str(player.playing))

	player.queue_free()

	var music_path := _find_music_file()

	if music_path == "":
		_skip("AudioStreamMP3 decodes UO music", "no UO_CLIENT_DATA")
	else:
		var bytes := FileAccess.get_file_as_bytes(music_path)
		var mp3 := AudioStreamMP3.load_from_buffer(bytes)

		_check("AudioStreamMP3 decodes UO music",
			mp3 != null and mp3.get_length() > 1.0,
			"%.1fs" % (mp3.get_length() if mp3 != null else 0.0))

		if mp3 != null:
			# UOMusic's loop flag has to reach the stream; upstream implements
			# looping itself by rewinding the decoder.
			mp3.loop = true
			_check("...and honours the loop flag", mp3.loop, str(mp3.loop))

	print("[audio-probe] %s" % ["PASS" if failures == 0 else "FAIL"])
	quit(0 if failures == 0 else 1)
