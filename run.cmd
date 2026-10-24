@echo off
setlocal
pushd "%~dp0"
if not exist "VideoCut.exe" call build.cmd
if errorlevel 1 exit /b 1
start "" "%~dp0VideoCut.exe"
popd
