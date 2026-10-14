using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

// ビルド済み Player の動作確認。Editor では動くのに Player だけで落ちる不具合を、配る前に見つける。
// 2026-10-12: build に入っていない shader を Shader.Find して例外になり、Player だけ剣が出なかった。
// -phonesaberSmoke <1 scene あたりの秒数> を付けて起動したときだけ働く(Editor と通常起動では何もしない)。
//   Title → SongSelect → Game(14秒の実カット) → Result → 判定調整 → SongSelect → Title。
//   終了コード: 0 = 全確認OK、1 = Error / Exception / Assert、2 = 入力・切断・遷移の失敗。
// PhoneSaber/tools/unity_copy.sh build-mac が、差し替える前の Player に対して実行する。
// 見た目の確認(unity_copy.sh shots): -phonesaberSmokeShots <folder> を足すと各 scene の画面を PNG に残す
// (窓を隠して起動したまま撮れる。batchmode では撮れない)。-phonesaberSmokeRefreshHz <Hz> は
// 「画面の更新が遅い」注意の表示確認用に、注意の判定に使う Hz だけを差し替える(描画や vsync は変えない)。
public sealed class PhoneSaberPlayerSmoke : MonoBehaviour
{
    public const string Argument = "-phonesaberSmoke";
    public const float DefaultSeconds = 5f;
    public const string ShotsArgument = "-phonesaberSmokeShots";
    public const string RefreshHzArgument = "-phonesaberSmokeRefreshHz";
    string shotsFolder;
    float secondsPerScene;
    int exceptions, errors;
    bool flowOk = true;
    int cuts;
    const float GameSeconds = 14f;
    string resultKey, previousResult;
    bool hadResult;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (Application.isEditor) return;
        float seconds = ResolveSeconds(Environment.GetCommandLineArgs());
        if (seconds <= 0f) return;
        var smoke = new GameObject("PhoneSaber Player Smoke").AddComponent<PhoneSaberPlayerSmoke>();
        smoke.secondsPerScene = seconds;
        string[] args = Environment.GetCommandLineArgs();
        smoke.shotsFolder = Application.isBatchMode ? null : ResolveValue(args, ShotsArgument);
        if (double.TryParse(ResolveValue(args, RefreshHzArgument), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double hz) && hz > 0) PhoneSaberDisplaySync.RefreshHzOverride = hz;
        DontDestroyOnLoad(smoke.gameObject);
    }

    // 引数が無ければ 0(働かない)。秒数が無い・読めない・範囲外なら既定値。
    public static float ResolveSeconds(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != Argument) continue;
            if (i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float seconds) && seconds >= 1f && seconds <= 120f) return seconds;
            return DefaultSeconds;
        }
        return 0f;
    }

    // 引数の次の値。無い・次が別の引数なら null。
    public static string ResolveValue(string[] args, string name)
    {
        for (int i = 1; i + 1 < args.Length; i++)
            if (args[i] == name && !args[i + 1].StartsWith("-")) return args[i + 1];
        return null;
    }

    IEnumerator Capture(string scene)
    {
        if (string.IsNullOrEmpty(shotsFolder)) yield break;
        string path = System.IO.Path.Combine(shotsFolder, scene + ".png");
        try { System.IO.Directory.CreateDirectory(shotsFolder); System.IO.File.Delete(path); }
        catch (Exception e) { Debug.Log("[PhoneSaberSmoke] shot: " + e.Message); yield break; }
        ScreenCapture.CaptureScreenshot(path);
        // 書き出しはフレームの終わり以降。最大2秒待つ。
        float until = Time.realtimeSinceStartup + 2f;
        while (!System.IO.File.Exists(path) && Time.realtimeSinceStartup < until) yield return null;
        Debug.Log("[PhoneSaberSmoke] shot " + scene + " " + (System.IO.File.Exists(path) ? "saved" : "MISSING"));
    }

    void OnEnable() { Application.logMessageReceivedThreaded += OnLog; }
    void OnDisable()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        RestoreResult();
    }

    void OnLog(string condition, string stackTrace, LogType type)
    {
        // 許可リストは空。Error / Assert も異常終了にする(Warning は任意 bridge 不在などを含む)。
        if (type == LogType.Exception) System.Threading.Interlocked.Increment(ref exceptions);
        if (type == LogType.Error || type == LogType.Assert) System.Threading.Interlocked.Increment(ref errors);
    }

    IEnumerator Stay(string scene, string label = null)
    {
        yield return new WaitForSecondsRealtime(secondsPerScene);
        bool loaded = SceneManager.GetActiveScene().name == scene;
        flowOk &= loaded;
        Debug.Log("[PhoneSaberSmoke] scene=" + (label ?? scene) + " loaded=" + loaded);
        yield return Capture(label ?? scene);
    }

    void RestoreResult()
    {
        if (resultKey == null) return;
        if (hadResult) PlayerPrefs.SetString(resultKey, previousResult);
        else PlayerPrefs.DeleteKey(resultKey);
        PlayerPrefs.Save(); resultKey = null;
    }

    IEnumerator Start()
    {
        Debug.Log("[PhoneSaberSmoke] start mode=" + (Application.isBatchMode ? "batch" : "window") +
            " seconds-per-scene=" + secondsPerScene.ToString(CultureInfo.InvariantCulture));
        // 実際の初期シーンも確認し、Build Scene List の誤りを強制ロードで隠さない。
        yield return null;
        bool startsAtTitle = SceneManager.GetActiveScene().name == "Title";
        flowOk &= startsAtTitle;
        if (!startsAtTitle) SceneManager.LoadScene("Title");
        yield return Stay("Title");
        SceneManager.LoadScene("SongSelect");
        yield return Stay("SongSelect");
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedSongTitle = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        SceneManager.LoadScene("Game");
        yield return null; // Start による音源ロードを開始させる
        yield return CheckInput(GameSeconds);
        flowOk &= SceneManager.GetActiveScene().name == "Game";
        yield return Capture("Game");

        // 曲末へ時計を飛ばすと集計・保存を汚すため、時間を区切ったプレイの結果UIだけ開く。
        // 完走時の精算・達成・日次順位の経路はこの smoke の対象外。
        var score = FindFirstObjectByType<ScoreManager>();
        if (score != null)
        {
            GameSession.FinalScore = score.Score;
            GameSession.FinalHit = score.HitCount;
            GameSession.FinalMiss = score.MissCount;
            GameSession.FinalMaxCombo = score.MaxCombo;
            GameSession.FinalComboBonus = score.ComboBonus;
            GameSession.FinalPerfect = score.PerfectCount;
            GameSession.FinalGreat = score.GreatCount;
            GameSession.FinalGood = score.GoodCount;
            GameSession.FinalBad = score.BadCount;
        }
        // ResultSkin が自動保存するスコアは専用キーへ隔離し、終了時に元へ戻す。
        GameSession.SelectedSongId = "PhoneSaberSmoke";
        resultKey = HighScoreStore.Key(GameSession.SelectedSongId, GameSession.SelectedDifficulty);
        hadResult = PlayerPrefs.HasKey(resultKey); previousResult = PlayerPrefs.GetString(resultKey, "");
        SceneManager.LoadScene("Result");
        yield return Stay("Result");
        flowOk &= FindFirstObjectByType<ResultController>() != null;
        RestoreResult();

        GameSession.SelectedSongId = "ElDorado";
        GameSession.IsCalibrationMode = true;
        SceneManager.LoadScene("Game");
        yield return null;
        yield return CheckInput(Mathf.Max(8f, secondsPerScene), true);
        var calibration = FindFirstObjectByType<CalibrationController>();
        flowOk &= calibration != null && calibration.IsLive;
        yield return Capture("Calibration");
        if (calibration != null) calibration.DiscardAndExit(); // 保存しない実際の戻り経路
        yield return Stay("SongSelect", "CalibrationReturn");
        flowOk &= !GameSession.IsCalibrationMode;
        var selection = FindFirstObjectByType<SongSelectController>();
        if (selection != null) selection.ReturnToTitle();
        else flowOk = false;
        yield return Stay("Title", "TitleReturn");
        Debug.Log("[PhoneSaberSmoke] done exceptions=" + exceptions + " errors=" + errors +
            " input=" + (inputOk ? "ok" : "FAILED") + " cuts=" + cuts + " flow=" + (flowOk ? "ok" : "FAILED"));
        Application.Quit(exceptions + errors > 0 ? 1 : inputOk && cuts > 0 && flowOk ? 0 : 2);
    }

    bool inputOk = true;

    // 本番のUDP・受信・橋渡し・判定を通す。Cut / RegisterHit を直接呼ばず、判定条件も緩めない。
    IEnumerator CheckInput(float seconds, bool calibration = false)
    {
        var input = InputPoint.Instance;
        if (input == null) { inputOk = false; Debug.Log("[PhoneSaberSmoke] input: InputPoint がありません"); yield break; }
        int red = input.ReceivedPacketCount, blue = input.ReceivedPacketCount2;
        float until = Time.realtimeSinceStartup + seconds;
        using (var sender = new System.Net.Sockets.UdpClient())
        {
            int i = 0;
            while (Time.realtimeSinceStartup < until)
            {
                // 長い縦の刃を左右へ往復。端末の認識処理は通さず、本文の4座標形式は同じ。
                int x = 320 + (i % 32 < 16 ? i % 16 : 15 - i % 16) * 80;
                byte[] a = System.Text.Encoding.ASCII.GetBytes(x + ",200," + x + ",900");
                int bx = 1920 - x;
                byte[] b = System.Text.Encoding.ASCII.GetBytes(bx + ",200," + bx + ",900");
                sender.Send(a, a.Length, "127.0.0.1", input.port);
                sender.Send(b, b.Length, "127.0.0.1", input.port2);
                i++;
                yield return new WaitForSecondsRealtime(1f / 30f);
            }
        }
        bool received = input.ReceivedPacketCount - red >= 30 && input.ReceivedPacketCount2 - blue >= 30;
        bool endpoints = input.HasValidStickEndpoints && input.HasValidStickEndpoints2;
        var bridges = FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None);
        bool redBlade = false, blueBlade = false;
        foreach (var bridge in bridges)
        {
            if (!bridge.HasBlade || bridge.BladeVisual == null || bridge.BladeVisual.VertexCount == 0) continue;
            if (bridge.stickIndex == 1) redBlade = true;
            if (bridge.stickIndex == 2) blueBlade = true;
        }
        var score = FindFirstObjectByType<ScoreManager>();
        int hits = score != null ? score.HitCount : 0;
        if (!calibration) cuts = hits;
        flowOk &= hits > 0; // 調整画面でも実カットを確認
        inputOk &= received && endpoints && redBlade && blueBlade;
        Debug.Log("[PhoneSaberSmoke] input: mode=" + (calibration ? "calibration" : "game") +
            " received=" + received + " endpoints=" + endpoints + " red-blade=" + redBlade + " blue-blade=" + blueBlade + " cuts=" + hits);
    }
}
