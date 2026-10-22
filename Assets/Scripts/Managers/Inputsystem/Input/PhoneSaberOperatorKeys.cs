using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;

// 運営のキーボード操作（2026-10-18 ユーザー: スマホに無視させる操作は F8 の画面ではなくキーで直接）。
// Ctrl+Shift+文字。ゲームが使う A/S/D/W・数字・矢印・Space・Enter・Esc・N とは重ねない。
// 押すと PhoneSaberDiscoveryResponder.QueueCommand で、探索応答と一緒にスマホへ 10 秒送り、返事を画面に出す。
public sealed class PhoneSaberOperatorKeys : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        ResetDefaultsState();
        var go = new GameObject("PhoneSaber Operator Keys");
        go.AddComponent<PhoneSaberOperatorKeys>().toast = go.AddComponent<PhoneSaberOperatorToast>();
        DontDestroyOnLoad(go);
    }

    PhoneSaberOperatorToast toast;
    static bool lastSentTrackLock = true; // スマホの新しい既定 ON に合わせる。
    static int shutterIndex; // OFF → 1/500 → 1/1000 → 1/2000 → OFF
    static int exposureIndex = 2; // 開始時 -2、次に 0 → -1 → -2（スマホ側が保存する）

    // Unity API を使わず、defaults のキュー投入と一緒に Mac 側も既定に戻す。
    public static void ResetDefaultsState()
    {
        Volatile.Write(ref lastSentTrackLock, true);
        Volatile.Write(ref exposureIndex, 2);
        Volatile.Write(ref shutterIndex, 0);
    }

    // 運営の操作を除いて、このフレームにキーが押されたか。F1〜F12(F4 の投影切替・F7〜F10 など)と、
    // Ctrl・Alt・Windows を押しながらのキー(Ctrl+Shift+文字)、修飾キーだけの押下は数えない。
    // 「何かキーで演出を飛ばす・案内を閉じる」が運営の操作で起きないようにする。
    public static bool VisitorKeyPressedThisFrame(Keyboard keyboard)
    {
        if (keyboard == null || !keyboard.anyKey.wasPressedThisFrame) return false;
        if (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed || keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed) return false;
        foreach (var key in keyboard.allKeys)
            if (key != null && key.wasPressedThisFrame && !IsOperatorOnlyKey(key.keyCode)) return true;
        return false;
    }
    static bool IsOperatorOnlyKey(Key key) => key >= Key.F1 && key <= Key.F12
        || key == Key.LeftShift || key == Key.RightShift || key == Key.LeftCtrl || key == Key.RightCtrl
        || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftMeta || key == Key.RightMeta || key == Key.ContextMenu;

    // 押されたキーに対応する指示と表示。対応しないキーは null。
    public static (string command, string label)? CommandFor(Key key, int currentExposureIndex, out int nextExposureIndex, bool currentTrackLock = true, int currentShutterIndex = 0)
    {
        nextExposureIndex = currentExposureIndex;
        switch (key)
        {
            case Key.R: return ("ignore=red", "スマホ: 今の赤を無視");
            case Key.B: return ("ignore=blue", "スマホ: 今の青を無視");
            case Key.I: return ("ignore=both", "スマホ: 今の赤と青をまとめて無視");
            case Key.C: return ("clear-ignore=1", "スマホ: 無視をすべて消す");
            // iPhone の自動クリップ（c81113d）: 今の前後数秒を保存し、曲の合間に Mac の diagnostics-inbox/autoclips へ送る。
            case Key.K: return ("clip=1", "スマホ: 今の場面を切り抜いて保存");
            case Key.P: return (PhoneSaberDiscoveryResponder.LearnAreaCommand,
                $"スマホ: プレイ範囲を覚える（{PhoneSaberDiscoveryResponder.LearnAreaSeconds}秒、範囲いっぱいに振る）");
            case Key.X: return (PhoneSaberDiscoveryResponder.ClearAreaCommand, "スマホ: プレイ範囲を消す");
            case Key.S: return ShutterCommand(currentShutterIndex, out _);
            case Key.T: return TrackLockCommand(currentTrackLock);
            case Key.E:
                var choices = PhoneSaberDiscoveryResponder.ExposureBiasChoices;
                nextExposureIndex = (currentExposureIndex + 1) % choices.Length;
                int ev = choices[nextExposureIndex];
                return (PhoneSaberDiscoveryResponder.ExposureBiasCommand(ev), $"スマホ露出: {(ev == 0 ? "0（OFF）" : ev.ToString())}");
            default: return null;
        }
    }

    public static (string command, string label) ShutterCommand(int index, out int nextIndex)
    {
        var choices = PhoneSaberDiscoveryResponder.ShutterChoices;
        nextIndex = (index + 1) % choices.Length;
        int value = choices[nextIndex];
        return (PhoneSaberDiscoveryResponder.ShutterCommand(value), "iPhone固定シャッター: " + ShutterLabel(value));
    }

    static string ShutterLabel(int value) => value == 0 ? "OFF（自動露出）" : "1/" + value + " 秒";

    public static void SelectShutter(int value)
    {
        int index = System.Array.IndexOf(PhoneSaberDiscoveryResponder.ShutterChoices, value);
        if (index < 0) return;
        Volatile.Write(ref shutterIndex, index);
        PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.ShutterCommand(value));
    }

    public static (string command, string label) TrackLockCommand(bool lastSent)
    {
        bool enabled = !lastSent;
        return ("track-lock=" + (enabled ? "1" : "0"), "スマホ: 剣を追いかける " + (enabled ? "ON" : "OFF"));
    }

    static readonly Key[] Keys = { Key.R, Key.B, Key.I, Key.C, Key.K, Key.P, Key.X, Key.E, Key.T, Key.S };

    string displayed = "";
    int displayedId;
    int toastRevision = -1;
    double nextToastRefresh = double.PositiveInfinity;
    public const string NoReplyMessage = "スマホから返事がありません（送信中か・同じネットワークか確認）";
    public static string LastResultText => StatusText(PhoneSaberDiscoveryResponder.Acknowledgements,
        SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));

    public static string PendingLabel(string fields)
    {
        switch (fields)
        {
            case "ignore=red": return "スマホ: 今の赤を無視";
            case "ignore=blue": return "スマホ: 今の青を無視";
            case "ignore=both": return "スマホ: 今の赤と青をまとめて無視";
            case "clear-ignore=1": return "スマホ: 無視をすべて消す";
            case "clear-area=1": return "スマホ: プレイ範囲を消す";
            case "clip=1": return "スマホ: 今の場面を切り抜いて保存";
            case PhoneSaberDiscoveryResponder.DefaultsCommand: return "スマホ: 既定に戻す（剣を追いかける ON・露出 −2）";
            default:
                if (fields.StartsWith("learn-area=")) return "スマホ: プレイ範囲を覚える（" + fields.Substring(11) + "秒、範囲いっぱいに振る）";
                if (fields.StartsWith("shutter=")) return "iPhone固定シャッター: " + (fields == "shutter=0" ? "OFF（自動露出）" : "1/" + fields.Substring(8) + " 秒");
                if (fields.StartsWith("exposure-bias=")) return "スマホ露出: " + (fields.Substring(14) == "0" ? "0（OFF）" : fields.Substring(14));
                if (fields.StartsWith("track-lock=")) return "スマホ: 剣を追いかける " + (fields.EndsWith("1") ? "ON" : "OFF");
                if (fields.StartsWith("auto-clip=")) return "スマホ: 自動クリップ " + (fields.EndsWith("1") ? "ON" : "OFF");
                return "スマホ: " + fields;
        }
    }

    // GUI・時計・通信に依存しない表示。F8 とトーストは同じ文字列を読む。
    public static string StatusText(PhoneSaberAcknowledgementStore store, double now)
    {
        if (!store.Latest(out int id, out string fields, out double queuedAt)) return "";
        var replies = store.Read(id);
        if (replies.Length > 0) return string.Join("\n", replies.Select(a => ResultText(a, fields)));
        return now - queuedAt >= 3 ? NoReplyMessage
            : PendingLabel(fields) + "（送信中）";
    }

    public static string ResultText(PhoneSaberAcknowledgement ack, string command)
    {
        string phone = ack.Platform == "ios" ? "iPhone" : "Android";
        if (ack.Station != "none") phone += "（台" + ack.Station + "）";
        string result;
        switch (ack.Result)
        {
            case "ignored":
                string[] counts = ack.Detail.Split('-');
                int red = int.Parse(counts[0].Substring(3)), blue = int.Parse(counts[1].Substring(4));
                result = (red > 0 && blue > 0 ? "赤と青" : red > 0 ? "赤" : "青") + "を無視しました（赤" + red + "・青" + blue + "）";
                break;
            case "nothing":
                string color = command == "ignore=red" ? "赤が" : command == "ignore=blue" ? "青が" : "赤も青も";
                result = "今は" + color + "映っていないので無視するものがありません";
                break;
            case "cleared": result = ack.Detail == "area" ? "プレイ範囲を消しました" : "無視をすべて消しました"; break;
            case "learning": result = command.StartsWith("ignore=")
                ? "無視する物を覚えています（剣を消して3秒待つ）"
                : "プレイ範囲を覚えています（" + ack.Detail.TrimEnd('s') + "秒、範囲いっぱいに振る）"; break;
            case "learned": result = "プレイ範囲を覚えました（" + ack.Detail.Substring(7) + "回分）"; break;
            case "failed": result = "覚えられませんでした（振りが足りません）"; break;
            case "shutter": result = "固定シャッター " + (ack.Detail == "off" ? "OFF（自動露出）" : "1/" + ack.Detail + " 秒"); break;
            case "exposure": result = "露出を " + ack.Detail.Substring(2) + " EV にしました"; break;
            case "tracklock": result = "剣を追いかける " + (ack.Detail == "on" ? "ON" : "OFF"); break;
            case "defaults": result = "既定に戻しました（剣を追いかける ON・露出 −2）"; break;
            case "clip-saved": result = "今の場面を保存しました"; break;
            case "clip-off": result = "今の場面を保存できませんでした（クリップOFF・停止中・保存不可）"; break;
            case "autoclip": result = "自動クリップ " + (ack.Detail == "on" ? "ON" : "OFF"); break;
            default: result = "返事: " + ack.Result; break;
        }
        return phone + ": " + result;
    }

    // 自動設定は送信中・成功・無応答ともトーストを出さない。F8 の状態欄には残す。
    public static string ToastText(PhoneSaberAcknowledgementStore store, double now)
    {
        if (!store.Latest(out _, out _, out _, out bool automatic) || automatic) return "";
        return StatusText(store, now);
    }

    public static bool ShouldRefreshToast(int revision, int previousRevision, double now, double nextRefresh)
        => revision != previousRevision || now >= nextRefresh;

    void RefreshToast()
    {
        var store = PhoneSaberDiscoveryResponder.Acknowledgements;
        int revision = store.Revision;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        // 変更なしのフレームは文字列・LINQ・配列を作らない。時計だけの変化は3秒の無応答判定時だけ。
        if (!ShouldRefreshToast(revision, toastRevision, now, nextToastRefresh)) return;
        toastRevision = revision;
        bool latest = store.Latest(out int id, out _, out double queuedAt, out bool automatic);
        nextToastRefresh = latest && !automatic && now < queuedAt + 3 ? queuedAt + 3 : double.PositiveInfinity;
        string message = ToastText(store, now);
        if (message == displayed && id == displayedId) return;
        displayed = message; displayedId = id;
        if (toast != null && message.Length == 0) toast.enabled = false;
        if (toast != null && message.Length > 0)
            toast.Show(message, message == NoReplyMessage || PhoneSaberDiscoveryResponder.Acknowledgements.Read(id).Any(a => a.Result == "learning"));
    }

    void Update()
    {
        PhoneSaberDiscoveryResponder.ProcessAutomaticDefaults();
        RefreshToast();
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        bool ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
        bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        if (!ctrl || !shift) return;
        foreach (var key in Keys)
        {
            if (!keyboard[key].wasPressedThisFrame) continue;
            var command = CommandFor(key, Volatile.Read(ref exposureIndex), out int next, Volatile.Read(ref lastSentTrackLock), Volatile.Read(ref shutterIndex));
            if (command == null) continue;
            exposureIndex = next;
            if (key == Key.S) shutterIndex = (shutterIndex + 1) % PhoneSaberDiscoveryResponder.ShutterChoices.Length;
            if (key == Key.T) lastSentTrackLock = !lastSentTrackLock;
            PhoneSaberDiscoveryResponder.QueueCommand(command.Value.command);
            PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "operator-key", "key=" + key + " command=" + command.Value.command);
            RefreshToast();
        }
    }
}

// 結果は3秒。学習中と無応答は次の結果・操作まで残す。普段は無効。
public sealed class PhoneSaberOperatorToast : MonoBehaviour
{
    double until;
    string text = "";
    GUIStyle style;

    void Awake() => enabled = false;

    public void Show(string message, bool persistent = false)
    { text = message; until = persistent ? double.PositiveInfinity : Time.realtimeSinceStartupAsDouble + 3; enabled = true; }

    void Update() { if (Time.realtimeSinceStartupAsDouble > until) enabled = false; }

    void OnGUI()
    {
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            style.normal.textColor = Color.yellow;
        }
        style.fontSize = Mathf.Max(16, Screen.height / 32);
        GUI.Box(new Rect(Screen.width * 0.2f, Screen.height * 0.16f, Screen.width * 0.6f, style.fontSize * (text.Count(c => c == '\n') + 1) * 2.4f), text, style);
    }
}
