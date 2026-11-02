@echo off
rem ScreenTranslator 빌드 (Windows 내장 csc.exe 사용, 별도 설치 불필요)
setlocal
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
set "WINMD=%WINDIR%\System32\WinMetadata\Windows.winmd"
set "ICO="
if exist "%~dp0ScreenTranslator.ico" set "ICO=/win32icon:"%~dp0ScreenTranslator.ico""

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 %ICO% ^
  /out:"%~dp0ScreenTranslator.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%WINMD%" ^
  "%~dp0ScreenTranslator.cs"

if errorlevel 1 (
  echo.
  echo 빌드 실패. 위의 오류 메시지를 확인하세요.
  pause
  exit /b 1
)
echo.
echo 빌드 완료: ScreenTranslator.exe
pause
