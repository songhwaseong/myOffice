@echo off
setlocal
cd /d "%~dp0"

echo [1/3] Copying offline HTML...
copy /Y "..\pdf-signer-offline.html" "app.html" >nul
if errorlevel 1 (
  echo ^>^> Missing ..\pdf-signer-offline.html. Run "node build-offline.js" first.
  exit /b 1
)

echo [2/3] Building exe...
go build -ldflags "-s -w" -o "..\pdf-signer.exe" .
if errorlevel 1 (
  echo ^>^> Build failed. Install Go and make sure it is on PATH.
  exit /b 1
)

echo [3/3] Done: ..\pdf-signer.exe
endlocal
