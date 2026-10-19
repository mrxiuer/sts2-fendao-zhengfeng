@echo off
set "FDZF_PWSH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "FDZF_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%FDZF_PWSH%" (
  echo PowerShell 7 was not found. Run scripts/Start-LocalMultiplayer.ps1 from PowerShell 7.
  pause
  exit /b 1
)
"%FDZF_PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-LocalMultiplayer.ps1" %*
set "FDZF_EXIT=%ERRORLEVEL%"
if not "%FDZF_EXIT%"=="0" pause
exit /b %FDZF_EXIT%
