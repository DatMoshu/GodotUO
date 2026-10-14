@echo off
rem Builds the Godot project's C# assemblies headless, without opening the editor, and prints the compiler output.
REM Builds the Godot project's C# assemblies without opening the editor.
REM This is the fast "does it still compile" check.
call "%~dp0..\_shared\common.bat" || exit /b 1
echo [build] Building C# assemblies for %UO_GODOT_PROJECT%
REM UO_BUILD_TIMEOUT (seconds, _shared\config.bat) bounds the engine run: a
REM headless --quit that never quits is stopped as a tree and reported as
REM exit 124, so automation never hangs forever. The engine streams to this
REM console itself; a healthy build keeps its own exit code.
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$p = Start-Process -FilePath $env:GODOT_CONSOLE -ArgumentList '--headless','--path',$env:UO_GODOT_PROJECT,'--build-solutions','--quit' -NoNewWindow -PassThru; $s = 0; if (-not [int]::TryParse($env:UO_BUILD_TIMEOUT, [ref] $s) -or $s -le 0) { $s = 600 }; if (-not $p.WaitForExit($s * 1000)) { Write-Output ('[build] timed out after ' + $s + ' s; stopping the engine tree'); taskkill /PID $p.Id /T /F; exit 124 }; exit $p.ExitCode"
exit /b %ERRORLEVEL%
