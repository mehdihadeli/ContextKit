@rem Repo-local development launcher for the GroundKit CLI.
@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%src\GroundKit.Cli\GroundKit.Cli.csproj"

dotnet run --project "%PROJECT%" --no-restore -v:q -- %*
exit /b %ERRORLEVEL%