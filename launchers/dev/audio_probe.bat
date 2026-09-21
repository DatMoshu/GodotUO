@echo off
REM ============================================================================
REM  Asks whether Godot can play ClassicUO's audio directly -- AudioStreamWav
REM  from the raw PCM SoundsLoader already returns, and AudioStreamMP3 for
REM  music -- rather than shimming FNA's DynamicSoundEffectInstance on an
REM  AudioStreamGenerator and writing a push pump with nothing to tick it.
REM
REM  The MP3 stage reads the UO install in place and needs UO_CLIENT_DATA; it
REM  reports SKIP without it. Nothing is written.
REM
REM  Deliberately NOT --headless, to stay consistent with the other probes.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
"%GODOT_CONSOLE%" --path "%UO_GODOT_PROJECT%" --script res://dev/audio_probe.gd %*
exit /b %ERRORLEVEL%
