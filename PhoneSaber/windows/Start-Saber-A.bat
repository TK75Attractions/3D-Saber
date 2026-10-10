@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
rem 実際のビルド先に合わせて、次の exe パスを変更してください。
set "GAME_EXE=%~dp0..\..\Builds\Windows\3D-Saber.exe"
set "LOG_FILE=%~dp0Start-Saber-A.log"
set "STOP_FILE=%~dp0Start-Saber-A.STOP"
set "RESTART_COUNT=0"

if not exist "%GAME_EXE%" (
    echo ゲーム exe が見つかりません: "%GAME_EXE%"
    echo この bat の GAME_EXE をビルド済み exe のパスに変更してください。
    pause
    exit /b 1
)

rem 前回の STOP が残っていると何も起動しないので、黙って閉じずに知らせる。
set "STOP_FOUND="
if exist "%STOP_FILE%" set "STOP_FOUND=%STOP_FILE%"
if exist "%~dp0STOP" set "STOP_FOUND=%~dp0STOP"
if defined STOP_FOUND (
    >>"%LOG_FILE%" echo [%date% %time%] station=A not-started STOP-file restart=%RESTART_COUNT%
    echo 停止ファイルがあるため起動しません: "%STOP_FOUND%"
    echo このファイルを削除してから、もう一度起動してください。
    pause
    exit /b 1
)

rem /wait で Player の終了コードを待つ。Ctrl+C は「バッチ ジョブを終了しますか」で Y を選ぶと監視終了。
:launch
if exist "%STOP_FILE%" goto stopped
if exist "%~dp0STOP" goto stopped
>>"%LOG_FILE%" echo [%date% %time%] station=A launch restart=%RESTART_COUNT%
echo 台 A を起動します。再起動回数: %RESTART_COUNT%  [Ctrl+C: 監視終了]
for %%I in ("%GAME_EXE%") do start "" /wait /D "%%~dpI" "%%~fI" -phonesaberStation A
set "GAME_EXIT=%ERRORLEVEL%"
>>"%LOG_FILE%" echo [%date% %time%] station=A exit-code=%GAME_EXIT% restart=%RESTART_COUNT%
if "%GAME_EXIT%"=="0" exit /b 0
if exist "%STOP_FILE%" goto stopped
if exist "%~dp0STOP" goto stopped
set /a RESTART_COUNT+=1 >nul
echo 異常終了 code=%GAME_EXIT%。5秒後に再起動します。
rem 1秒ごとに STOP を確認しながら待つ。timeout は入力がリダイレクトされていると即失敗するので、
rem そのときは ping で1秒待つ(待機の失敗を理由に監視を止めない)。
set "WAIT_LEFT=5"
:delay
if exist "%STOP_FILE%" goto stopped
if exist "%~dp0STOP" goto stopped
if %WAIT_LEFT% LEQ 0 goto launch
timeout /t 1 /nobreak >nul 2>&1 || ping -n 2 127.0.0.1 >nul
set /a WAIT_LEFT-=1 >nul
goto delay

:stopped
>>"%LOG_FILE%" echo [%date% %time%] station=A watchdog-stopped STOP-file restart=%RESTART_COUNT%
echo 停止ファイルを検出したため、監視を終了しました。次回起動前に削除してください。
exit /b 0
