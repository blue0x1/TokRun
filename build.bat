@echo off
setlocal
cd /d "%~dp0"

where msbuild >nul 2>nul
if %ERRORLEVEL%==0 (
  echo [*] Building Release AnyCPU...
  msbuild TokRun.sln /p:Configuration=Release /p:Platform="Any CPU"
  if errorlevel 1 exit /b 1
  echo [*] Building Release x64...
  msbuild TokRun.sln /p:Configuration=Release /p:Platform=x64
  if errorlevel 1 exit /b 1
  echo [*] Building Release x86...
  msbuild TokRun.sln /p:Configuration=Release /p:Platform=x86
  if errorlevel 1 exit /b 1
  echo [+] Build complete.
  exit /b 0
)

echo [!] msbuild not found. Trying .NET Framework csc.exe.
set CSC64=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set CSC32=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

set BUILT=0

if exist "%CSC64%" (
  if not exist TokRun\bin\Release\x64 mkdir TokRun\bin\Release\x64
  if exist TokRun\bin\Release\x64\TokRun.exe del /f /q TokRun\bin\Release\x64\TokRun.exe
  echo [*] Building x64 with csc.exe...
  "%CSC64%" /nologo /optimize+ /platform:x64 /out:TokRun\bin\Release\x64\TokRun.exe TokRun\*.cs
  if errorlevel 1 exit /b 1
  set BUILT=1
)

if exist "%CSC32%" (
  if not exist TokRun\bin\Release\x86 mkdir TokRun\bin\Release\x86
  if exist TokRun\bin\Release\x86\TokRun.exe del /f /q TokRun\bin\Release\x86\TokRun.exe
  echo [*] Building x86 with csc.exe...
  "%CSC32%" /nologo /optimize+ /platform:x86 /out:TokRun\bin\Release\x86\TokRun.exe TokRun\*.cs
  if errorlevel 1 exit /b 1
  set BUILT=1
)

if "%BUILT%"=="0" (
  echo [-] Could not find msbuild or .NET Framework 4 csc.exe.
  exit /b 1
)

echo [+] Build complete.
exit /b 0
