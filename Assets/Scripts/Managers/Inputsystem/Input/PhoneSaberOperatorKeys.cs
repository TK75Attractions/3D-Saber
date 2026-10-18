using UnityEngine;
using UnityEngine.InputSystem;

// 運営のキーボード操作（2026-10-18 ユーザー: スマホに無視させる操作は F8 の画面ではなくキーで直接）。
// Ctrl+Shift+文字。ゲームが使う A/S/D/W・数字・矢印・Space・Enter・Esc・N とは重ねない。
// 押すと PhoneSaberDiscoveryResponder.QueueCommand で、探索応答と一緒にスマホへ 10 秒送り、3 秒だけ画面に出す。
public sealed class PhoneSaberOperatorKeys : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        var go = new GameObject("PhoneSaber Operator Keys");
        go.AddComponent<PhoneSaberOperatorKeys>().toast = go.AddComponent<PhoneSaberOperatorToast>();
        DontDestroyOnLoad(go);
    }

    PhoneSaberOperatorToast toast;
    static int exposureIndex; // 0 → -1 → -2 → 0（PC から最後に送った値。スマホ側が保存する）

    // 押されたキーに対応する指示と表示。対応しないキーは null。
    public static (string command, string label)? CommandFor(Key key, int currentExposureIndex, out int nextExposureIndex)
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
            case Key.E:
                var choices = PhoneSaberDiscoveryResponder.ExposureBiasChoices;
                nextExposureIndex = (currentExposureIndex + 1) % choices.Length;
                int ev = choices[nextExposureIndex];
                return (PhoneSaberDiscoveryResponder.ExposureBiasCommand(ev), $"スマホ露出: {(ev == 0 ? "0（OFF）" : ev.ToString())}");
            default: return null;
        }
    }

    static readonly Key[] Keys = { Key.R, Key.B, Key.I, Key.C, Key.K, Key.P, Key.X, Key.E };

    void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        bool ctrl = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
        bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        if (!ctrl || !shift) return;
        foreach (var key in Keys)
        {
            if (!keyboard[key].wasPressedThisFrame) continue;
            var command = CommandFor(key, exposureIndex, out int next);
            if (command == null) continue;
            exposureIndex = next;
            PhoneSaberDiscoveryResponder.QueueCommand(command.Value.command);
            PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "SYSTEM", "operator-key", "key=" + key + " command=" + command.Value.command);
            if (toast != null) toast.Show(command.Value.label + "（送信中）");
        }
    }
}

// 操作の確認を 3 秒だけ出す。普段は無効（OnGUI の毎フレームの負荷を出さない）。
public sealed class PhoneSaberOperatorToast : MonoBehaviour
{
    double until;
    string text = "";
    GUIStyle style;

    void Awake() => enabled = false;

    public void Show(string message) { text = message; until = Time.realtimeSinceStartupAsDouble + 3; enabled = true; }

    void Update() { if (Time.realtimeSinceStartupAsDouble > until) enabled = false; }

    void OnGUI()
    {
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            style.normal.textColor = Color.yellow;
        }
        style.fontSize = Mathf.Max(16, Screen.height / 32);
        GUI.Box(new Rect(Screen.width * 0.2f, Screen.height * 0.16f, Screen.width * 0.6f, style.fontSize * 2.4f), text, style);
    }
}
