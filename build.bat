@echo off
rem Build the HIOKI 3238 host app (Windows built-in C# compiler, no Visual Studio needed).
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found. Please install .NET Framework 4.x.
    pause
    exit /b 1
)

"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /platform:anycpu ^
    /win32manifest:app.manifest ^
    /win32icon:app.ico ^
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    /out:"日置3238上位机.exe" Main.cs

if errorlevel 1 (
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Build OK: 日置3238上位机.exe
pause
