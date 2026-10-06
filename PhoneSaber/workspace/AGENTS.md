# ワークスペース案内

`/Users/satoshi/縁日/` 自体はGitリポジトリではありません。作業前に対象パスが属するリポジトリを確認してください。

## 主な場所

- `GitHub/3D-Saber/`: 唯一の本番リポジトリ。Unity root はここ、iPhone/Android と Mac/Python診断ツール、テストは `PhoneSaber/` 内。
- 旧 `school-festival` repo は2026-10-06に履歴ごと `3D-Saber/PhoneSaber/` へ統合し、アーカイブ済み。別途cloneしない。
- 旧 `GitHub/school-festival-experimental-recognition/` worktree は2026-09-28のブランチ整理で存在しません（確認済み）。作り直す場合はGit worktreeとして扱い、通常フォルダとして移動・削除しないでください。
- `docs/`: ワークスペース全体の設計資料。認識・通信・Unity入力・計測に関わる作業前に `docs/ennichi-camera-system.md` を確認してください。
- `PROJECT_STRUCTURE.md`: 詳細な構成説明。

本番経路:

```text
PhoneSaberSender
  → UDP 5005 / 5006
  → Unity InputPoint
  → SaberInputBridge
  → ゲーム処理
```

## 変更時のルール

- 変更前に対象がどのGitリポジトリに属するか確認し、無関係なリポジトリを変更しない。
- 既存の未コミット変更を上書きしない。
- 整理目的でSwift認識アルゴリズムやUDP仕様を変更しない。
- UnityのScene、Prefab、`.meta`を不用意に変更しない。
- worktreeを通常フォルダとして移動・削除しない。
- 不明なファイルは「不要そう」という理由だけで削除しない。
- 詳細構造は `PROJECT_STRUCTURE.md` を参照する。
