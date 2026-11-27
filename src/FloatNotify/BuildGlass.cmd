@echo off
setlocal
for /f "usebackq delims=" %%v in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "glassVs=%%v"
if not defined glassVs exit /b 2
call "%glassVs%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
if not exist "%~1" mkdir "%~1"
pushd "%~dp0"
cl /nologo /std:c++20 /O2 /EHsc /MT /LD /DWIN32_LEAN_AND_MEAN /DNOMINMAX "/Fo%~1/" GlassBackdrop.cpp LiquidLens.cpp /link "/OUT:%~1\GlassBackdrop.dll" "/IMPLIB:%~1\GlassBackdrop.lib" windowsapp.lib CoreMessaging.lib dwmapi.lib dxguid.lib
set "glassExit=%errorlevel%"
popd
exit /b %glassExit%
