@echo off
REM ============================================================================
REM  GUO - shared launcher logic
REM
REM  NOT A LAUNCHER. Never run this directly. Every .bat under launchers\
REM  `call`s this file as its first statement:
REM
REM      call "%~dp0..\_shared\common.bat" || exit /b 1
REM
REM  Afterwards the following are guaranteed to be set and validated:
REM      UO_ROOT           repo root (no trailing backslash)
REM      UO_GODOT_PROJECT  folder containing project.godot
REM      GODOT_EXE         interactive editor/runtime executable
REM      GODOT_CONSOLE     blocking console executable (use this in scripts)
REM      plus everything defined in _shared\config.bat
REM ============================================================================

REM --- Resolve repo root (this file lives at <root>\launchers\_shared) -------
for %%I in ("%~dp0..\..") do set "UO_ROOT=%%~fI"

REM --- 3. central shared config (lowest precedence, optional) ---------------
REM  Set UO_COMMON_CONFIG to a shared config.bat when several UO projects live
REM  side by side and should agree on paths. Loaded FIRST so the project config
REM  and real environment variables can still win.
if defined UO_COMMON_CONFIG (
    if exist "%UO_COMMON_CONFIG%" (
        call "%UO_COMMON_CONFIG%"
    ) else (
        echo [common] WARNING: UO_COMMON_CONFIG set but not found: "%UO_COMMON_CONFIG%"
    )
)

REM --- 2. project config ----------------------------------------------------
REM  Every setting there is guarded by `if not defined`, so values already in
REM  the environment (1) and values from the central config survive.
if not exist "%~dp0config.bat" (
    echo [common] FATAL: missing "%~dp0config.bat"
    exit /b 1
)
call "%~dp0config.bat"

REM --- Derived paths --------------------------------------------------------
set "UO_GODOT_PROJECT=%UO_ROOT%\godot\GUO"
set "UO_SOURCES=%UO_ROOT%\sources"
set "UO_TOOLS=%UO_ROOT%\tools"
set "UO_DOCS=%UO_ROOT%\docs"
set "UO_BUILD=%UO_ROOT%\build"

REM --- Resolve the Godot executables ---------------------------------------
set "UO_GODOT_DIR=%UO_TOOLS%\godot\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%"
if not defined GODOT_EXE     set "GODOT_EXE=%UO_GODOT_DIR%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.exe"
if not defined GODOT_CONSOLE set "GODOT_CONSOLE=%UO_GODOT_DIR%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%_console.exe"

if not exist "%GODOT_EXE%" (
    echo [common] FATAL: Godot not found at "%GODOT_EXE%"
    echo [common] Fetch the pinned build with: launchers\dev\fetch_godot.bat
    echo [common] Or set GODOT_EXE in launchers\_shared\config.bat
    exit /b 1
)
if not exist "%GODOT_CONSOLE%" set "GODOT_CONSOLE=%GODOT_EXE%"

REM --- Cache dir ------------------------------------------------------------
if not exist "%UO_CACHE_DIR%" mkdir "%UO_CACHE_DIR%" >nul 2>&1

exit /b 0
