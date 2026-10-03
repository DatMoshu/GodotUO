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
REM      UO_GODOT_HOME     folder holding the pinned engine (tools\godot)
REM      UO_UPSTREAM_DIR   folder holding ClassicUO (sources); = UO_SOURCES
REM      UO_MAIN_CHECKOUT  the main checkout, when UO_ROOT is a git worktree
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

REM --- Engine and upstream folders --------------------------------------------
REM  UO_GODOT_HOME (the folder holding the pinned engine release, tools\godot)
REM  and UO_UPSTREAM_DIR (the folder holding ClassicUO, sources) win when set.
REM  Otherwise this checkout's own copy, and failing that -- a git worktree gets
REM  neither gitignored folder -- the main checkout's. No directory links.
REM  tools\guo\config.py resolves both the same way.
call :find_main_checkout
set "UO_GODOT_RELEASE=Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%"
if defined UO_GODOT_HOME goto :godot_home_done
set "UO_GODOT_HOME=%UO_ROOT%\tools\godot"
if exist "%UO_GODOT_HOME%\%UO_GODOT_RELEASE%\" goto :godot_home_done
if not defined UO_MAIN_CHECKOUT goto :godot_home_done
if exist "%UO_MAIN_CHECKOUT%\tools\godot\%UO_GODOT_RELEASE%\" set "UO_GODOT_HOME=%UO_MAIN_CHECKOUT%\tools\godot"
:godot_home_done
if defined UO_UPSTREAM_DIR goto :upstream_done
set "UO_UPSTREAM_DIR=%UO_ROOT%\sources"
if exist "%UO_UPSTREAM_DIR%\ClassicUO\" goto :upstream_done
if not defined UO_MAIN_CHECKOUT goto :upstream_done
if exist "%UO_MAIN_CHECKOUT%\sources\ClassicUO\" set "UO_UPSTREAM_DIR=%UO_MAIN_CHECKOUT%\sources"
:upstream_done

REM --- Derived paths --------------------------------------------------------
set "UO_GODOT_PROJECT=%UO_ROOT%\godot\GUO"
set "UO_SOURCES=%UO_UPSTREAM_DIR%"
set "UO_TOOLS=%UO_ROOT%\tools"
set "UO_DOCS=%UO_ROOT%\docs"
set "UO_BUILD=%UO_ROOT%\build"

REM --- Resolve the Godot executables ---------------------------------------
set "UO_GODOT_DIR=%UO_GODOT_HOME%\%UO_GODOT_RELEASE%"
if not defined GODOT_EXE     set "GODOT_EXE=%UO_GODOT_DIR%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%.exe"
if not defined GODOT_CONSOLE set "GODOT_CONSOLE=%UO_GODOT_DIR%\Godot_v%GODOT_VERSION%_%GODOT_FLAVOR%_console.exe"

if not exist "%GODOT_EXE%" (
    echo [common] FATAL: Godot not found at "%GODOT_EXE%"
    echo [common] Fetch the pinned build with: launchers\dev\fetch_godot.bat
    echo [common] A git worktree uses the main checkout's tools\godot: fetch it there.
    echo [common] Or set UO_GODOT_HOME to the folder holding %UO_GODOT_RELEASE%
    echo [common] Or set GODOT_EXE in launchers\_shared\config.local.bat
    exit /b 1
)
if not exist "%GODOT_CONSOLE%" set "GODOT_CONSOLE=%GODOT_EXE%"

REM --- Cache dir ------------------------------------------------------------
if not exist "%UO_CACHE_DIR%" mkdir "%UO_CACHE_DIR%" >nul 2>&1

exit /b 0

REM --- :find_main_checkout ----------------------------------------------------
REM  Sets UO_MAIN_CHECKOUT when UO_ROOT is a git worktree. A worktree's .git is
REM  a FILE, "gitdir: <common .git>\worktrees\<name>"; that folder's commondir
REM  names the shared .git, whose parent is the main checkout. Read from the
REM  files, so git need not be on PATH.
:find_main_checkout
set "UO_MAIN_CHECKOUT="
if exist "%UO_ROOT%\.git\" exit /b 0
if not exist "%UO_ROOT%\.git" exit /b 0
set "UO__GITDIR="
for /f "usebackq tokens=1,*" %%A in ("%UO_ROOT%\.git") do if /i "%%A"=="gitdir:" set "UO__GITDIR=%%B"
if not defined UO__GITDIR exit /b 0
set "UO__GITDIR=%UO__GITDIR:/=\%"
pushd "%UO_ROOT%" || exit /b 0
set "UO__COMMON=..\.."
if exist "%UO__GITDIR%\commondir" for /f "usebackq delims=" %%A in ("%UO__GITDIR%\commondir") do set "UO__COMMON=%%A"
set "UO__COMMON=%UO__COMMON:/=\%"
pushd "%UO__GITDIR%" 2>nul && (
    for %%I in ("%UO__COMMON%\..") do set "UO_MAIN_CHECKOUT=%%~fI"
    popd
)
popd
set "UO__GITDIR="
set "UO__COMMON="
if defined UO_MAIN_CHECKOUT if not exist "%UO_MAIN_CHECKOUT%\launchers\_shared\config.bat" set "UO_MAIN_CHECKOUT="
exit /b 0
