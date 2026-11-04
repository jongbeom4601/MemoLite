@echo off
setlocal
cd /d "%~dp0"
dotnet publish "src\MemoLite\MemoLite.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained true -o "artifacts\MemoLite" --nologo
if errorlevel 1 exit /b 1
if not exist "artifacts\MemoLite\App.xbf" goto missing_resources
if not exist "artifacts\MemoLite\MainWindow.xbf" goto missing_resources
if not exist "artifacts\MemoLite\MemoLite.pri" goto missing_resources
echo.
echo Ready: artifacts\MemoLite\MemoLite.exe
exit /b 0

:missing_resources
echo ERROR: Required WinUI resources are missing from the published application.
exit /b 1
