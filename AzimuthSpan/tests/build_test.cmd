@echo off
rem Builds and runs tests\span_test.c against out\dinput8.dll.
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq delims=" %%i in (`call "%VSWHERE%" -latest -products * -property installationPath`) do set VS=%%i
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1 || exit /b 1
cd /d "%~dp0.."
if not exist build mkdir build
cl /nologo /W4 /Fo:build\ /Fe:build\span_test.exe tests\span_test.c user32.lib || exit /b 1
set AZIMUTH_SPAN_TEST=1
build\span_test.exe out\dinput8.dll
