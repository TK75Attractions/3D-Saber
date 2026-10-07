@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
rem 実際のビルド先に合わせて、次の exe パスを変更してください。
set "GAME_EXE=%~dp0..\..\Builds\Windows\3D-Saber.exe"
set "LOG_FILE=%~dp0Start-Saber-B.log"
set "STOP_FILE=%~dp0Start-Saber-B.STOP"
set "RESTART_COUNT=0"

if not exist "%GAME_EXE%" (
    echo ゲーム exe が見つかりません: "%GAME_EXE%"
    echo この bat の GAME_EXE をビルド済み exe のパスに変更してください。
    pause
    exit /b 1
)

rem /wait で Player の終了コードを待つ。Ctrl+C はバッチを終了して再起動を止める。
:launch
if exist "%STOP_FILE%" goto stopped
if exist "%~dp0STOP" goto stopped
>>"%LOG_FILE%" echo [%date% %time%] station=B launch restart=%RESTART_COUNT%
echo 台 B を起動します。再起動回数: %RESTART_COUNT%  [Ctrl+C: 監視終了]
for %%I in ("%GAME_EXE%") do start "" /wait /D "%%~dpI" "%%~fI" -phonesaberStation B
set "GAME_EXIT=%ERRORLEVEL%"
>>"%LOG_FILE%" echo [%date% %time%] station=B exit-code=%GAME_EXIT% restart=%RESTART_COUNT%
if "%GAME_EXIT%"=="0" exit /b 0
if exist "%STOP_FILE%" goto stopped
if exist "%~dp0STOP" goto stopped
set /a RESTART_COUNT+=1 >nul
echo 異常終了 code=%GAME_EXIT%。5秒後に再起動します。
rem 待機の失敗(Ctrl+C 等)を再起動と扱わない。
timeout /t 5 /nobreak >nul
if errorlevel 1 goto stopped
goto launch

:stopped
>>"%LOG_FILE%" echo [%date% %time%] station=B watchdog-stopped restart=%RESTART_COUNT%
exit /b 0
