@echo off
rem Builds rnnoise.dll (x64, static runtime) from an RNNoise 0.2 release tarball's sources with the Visual Studio C compiler.
rem Usage: build.cmd <rnnoise source folder> <output folder>
rem The release's x86 dispatch code expects Opus headers it does not ship, so the portable SSE2 code is used.
setlocal
set "SRC=%~1"
set "OUT=%~2"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo Visual Studio is required to build RNNoise. & exit /b 1)
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo The Visual Studio C++ x64 tools are required to build RNNoise. & exit /b 1)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist "%OUT%\obj" mkdir "%OUT%\obj"
cd /d "%OUT%\obj" || exit /b 1
set "S=%SRC%\src"
cl /nologo /c /O2 /fp:fast /MT /W0 /DRNNOISE_BUILD /DDLL_EXPORT /DWIN32 /D_CRT_SECURE_NO_WARNINGS /D__SSE2__ /I"%SRC%\include" /I"%S%" ^
  "%S%\denoise.c" "%S%\rnn.c" "%S%\pitch.c" "%S%\kiss_fft.c" "%S%\celt_lpc.c" "%S%\nnet.c" "%S%\nnet_default.c" ^
  "%S%\parse_lpcnet_weights.c" "%S%\rnnoise_data.c" "%S%\rnnoise_tables.c" || exit /b 1
link /nologo /DLL /OUT:"%OUT%\rnnoise.dll" *.obj || exit /b 1
