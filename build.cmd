@echo off
setlocal
cd /d "%~dp0"

where py.exe >nul 2>nul
if not errorlevel 1 (
  py.exe -3 "%~dp0build.py" %*
) else (
  where python.exe >nul 2>nul
  if errorlevel 1 (
    echo Python 3 was not found. Install Python 3 and enable "Add Python to PATH".
    set "BUILD_EXIT_CODE=1"
    goto :finish
  )
  python.exe "%~dp0build.py" %*
)

set "BUILD_EXIT_CODE=%ERRORLEVEL%"

:finish
echo.
if "%BUILD_EXIT_CODE%"=="0" (
  echo SeekClaw build completed successfully.
) else (
  echo SeekClaw build failed. See the error above.
)
if not defined SEEKCLAW_NO_PAUSE pause
exit /b %BUILD_EXIT_CODE%
