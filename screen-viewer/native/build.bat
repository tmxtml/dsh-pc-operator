@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%~dp0ScreenCapture.exe" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%~dp0ScreenCapture.cs"
if errorlevel 1 exit /b 1
echo BUILD_OK
