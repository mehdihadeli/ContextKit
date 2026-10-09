@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%src\ContextKit.Cli\ContextKit.Cli.csproj"

dotnet run --project "%PROJECT%" --no-restore -v:q -- %*
exit /b %ERRORLEVEL%