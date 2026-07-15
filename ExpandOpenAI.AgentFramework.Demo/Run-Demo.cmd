@echo off
setlocal
cd /d "%~dp0"

dotnet run --project "ExpandOpenAI.AgentFramework.Demo.csproj" -- %*
set "DEMO_EXIT_CODE=%ERRORLEVEL%"

if not "%DEMO_EXIT_CODE%"=="0" (
    echo.
    echo Demo exited with code %DEMO_EXIT_CODE%.
    pause
)

exit /b %DEMO_EXIT_CODE%
