@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "WPF=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF"
if not exist "%CSC%" (
  echo Cannot find the Windows C# compiler.
  exit /b 1
)
set "TARGET=winexe"
set "OUTPUT=VideoCut.exe"
if /I "%~1"=="debug" (
  set "TARGET=exe"
  set "OUTPUT=VideoCut.Debug.exe"
)
"%CSC%" /nologo /target:%TARGET% /optimize+ /out:"%~dp0%OUTPUT%" ^
  /reference:"%WPF%\PresentationCore.dll" ^
  /reference:"%WPF%\PresentationFramework.dll" ^
  /reference:"%WPF%\WindowsBase.dll" ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Xaml.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  "%~dp0VideoCut.cs"
exit /b %errorlevel%
