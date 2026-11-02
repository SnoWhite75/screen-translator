@echo off
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo csc.exe not found.
  pause
  exit /b 1
)

set "WINMD=%WINDIR%\System32\WinMetadata\Windows.winmd"
set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"

if exist "ScreenTranslator.ico" (
  "%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:ScreenTranslator.ico /out:ScreenTranslator.exe %REFS% /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.dll" /r:"%WINMD%" ScreenTranslator.cs
) else (
  "%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:ScreenTranslator.exe %REFS% /r:"%FW%\System.Runtime.WindowsRuntime.dll" /r:"%FW%\System.Runtime.dll" /r:"%WINMD%" ScreenTranslator.cs
)

if errorlevel 1 (
  echo.
  echo BUILD FAILED. Please check the error messages above.
  pause
  exit /b 1
)

echo.
echo BUILD OK: ScreenTranslator.exe
pause
