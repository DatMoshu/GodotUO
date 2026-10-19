@echo off
rem Verifies pinned ComfyUI nodes and model hashes offline; --validate also checks server node types.
for %%I in ("%~dp0..\..") do set "UO_ROOT=%%~fI"
call "%~dp0..\_shared\config.bat"
"%UO_PYTHON%" "%UO_ROOT%\tools\comfy\setup_studio.py" --check %*
exit /b %ERRORLEVEL%
