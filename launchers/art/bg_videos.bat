@echo off
rem Renders the procedural background loops (or the screensavers) into the project's assets folder and prints each video it wrote.
rem args: --only <themes> | --set builtin|screensavers|store | --size WxH
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\bg_videos\run.py" %*
exit /b %ERRORLEVEL%
