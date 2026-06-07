@echo off
setlocal
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo ^>^> C# compiler not found.
  exit /b 1
)

echo [1/3] Copying offline HTML...
copy /Y "..\pdf-signer-offline.html" "app.html" >nul
if errorlevel 1 (
  echo ^>^> Missing ..\pdf-signer-offline.html. Run "node build-offline.js" first.
  exit /b 1
)

echo [2/3] Building exe...
"%CSC%" /nologo /target:exe /out:"..\pdf-signer.exe" /resource:"app.html,app.html" "launcher.cs"
if errorlevel 1 (
  echo ^>^> Build failed.
  exit /b 1
)

echo [3/3] Done: ..\pdf-signer.exe
endlocal
