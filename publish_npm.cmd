@echo off
setlocal
cd /d "%~dp0"

where py.exe >nul 2>nul
if not errorlevel 1 (
  py.exe -3 "%~dp0publish_npm.py" %*
) else (
  where python.exe >nul 2>nul
  if errorlevel 1 (
    echo Python 3 was not found. Install Python 3 and enable "Add Python to PATH".
    exit /b 1
  )
  python.exe "%~dp0publish_npm.py" %*
)

set "PUBLISH_EXIT_CODE=%ERRORLEVEL%"

echo.
if "%PUBLISH_EXIT_CODE%"=="0" (
  echo SeekClaw CLI npm packaging completed successfully.
) else (
  echo SeekClaw CLI npm packaging failed. See the error above.
)
if not defined SEEKCLAW_NO_PAUSE pause
exit /b %PUBLISH_EXIT_CODE%
