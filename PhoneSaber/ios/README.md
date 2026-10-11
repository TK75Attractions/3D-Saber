# iPhone 当日用sender

アプリのXcode projectは `PhoneSaberSender/PhoneSaberSender.xcodeproj`。カメラ・ローカルネットワークを許可します。
本番はPCの台別launcherでUnityを起動してから、iPhoneの「台」を同じA/Bにして「通常送信を開始」。
Mac＋iPhoneは同じWi-Fiで自動探索、Windows＋iPhoneは現在のPCのIPv4を手動IPに入力します。
保存した手動IPは台を変えても残ります（台の横に「手動 IP 指定中: 台の選択は使われません」と出ます）。Macの自動探索へ戻すときは停止して「手動 IP を解除して自動探索に戻す」を押します。

- 「PC（Unity）から応答がありません」が出たらゲーム起動・Wi-Fi・台・IPを確認。「接続」の「通信ログ」も確認します。
- 送信中だった場合は前面復帰で自動再開。「停止」後の前面復帰では再開しません。自動開始は既定OFF、ONなら次のアプリ起動で開始します。
- Debug Recording内の「露出実験」は通常送信にも適用・保存されます。実験後は「自動(既定)」へ戻します。
- 「振りだけ録画を開始（約51秒・自動で終了）」後の「完了」は録画完了です。転送完了はMac Status／inboxで同じsessionIDを確認します（現行画面に転送状況の専用表示はありません）。

A/Bの開場チェック・F4/F7/F8/F9・復旧・片付けは[当日runbook](../docs/claude/EVENT_DAY_RUNBOOK.md)。
単独の遅延計測と詳しい画面説明は[PhoneSaberSender README](PhoneSaberSender/README.md)（Unityを停止して使う計測ツール）。
