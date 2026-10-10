@echo off
rem Checks every local Markdown link and heading anchor in the repository and prints each broken one.
call "%~dp0..\_shared\common.bat" || exit /b 1
"%UO_PYTHON%" "%UO_TOOLS%\docs_lint\run.py" %*
exit /b %ERRORLEVEL%
