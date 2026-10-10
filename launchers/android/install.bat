@echo off
rem Installs build/android/GUO-debug.apk onto the attached Android device with adb and reports the result.
REM ============================================================================
REM  adb installs build\android\GUO-debug.apk onto the attached device
REM  (UO_ANDROID_DEVICE picks one when several are plugged in).
REM ============================================================================
call "%~dp0..\_shared\common.bat" || exit /b 1

"%UO_PYTHON%" "%UO_ROOT%\tools\android\run.py" install %*
exit /b %ERRORLEVEL%
