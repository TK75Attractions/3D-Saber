# 縁日ワークスペースの構成

`/Users/satoshi/縁日/` は単一のGitリポジトリではなく、複数のアプリ、試作プロジェクト、独立したGitリポジトリをまとめた作業フォルダです。主要なiPhoneカメラ入力の経路は次のとおりです。

```text
iPhone PhoneSaberSender
  → UDP 5005 / 5006
  → Unity InputPoint
  → SaberInputBridge
  → ゲーム
```

BonjourはMacのホストを見つけるために使います。座標データは引き続きUDPで送ります。UnityとMac/Pythonのデバッグ受信ツールは同じUDPポートを使うため、同時には起動しません。

2026-10-06 に本番repoを統合しました。`GitHub/` 内は `3D-Saber/` の1つだけを使い、PhoneSaber はその `PhoneSaber/` 内にあります。旧 `school-festival` はアーカイブ済みで、別途cloneは不要です。

## 主なプロジェクト

### `GitHub/3D-Saber/PhoneSaber/`（同一repo内）

iPhoneの認識・送信アプリと、カメラや通信を調べるMac側ツールを含みます。

- **iPhoneアプリ:** `ios/PhoneSaberSender/PhoneSaberSender/` にSwiftアプリ本体があります。BonjourでMacを探し、見つからない場合は画面からホストを指定できます。
- **Mac/Pythonデバッグ:** ルートの `udp_receive_probe.py` と `saber_camera_test.html` がUDP受信・状態表示・遅延計測を行います。`run_debug.command` と `start_phone_saber_latency.command` はこの画面を起動します。`camera.py`、`smartphone_camera.py`、カメラベンチマーク、Continuity Camera用コードは開発・比較用です。
- **テスト:** `test_*.py`、`native/test_capture.m`、`ios/PhoneSaberSenderTests/` にテストがあります。iOSテスト用の画像やフレームは `ios/PhoneSaberSenderTests/Fixtures/` にあります。
- **補助ツール:** `ios/PhoneSaberSender/Tools/` には録画・検出結果を調べるPythonツールがあります。

Mac側のPython受信ツールはUnityの代替受信先ではなく、診断用です。Unityと同じ UDP 5005 / 5006 を待ち受けるため、利用時はUnityを停止します。

### `GitHub/3D-Saber/`

縁日カメラ入力を受け取るUnity本体です。このフォルダ直下に `Assets/`、`Packages/`、`ProjectSettings/` があります。

- UDP受信は `Assets/Scripts/Managers/Inputsystem/Input/InputPoint.cs` が担当します。
- ブレードの入力は `Assets/Scripts/Saber/SaberInputBridge.cs` が担当します。
- Unity Editor / macOS Standaloneでは、`PhoneSaberBonjourPublisher.cs` がMac発見用のBonjourサービスを公開します。
- Unityのテストは `Assets/Tests/Editor/` と `Assets/Tests/PlayMode/` にあります。

### 旧 `school-festival-experimental-recognition` worktree

ブランチ `experimental-recognition-20260923` 用のGit worktreeでしたが、2026-09-28のブランチ整理後は存在しません。旧 `school-festival` repo は2026-10-06に履歴ごと `GitHub/3D-Saber/PhoneSaber/` へ統合し、アーカイブ済みです。`GitHub/` の本番repoは `3D-Saber/` の1つだけです。再作成する場合はGit worktreeとして扱い、通常フォルダとして移動・削除しません。

## ドキュメント

ルートの `docs/` に、iPhone認識、ネットワーク、Unity入力、Macデバッグ、遅延計測の仕様をまとめた `ennichi-camera-system.md` があります。各プロジェクト内のREADMEやAGENTS.mdは、そのプロジェクト固有の操作・構成案内です。

## ルートの試作・設計資料

- `3D弾幕/`、`譜面制作/`、`My project/` は、それぞれ独立したUnity試作プロジェクトです。PhoneSaberの本番経路で使うUnityプロジェクトは `GitHub/3D-Saber/` です。
- `BeatSaver_device/`、`acceration.detect/`、`testforxiao/` などはESP32/PlatformIO系の試作です。
- 旧PhoneSaberSender（ルート直下のHello World版）は2026-09-24に削除済みです。本番版は `GitHub/3D-Saber/PhoneSaber/ios/PhoneSaberSender/` です。
- `.stl`、`.3mf`、`.gcode`、`.ai`、`.png` などはハードウェアの設計・製作資料です。`make_reinforced_stl.rb` は補強STLを生成します。

## ローカルコピーを削除したリポジトリ

`Bullet-Hell`、`Note-Recorder`、`Racing` のローカルコピーは2026-09-24に削除済みです。GitHub上のremoteには変更を加えていません。
