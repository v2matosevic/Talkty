@echo off
REM Talkty Installer Signing Script
REM
REM Prerequisites:
REM 1. Windows SDK installed (for signtool.exe)
REM 2. Code signing certificate (.pfx file) or installed in certificate store
REM
REM Usage with PFX file:
REM   sign-installer.bat "path\to\certificate.pfx" "password"
REM
REM Usage with installed certificate:
REM   sign-installer.bat

setlocal

set INSTALLER=output\TalktySetup-1.0.0.exe
set TIMESTAMP_URL=http://timestamp.digicert.com
set APP_NAME=Talkty
set APP_URL=https://version2.hr

REM Find signtool.exe
set SIGNTOOL=
for %%i in (
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe"
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe"
    "C:\Program Files (x86)\Windows Kits\10\bin\x64\signtool.exe"
) do (
    if exist %%i set SIGNTOOL=%%i
)

if "%SIGNTOOL%"=="" (
    echo ERROR: signtool.exe not found. Install Windows SDK.
    echo Download: https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/
    exit /b 1
)

echo Using signtool: %SIGNTOOL%
echo.

if "%~1"=="" (
    REM Sign using certificate from Windows certificate store
    echo Signing with certificate from store...
    %SIGNTOOL% sign /a /tr %TIMESTAMP_URL% /td sha256 /fd sha256 /d "%APP_NAME%" /du "%APP_URL%" "%INSTALLER%"
) else (
    REM Sign using PFX file
    echo Signing with PFX file: %~1
    %SIGNTOOL% sign /f "%~1" /p "%~2" /tr %TIMESTAMP_URL% /td sha256 /fd sha256 /d "%APP_NAME%" /du "%APP_URL%" "%INSTALLER%"
)

if %ERRORLEVEL% EQU 0 (
    echo.
    echo SUCCESS: Installer signed successfully!
    echo.
    echo Verifying signature...
    %SIGNTOOL% verify /pa "%INSTALLER%"
) else (
    echo.
    echo ERROR: Signing failed. Check your certificate.
)

endlocal
