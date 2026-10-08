@echo off
REM ============================================================================
REM  GUO Device Test Manager: pick a device profile, apply it to an emulator or
REM  attached device, install and run GUO, collect screenshots. Needs tkinter.
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1
if defined UO_ANDROID_JDK set "JAVA_HOME=%UO_ANDROID_JDK%"

"%UO_PYTHON%" "%UO_TOOLS%\android\device_manager.py" %*
exit /b %ERRORLEVEL%
