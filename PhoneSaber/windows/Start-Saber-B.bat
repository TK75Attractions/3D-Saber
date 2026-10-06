@echo off
chcp 65001 >nul
rem 実際のビルド先に合わせて、次の exe パスを変更してください。
set "GAME_EXE=%~dp0..\..\Builds\Windows\3D-Saber.exe"

if not exist "%GAME_EXE%" (
    echo ゲーム exe が見つかりません: "%GAME_EXE%"
    echo この bat の GAME_EXE をビルド済み exe のパスに変更してください。
    pause
    exit /b 1
)

rem exe のフォルダを作業ディレクトリにし、台名を起動引数で指定します。
for %%I in ("%GAME_EXE%") do start "" /D "%%~dpI" "%%~fI" -phonesaberStation B
if errorlevel 1 (
    echo ゲームを起動できませんでした。
    pause
    exit /b 1
)
exit /b 0
