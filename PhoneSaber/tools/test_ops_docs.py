"""当日資料の復旧手順を検査する。実機での操作・通信成功を証明するテストではない。"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
PHONE = ROOT / "PhoneSaber"


def read(path):
    return (PHONE / path).read_text(encoding="utf-8")


class OperationsDocsTests(unittest.TestCase):
    def test_auto_latency_needs_no_owner_measurement_steps(self):
        staff = read("docs/STAFF_MANUAL.md")
        runbook = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        for label in ("遅延の目安", "自動記録", "数値入力", "コピー"):
            self.assertIn(label, staff)
        self.assertIn("event=latency-estimate", runbook)
        self.assertIn("--estimates", runbook)
        self.assertIn("旧アプリ/P2P", runbook)
        self.assertIn("投影機処理は未算入", runbook)

    def test_staff_recovery_is_two_columns_and_has_no_network_jargon(self):
        recovery = read("docs/STAFF_MANUAL.md").split("## 3. 困ったとき", 1)[1].split("## 4.", 1)[0]
        rows = [line for line in recovery.splitlines() if line.startswith("|")]
        self.assertGreater(len(rows), 8)
        for row in rows:
            self.assertEqual(row.count("|"), 3, row)
        for word in ("UDP", "P2P", "bridge"):
            self.assertNotIn(word, recovery)
        self.assertNotIn("手動IP", recovery)

    def test_staff_wrong_station_does_not_ignore_saved_ip(self):
        row = next(line for line in read("docs/STAFF_MANUAL.md").splitlines()
                   if line.startswith("| 別の台"))
        self.assertIn("技術担当", row)
        self.assertIn("PC番号", row)

    def test_staff_checks_wifi_and_covers_manual_destination_warning(self):
        doc = read("docs/STAFF_MANUAL.md")
        opening = doc.split("## 1.", 1)[1].split("## 2.", 1)[0]
        self.assertIn("同じ Wi-Fi", opening)
        self.assertIn('iPhoneに「応答がありません」を含む表示', doc)

    def test_staff_pdf_is_rebuilt_by_the_tool_and_fits_two_pages(self):
        opening = read("docs/claude/EVENT_DAY_RUNBOOK.md").split("## 困ったら", 1)[0]
        self.assertIn("STAFF_MANUAL.pdf", opening)
        self.assertIn("build_staff_manual_pdf.py", opening)
        import build_staff_manual_pdf as builder
        self.assertEqual(builder.page_count((PHONE / "docs/STAFF_MANUAL.pdf").read_bytes()), 2)
        page = builder.convert(read("docs/STAFF_MANUAL.md"))
        # 配布用 PDF の元になる HTML に、復旧表の新しい行と改ページが入っている。
        for label in ("充電ケーブルを差し直す", "60秒待つ", 'class="pagebreak"', "<table>"):
            self.assertIn(label, page)
        self.assertNotIn("**", page)
        self.assertNotIn("](", page)

    def test_opening_restores_normal_sender_and_overlay_settings(self):
        doc = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        checklist = doc.split("## 開場直前チェック", 1)[1].split("## 困ったら", 1)[0]
        for setting in ("診断録画OFF", "fps自動比較OFF", "同期チェックOFF"):
            self.assertIn(setting, checklist)
        self.assertIn("iPhoneのDebug版", checklist)
        self.assertIn("fps自動比較をOFF", doc.split("### 会場でのネットワーク確認", 1)[1])
        self.assertIn("payload解析: OK", checklist)
        self.assertIn("payload解析: OK", read("windows/README.md"))
        stats = (ROOT / "Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberInputStats.cs").read_text()
        self.assertIn("payload解析: {6}", stats)
        self.assertIn('stats.PayloadParsed ? "OK" : "NG"', stats)

    def test_staff_quit_stop_and_frozen_game_have_distinct_actions(self):
        staff = read("docs/STAFF_MANUAL.md")
        for symptom in ("異常終了", "正常終了", "停止ファイル", "画面が固まった"):
            with self.subTest(symptom=symptom):
                self.assertIn(symptom, staff)
        self.assertIn("60秒", staff)
        self.assertIn('HANG_SECONDS="${PHONESABER_HANG_SECONDS:-45}"', read("mac/saber-watchdog.sh"))

    def test_android_foreground_and_network_recovery_is_automatic(self):
        doc = read("android/README.md")
        self.assertNotIn("確認後に開始し直す", doc)
        self.assertIn("前面に戻すと自動再開", doc)
        self.assertIn("Wi-Fi切替では送信意思を保ち", doc)
        activity = read("android/app/src/main/java/jp/phonesaber/sender/MainActivity.kt")
        self.assertIn("stopSending(clearIntent = false)", activity)
        self.assertIn("resumePolicy.foreground", activity)

    def test_public_hotspot_is_not_a_private_network_requirement(self):
        for path in ("docs/claude/EVENT_DAY_RUNBOOK.md", "windows/README.md", "android/README.md"):
            with self.subTest(path=path):
                doc = read(path)
                for stale in ("WindowsはPrivate設定", "共有接続もPrivate", "Privateネットワーク、",
                              "プライベート ネットワーク** に設定します", "**プライベートネットワーク**で許可"):
                    self.assertNotIn(stale, doc)
                self.assertIn("Public", doc)
        self.assertIn("Profile = 'Private,Public'", read("windows/Allow-PhoneSaber-Firewall.ps1"))

    def test_exposure_persists_and_also_affects_normal_sending(self):
        doc = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        self.assertIn("通常送信にも適用", doc)
        self.assertIn("録画をOFFにしても", doc)
        self.assertIn("setting.store(in: .standard)", read("ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift"))

    def test_preopening_checklist_covers_both_stations_in_order(self):
        doc = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        self.assertIn("## 開場直前チェック", doc)
        checklist = doc.split("## 開場直前チェック", 1)[1].split("## 困ったら", 1)[0]
        self.assertIn("A台", checklist)
        self.assertIn("B台", checklist)
        for item in ("STOP", "手動IP", "F4", "F7", "F8", "F9", "正常終了", "競合", "自動再開"):
            with self.subTest(item=item):
                self.assertIn(item, checklist)
        self.assertLess(checklist.index("STOP"), checklist.index("F8"))
        self.assertIn("phonesaber-watchdog.*/heartbeat-*", read("mac/README.md"))
        self.assertIn("LastWriteTimeUtc", read("windows/README.md"))

    def test_ios_probe_launcher_exists_and_measurement_is_enabled(self):
        doc = read("ios/PhoneSaberSender/README.md").split("## 定義", 1)[0]
        self.assertNotIn("`run_debug.command`", doc)
        self.assertIn("`start_phone_saber_latency.command`", doc)
        self.assertTrue((PHONE / "start_phone_saber_latency.command").is_file())
        self.assertIn("遅延計測モード", doc)
        self.assertIn("遅延計測を開始", doc)
        self.assertIn("接続", doc)

    def test_new_messages_are_described_without_inventing_missing_ui(self):
        doc = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        for label in ("PC（Unity）から応答がありません", "通信ログ", "振りだけ録画を開始", "約51秒",
                      "別のスマホもこのPCへ送信中です", "警告だけで、座標は弾かない",
                      "Mac 受信済み", "未転送の録画を今すぐ送る", "手動 IP を解除して自動探索に戻す",
                      "uploaded selected bundle"):
            with self.subTest(label=label):
                self.assertIn(label, doc)
        # 文書に書いた表示は、実物のコードにある文言と一致させる（無い UI を書かない）。
        stats = (ROOT / "Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberInputStats.cs").read_text()
        self.assertIn("別のスマホもこのPCへ送信中です", stats)
        self.assertIn("public static bool Enforce = false;", stats)
        transfer = read("ios/PhoneSaberSender/PhoneSaberSender/DebugBundleTransfer.swift")
        for label in ("転送待ち", "転送中", "Mac 受信済み", "転送失敗"):
            self.assertIn(label, transfer)
        ui = read("ios/PhoneSaberSender/PhoneSaberSender/ContentView.swift")
        for label in ("通信ログ", "振りだけ録画を開始"):
            self.assertIn(label, ui)

    def test_mac_stop_and_game_running_messages_are_literal(self):
        doc = read("mac/README.md")
        for message in ("STOP ファイルがあるため", "この PC では既に監視が動いています", "が既に起動しています"):
            self.assertIn(message, doc)
            self.assertIn(message, read("mac/saber-watchdog.sh"))

    def test_windows_f9_requires_restoring_camera_and_has_block_progress(self):
        doc = read("windows/README.md")
        self.assertIn("8ブロック", doc)
        self.assertIn("カメラをプレイヤー側へ戻し", doc)
        self.assertIn("F7", doc)
        self.assertIn("F4", doc)

    def test_active_ios_commands_do_not_depend_on_developers_checkout(self):
        doc = read("ios/PhoneSaberSender/README.md").split("## レビュー修正後の検証記録", 1)[0]
        self.assertNotIn("/Users/satoshi/", doc)
        self.assertIn("open start_phone_saber_latency.command", doc)
        self.assertIn("bash ios/PhoneSaberSender/run-static-tests.sh", doc)

    def test_calibration_button_labels_match_overlay(self):
        doc = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        overlay = (ROOT / "Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberOperatorOverlay.cs").read_text()
        for label in ("4隅の測定を開始 [F7]", "この隅を採取 [F7]", "BLUEの剣を採取（OFFならRED）",
                      "位置補正をON（この台だけ）", "この台の位置補正をリセット（OFFに戻す）"):
            with self.subTest(label=label):
                self.assertIn(label, doc)
                self.assertIn(label, overlay)

    def test_sender_ip_is_the_phone_and_status_may_include_old_logs(self):
        doc = read("docs/claude/EVENT_DAY_RUNBOOK.md")
        for detail in ("送信先PCのIPではない", "最後の `bridge starting`", "古い起動の記録", "ゲームが正常でもWARN"):
            self.assertIn(detail, doc)
        self.assertIn('"[PhoneSaber][P2P] bridge starting"', read("ios/PhoneSaberSender/Tools/phone_saber_status.py"))

    def test_ios_operator_entrypoint_exists(self):
        path = PHONE / "ios/README.md"
        self.assertTrue(path.is_file(), "指定のiPhone当日資料がない")
        doc = read("ios/README.md")
        for label in ("通常送信を開始", "手動IP", "露出実験", "PhoneSaberSender/README.md"):
            self.assertIn(label, doc)

    def test_relative_document_links_exist(self):
        for path in ("docs/STAFF_MANUAL.md", "docs/claude/EVENT_DAY_RUNBOOK.md", "mac/README.md",
                     "windows/README.md", "android/README.md", "ios/README.md", "ios/PhoneSaberSender/README.md"):
            for target in re.findall(r"\]\(([^)]+)\)", read(path)):
                if "://" not in target and not target.startswith("#"):
                    with self.subTest(path=path, target=target):
                        self.assertTrue((PHONE / path).parent.joinpath(target.split("#")[0]).exists())


if __name__ == "__main__":
    unittest.main()
