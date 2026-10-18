@echo off
rem Build LineDry with the compiler that ships inside .NET Framework 4.8.
rem No SDK, no NuGet, no downloads: the reference assemblies are system files.
setlocal enabledelayedexpansion

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "RES=%ROOT%res"
set "OUT=%ROOT%build"

if not exist "%CSC%" (
  echo [build] csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUT%" mkdir "%OUT%"

set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"

set "RESARGS="
for %%R in (en es gl fr pt-BR ja ko zh-Hans tr az hi) do (
  if exist "%RES%\%%R.strings" (
    set "RESARGS=!RESARGS! /resource:"%RES%\%%R.strings",LineDry.res.%%R.strings"
  )
)

set "LANGARG="
if /i "%1"=="en" set "LANGARG=/define:LANG_EN"

echo [build] compiling LineDry.exe
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /out:"%OUT%\LineDry.exe" ^
  /win32manifest:"%RES%\app.manifest" ^
  /win32icon:"%RES%\app.ico" ^
  %REFS% %RESARGS% ^
  "%SRC%\*.cs"

if errorlevel 1 (
  echo [build] FAILED
  exit /b 1
)

for %%F in ("%OUT%\LineDry.exe") do echo [build] OK  %%~zF bytes
endlocal
