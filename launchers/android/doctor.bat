@echo off
REM ============================================================================
REM  Says what a Godot 4.7 .NET Android export needs on this machine and what
REM  is missing: JDK 17, the Android SDK, the mono export templates, the debug
REM  keystore, Godot's editor settings, an attached device. Each missing thing
REM  comes with the exact fix. Add --publish to also build the C# for
REM  android-arm64, which proves the csproj guards without an SDK.
REM  Settings come from launchers\_shared\config.bat (UO_ANDROID_*).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" doctor %*
exit /b %ERRORLEVEL%
