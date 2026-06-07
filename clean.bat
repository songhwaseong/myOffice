@echo off
setlocal
cd /d "%~dp0"

echo Cleaning generated files...

if exist "build-tmp" (
  echo - build-tmp
  rmdir /s /q "build-tmp"
)

if exist "pdf-signer-offline.html" (
  echo - pdf-signer-offline.html
  del /f /q "pdf-signer-offline.html"
)

if exist "pdf-signer.exe" (
  echo - pdf-signer.exe
  del /f /q "pdf-signer.exe"
)

if exist "desktop\app.html" (
  echo - desktop\app.html
  del /f /q "desktop\app.html"
)

echo Done.
endlocal
