using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

// ビルド済み Player の動作確認。Editor では動くのに Player だけで落ちる不具合を、配る前に見つける。
// 2026-10-12: build に入っていない shader を Shader.Find して例外になり、Player だけ剣が出なかった。
// -phonesaberSmoke <1 scene あたりの秒数> を付けて起動したときだけ働く(Editor と通常起動では何もしない)。
//   Title → SongSelect → Game(ElDorado / normal)を順に開き、本編では自分へ座標を送って受信と剣の部品を確かめる。
//   終了コード: 0 = 例外なし・入力 OK、1 = 例外あり、2 = 入力が通らない。
// PhoneSaber/tools/unity_copy.sh build-mac が、差し替える前の Player に対して実行する。
// 見た目の確認(unity_copy.sh shots): -phonesaberSmokeShots <folder> を足すと各 scene の画面を PNG に残す
// (窓を隠して起動したまま撮れる。batchmode では撮れない)。-phonesaberSmokeRefreshHz <Hz> は
// 「画面の更新が遅い」注意の表示確認用に、注意の判定に使う Hz だけを差し替える(描画や vsync は変えない)。
public sealed class PhoneSaberPlayerSmoke : MonoBehaviour
{
    public const string Argument = "-phonesaberSmoke";
    public const float DefaultSeconds = 8f;
    public const string ShotsArgument = "-phonesaberSmokeShots";
    public const string RefreshHzArgument = "-phonesaberSmokeRefreshHz";
    string shotsFolder;
    static readonly string[] Scenes = { "Title", "SongSelect", "Game" };
    float secondsPerScene;
    int exceptions;

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

    void OnEnable() { Application.logMessageReceived += OnLog; }
    void OnDisable() { Application.logMessageReceived -= OnLog; }

    void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Exception) exceptions++;
    }

    IEnumerator Start()
    {
        Debug.Log("[PhoneSaberSmoke] start seconds-per-scene=" + secondsPerScene.ToString(CultureInfo.InvariantCulture));
        yield return new WaitForSecondsRealtime(secondsPerScene); // 起動時の scene(Title)
        yield return Capture(Scenes[0]);
        for (int i = 1; i < Scenes.Length; i++)
        {
            bool game = Scenes[i] == "Game";
            if (game)
            {
                // 曲選択を操作せずに本編へ入る(PhoneSaberFrameCost と同じ準備)。
                GameSession.SelectedSongId = "ElDorado";
                GameSession.SelectedDifficulty = "normal";
                GameSession.IsCalibrationMode = false;
                GameSession.TutorialPending = false;
            }
            Debug.Log("[PhoneSaberSmoke] load " + Scenes[i] + " exceptions-so-far=" + exceptions);
            SceneManager.LoadScene(Scenes[i]);
            // 本編は開始演出のあとに小節線やノーツが出るので長めに待つ。
            yield return new WaitForSecondsRealtime(secondsPerScene);
            if (game) yield return CheckInput();
            if (game) yield return new WaitForSecondsRealtime(secondsPerScene);
            yield return Capture(Scenes[i]);
        }
        Debug.Log("[PhoneSaberSmoke] done exceptions=" + exceptions + " input=" + (inputOk ? "ok" : "FAILED"));
        Application.Quit(exceptions > 0 ? 1 : inputOk ? 0 : 2);
    }

    bool inputOk;

    // スマホの代わりに自分へ座標を送り、本編で受信・端点・剣の部品がそろうことを確かめる。
    IEnumerator CheckInput()
    {
        var input = InputPoint.Instance;
        if (input == null) { Debug.Log("[PhoneSaberSmoke] input: InputPoint がありません"); yield break; }
        int red = input.ReceivedPacketCount, blue = input.ReceivedPacketCount2;
        using (var sender = new System.Net.Sockets.UdpClient())
        {
            for (int i = 0; i < 45; i++)
            {
                byte[] a = System.Text.Encoding.ASCII.GetBytes((600 + i * 4) + ",300," + (900 + i * 4) + ",700");
                byte[] b = System.Text.Encoding.ASCII.GetBytes((1300 - i * 4) + ",300," + (1000 - i * 4) + ",700");
                sender.Send(a, a.Length, "127.0.0.1", input.port);
                sender.Send(b, b.Length, "127.0.0.1", input.port2);
                yield return new WaitForSecondsRealtime(1f / 30f);
            }
        }
        bool received = input.ReceivedPacketCount - red >= 30 && input.ReceivedPacketCount2 - blue >= 30;
        bool endpoints = input.HasValidStickEndpoints && input.HasValidStickEndpoints2;
        int bridges = FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None).Length;
        inputOk = received && endpoints && bridges > 0;
        Debug.Log("[PhoneSaberSmoke] input: received=" + received + " endpoints=" + endpoints + " saber-bridges=" + bridges);
    }
}
