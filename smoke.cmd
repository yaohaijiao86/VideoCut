@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "WPF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF"
"%CSC%" /nologo /target:exe /main:VideoCut.Smoke /out:"%TEMP%\VideoCut.Smoke.exe" ^
  /reference:"%WPF%\PresentationCore.dll" ^
  /reference:"%WPF%\PresentationFramework.dll" ^
  /reference:"%WPF%\WindowsBase.dll" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Xaml.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  "%~dp0VideoCut.cs" "%~dp0Smoke.cs"
if errorlevel 1 exit /b 1
"%TEMP%\VideoCut.Smoke.exe"
exit /b %errorlevel%
