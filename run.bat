@echo off
setlocal
cd /d "%~dp0"
if not exist "artifacts\MemoLite\MemoLite.exe" goto build
if not exist "artifacts\MemoLite\App.xbf" goto build
if not exist "artifacts\MemoLite\MainWindow.xbf" goto build
if not exist "artifacts\MemoLite\MemoLite.pri" goto build
goto launch

:build
    call build.bat
    if errorlevel 1 (
        pause
        exit /b 1
    )

:launch
start "" "%~dp0artifacts\MemoLite\MemoLite.exe" %*
