@echo off
rem Builds out\dinput8.dll (the Azimuth-Span shim) with the Visual Studio 2022 Build Tools (x64).
rem AzimuthTelemetry.csproj runs this and embeds the result.
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -property installationPath`) do set VS=%%i
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
if not exist build mkdir build
cl /nologo /O2 /W4 /WX /MT /LD /Fo:build\ /Fe:out\dinput8.dll src\span.c /link /DEF:src\span.def user32.lib /IMPLIB:build\dinput8.lib || exit /b 1
