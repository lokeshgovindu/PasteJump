@echo off
rem VS 2026's toolchain, as CLAUDE.md requires for this repository.
rem Output goes to artifacts\ like everything else - nothing is written beside the source.
setlocal

set VCVARS=C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat
if not exist "%VCVARS%" (
  echo Could not find VS 2026's vcvars64.bat at:
  echo   %VCVARS%
  exit /b 1
)

rem Deliberately NOT checking errorlevel from vcvars: on at least one machine here it reports failure
rem because vswhere.exe is missing from PATH, while still setting the environment perfectly well. The
rem honest test is whether the compiler is reachable afterwards.
call "%VCVARS%" >nul 2>&1

where cl.exe >nul 2>&1
if errorlevel 1 (
  echo vcvars64.bat ran but cl.exe is still not on PATH - the C++ workload may not be installed.
  exit /b 1
)

set OUT=%~dp0..\..\artifacts\native-spike
if not exist "%OUT%" mkdir "%OUT%"

cl /nologo /EHsc /W4 /O2 /std:c++20 "%~dp0pjnative.cpp" /Fo"%OUT%\\" /Fe"%OUT%\pjnative.exe" /link user32.lib gdi32.lib advapi32.lib
if errorlevel 1 exit /b 1

echo.
echo Built %OUT%\pjnative.exe
echo Launch it from a scheduled task, not a shell - focusing another window needs foreground rights:
echo   schtasks /Create /TN PJNative /TR "%OUT%\pjnative.exe" /SC ONCE /ST 23:59 /IT /F
echo   schtasks /Run /TN PJNative  ^&^&  schtasks /Delete /TN PJNative /F
exit /b 0
