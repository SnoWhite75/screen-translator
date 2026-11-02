@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo csc.exe not found.
  pause
  exit /b 1
)

set "WREFS="
for %%F in ("%WINDIR%\System32\WinMetadata\*.winmd") do set "WREFS=!WREFS! /r:%%F"
if "!WREFS!"=="" (
  echo No WinMetadata files found in %WINDIR%\System32\WinMetadata
  pause
  exit /b 1
)

set "ICOARG="
if exist "ScreenTranslator.ico" set "ICOARG=/win32icon:ScreenTranslator.ico"

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 %ICOARG% /out:ScreenTranslator.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" !WREFS! ScreenTranslator.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED. Please check the error messages above.
  pause
  exit /b 1
)

echo.
echo BUILD OK: ScreenTranslator.exe
pause
