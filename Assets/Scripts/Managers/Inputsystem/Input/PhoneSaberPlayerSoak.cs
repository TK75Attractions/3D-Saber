using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

// 明示引数のあるPlayer専用。scene/prefabと通常起動・既存smokeの動作は変更しない。
public sealed class PhoneSaberPlayerSoak : MonoBehaviour
{
    double minutes, gameSeconds;
    readonly PhoneSaberSoakErrors faults = new PhoneSaberSoakErrors();
    readonly List<Choice> choices = new List<Choice>();
    readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
    readonly Dictionary<string, string> savedPrefs = new Dictionary<string, string>();
    bool inputOk = true, flowOk = true;
    PhoneSaberSoakSender sender;
    struct Choice { public string song, difficulty; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (Application.isEditor) return;
        string[] args = Environment.GetCommandLineArgs();
        double duration = PhoneSaberSoakModel.Minutes(args);
        if (duration <= 0) return;
        var soak = new GameObject("PhoneSaber Player Soak").AddComponent<PhoneSaberPlayerSoak>();
        soak.minutes = duration; soak.gameSeconds = PhoneSaberSoakModel.GameSeconds(args);
        DontDestroyOnLoad(soak.gameObject);
    }

    void OnEnable() { Application.logMessageReceivedThreaded += OnLog; }
    void OnDisable()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        StopSender(); RestorePrefs();
    }
    void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
            faults.Add(condition, type == LogType.Exception);
    }
    void StopSender() { sender?.Dispose(); sender = null; }

    // 自然完走が起きても試験の成績を残さない。通常終了/disable時に元の3種類の記録を戻す。
    void SavePrefs(Choice choice)
    {
        foreach (string key in new[] { HighScoreStore.Key(choice.song, choice.difficulty),
            SongAchievementStore.Key(choice.song, choice.difficulty), DailyRankingStore.Key(choice.song, choice.difficulty) })
            if (!savedPrefs.ContainsKey(key)) savedPrefs.Add(key, PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null);
    }
    void RestorePrefs()
    {
        if (savedPrefs.Count == 0) return;
        foreach (var pair in savedPrefs)
            if (pair.Value == null) PlayerPrefs.DeleteKey(pair.Key); else PlayerPrefs.SetString(pair.Key, pair.Value);
        PlayerPrefs.Save(); savedPrefs.Clear();
    }

    // ロード・Start・退出演出を待つ。到達しない経路を成功扱いにしない。
    IEnumerator WaitScene(string scene)
    {
        double until = Time.realtimeSinceStartupAsDouble + 10;
        yield return null;
        while ((SceneManager.GetActiveScene().name != scene || ScreenTransition.IsBusy) &&
            Time.realtimeSinceStartupAsDouble < until) yield return null;
        flowOk &= SceneManager.GetActiveScene().name == scene && !ScreenTransition.IsBusy;
        yield return new WaitForSecondsRealtime(1);
    }

    void ReadChoices(SongSelectController selection)
    {
        if (selection == null || selection.difficultyNames == null) return;
        for (int song = 0; song < selection.SongCount; song++)
        {
            if (selection.IsLocked(song)) continue;
            for (int difficulty = 0; difficulty < selection.difficultyNames.Length; difficulty++)
                if (SongSelectController.HasPlayableChart(selection.SongIdAt(song), new[] { selection.difficultyNames[difficulty] }))
                    choices.Add(new Choice { song = selection.SongIdAt(song), difficulty = selection.difficultyNames[difficulty] });
        }
    }

    IEnumerator Start()
    {
        Debug.Log("[PhoneSaberSoak] start mode=" + (Application.isBatchMode ? "batch" : "window") +
            " minutes=" + F(minutes) + " game-seconds=" + F(gameSeconds));
        double deadline = Time.realtimeSinceStartupAsDouble + minutes * 60;
        int loops = 0;
        yield return null;
        flowOk &= SceneManager.GetActiveScene().name == "Title";
        if (!flowOk) SceneManager.LoadScene("Title");
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            double start = Time.realtimeSinceStartupAsDouble;
            yield return WaitScene("Title");
            double title = Time.realtimeSinceStartupAsDouble;
            SceneManager.LoadScene("SongSelect");
            yield return WaitScene("SongSelect");
            var selection = FindFirstObjectByType<SongSelectController>();
            if (choices.Count == 0) ReadChoices(selection);
            if (selection == null || choices.Count == 0) { flowOk = false; break; }
            Choice choice = choices[loops % choices.Count];
            int index = -1;
            for (int i = 0; i < selection.SongCount; i++) if (selection.SongIdAt(i) == choice.song) index = i;
            selection.Select(index);
            selection.SetDifficulty(Array.IndexOf(selection.difficultyNames, choice.difficulty));
            SavePrefs(choice);
            GameSession.TutorialPending = false;
            selection.StartGame();
            double selected = Time.realtimeSinceStartupAsDouble;
            yield return WaitScene("Game");
            double entered = Time.realtimeSinceStartupAsDouble;
            var input = InputPoint.Instance;
            int red = input != null ? input.ReceivedPacketCount : 0;
            int blue = input != null ? input.ReceivedPacketCount2 : 0;
            var bridges = FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None);
            var firstA = new Vector3[bridges.Length]; var firstB = new Vector3[bridges.Length];
            var seen = new bool[bridges.Length];
            bool redActive = false, blueActive = false, redMoved = false, blueMoved = false;
            int frames = 0; double worst = 0, last = entered;
            // 時間切れでも開始済みの1周は最後まで確認し、短い最終周の入力失敗を作らない。
            double until = entered + gameSeconds;
            if (input != null)
                sender = new PhoneSaberSoakSender(input.port, input.port2, gameSeconds, Debug.LogException);
            while (Time.realtimeSinceStartupAsDouble < until && SceneManager.GetActiveScene().name == "Game")
            {
                yield return null;
                double now = Time.realtimeSinceStartupAsDouble;
                worst = Math.Max(worst, now - last); last = now; frames++;
                for (int i = 0; i < bridges.Length; i++)
                {
                    var bridge = bridges[i];
                    if (bridge == null || !bridge.isActiveAndEnabled || !bridge.gameObject.activeInHierarchy ||
                        !bridge.ReceivingCamera || !bridge.HasBlade || bridge.BladeVisual == null ||
                        !bridge.BladeVisual.gameObject.activeInHierarchy || bridge.BladeVisual.VertexCount == 0) continue;
                    bool moved = seen[i] && ((bridge.WorldEndA - firstA[i]).sqrMagnitude > .01f ||
                        (bridge.WorldEndB - firstB[i]).sqrMagnitude > .01f);
                    if (!seen[i]) { firstA[i] = bridge.WorldEndA; firstB[i] = bridge.WorldEndB; seen[i] = true; }
                    if (bridge.stickIndex == 1) { redActive = true; redMoved |= moved; }
                    if (bridge.stickIndex == 2) { blueActive = true; blueMoved |= moved; }
                }
            }
            StopSender();
            double played = Time.realtimeSinceStartupAsDouble;
            red = input != null ? input.ReceivedPacketCount - red : 0;
            blue = input != null ? input.ReceivedPacketCount2 - blue : 0;
            bool reached = red >= 30 && blue >= 30 && redActive && blueActive && redMoved && blueMoved;
            inputOk &= reached;
            string exit = "scene-load";
            if (SceneManager.GetActiveScene().name == "Result")
            {
                var result = FindFirstObjectByType<ResultController>();
                if (result != null) { exit = "result-return"; result.OnBackButton(); }
                else { flowOk = false; SceneManager.LoadScene("Title"); }
            }
            else
            {
                flowOk &= SceneManager.GetActiveScene().name == "Game";
                // 本編の途中退出はprivate。公開口の無い経路では許可されたscene loadを使う。
                SceneManager.LoadScene("Title");
            }
            yield return WaitScene("Title");
            RestorePrefs();
            double returned = Time.realtimeSinceStartupAsDouble;
            string metrics = Snapshot();
            string detail = "loop=" + (++loops) + " song=" + choice.song + " difficulty=" + choice.difficulty +
                " title-ms=" + F((title - start) * 1000) + " select-ms=" + F((selected - title) * 1000) +
                " load-game-ms=" + F((entered - selected) * 1000) + " game-ms=" + F((played - entered) * 1000) +
                " return-ms=" + F((returned - played) * 1000) + " exit=" + exit +
                " fps=" + F(frames / Math.Max(.001, played - entered)) + " worst-frame-ms=" + F(worst * 1000) +
                " red-packets=" + red + " blue-packets=" + blue + " red-active=" + redActive + " blue-active=" + blueActive +
                " red-moved=" + redMoved + " blue-moved=" + blueMoved + " input=" + (reached ? "ok" : "FAILED") +
                " flow=" + (flowOk ? "ok" : "FAILED") + metrics + " " + faults.Drain();
            PhoneSaberEventLog.Record("soak", "BOTH", "soak", detail);
            PhoneSaberEventLog.Current?.Flush();
            if (PhoneSaberEventLog.Current == null || !string.IsNullOrEmpty(PhoneSaberEventLog.Current.LastError))
                Debug.LogError("[PhoneSaberSoak] event log unavailable: " + PhoneSaberEventLog.Current?.LastError);
            // 同じ形式でPlayerログにも残す。ローテーション後も今回の全周回をshellで集計できる。
            Debug.Log(PhoneSaberEventLog.Format(DateTimeOffset.UtcNow, "soak", "BOTH", "soak", detail).TrimEnd());
            if (!flowOk) break;
        }
        RestorePrefs();
        string leak = LeakVerdict();
        Debug.Log("[PhoneSaberSoak] done loops=" + loops + " exceptions=" + faults.Exceptions + " leak=" + leak +
            " errors=" + faults.Errors + " input=" + (inputOk && loops > 0 ? "ok" : "FAILED") +
            " flow=" + (flowOk && loops > 0 ? "ok" : "FAILED"));
        Application.Quit(faults.Exceptions + faults.Errors > 0 ? 1 :
            !inputOk || !flowOk || loops == 0 ? 2 : leak.StartsWith("growth:") || leak == "invalid" ? 3 : 0);
    }

    string Snapshot()
    {
        string result = Metric("AudioSource", Resources.FindObjectsOfTypeAll<AudioSource>().Length);
        result += Metric("Material", Resources.FindObjectsOfTypeAll<Material>().Length);
        result += Metric("Texture2D", Resources.FindObjectsOfTypeAll<Texture2D>().Length);
        result += Metric("Mesh", Resources.FindObjectsOfTypeAll<Mesh>().Length);
        result += Metric("GameObject", Resources.FindObjectsOfTypeAll<GameObject>().Length);
        result += Metric("MonoBehaviour", Resources.FindObjectsOfTypeAll<MonoBehaviour>().Length);
        result += Metric("SaberInputBridge", Resources.FindObjectsOfTypeAll<SaberInputBridge>().Length);
        result += Metric("InputPoint", Resources.FindObjectsOfTypeAll<InputPoint>().Length);
        result += Metric("managed-bytes", GC.GetTotalMemory(false));
        result += Metric("allocated-bytes", Profiler.GetTotalAllocatedMemoryLong());
        result += Metric("reserved-bytes", Profiler.GetTotalReservedMemoryLong());
        return result;
    }
    string Metric(string name, long value)
    {
        if (!samples.TryGetValue(name, out var list)) samples.Add(name, list = new List<double>());
        list.Add(value);
        return " " + name + "=" + value.ToString(CultureInfo.InvariantCulture);
    }
    string LeakVerdict()
    {
        string result = "stable";
        foreach (var pair in samples)
        {
            double floor = pair.Key.EndsWith("-bytes") ? 16 * 1024 * 1024 :
                pair.Key == "GameObject" ? 64 : pair.Key == "MonoBehaviour" ? 128 :
                pair.Key == "InputPoint" ? 1 : pair.Key == "SaberInputBridge" ? 2 :
                pair.Key == "AudioSource" ? 4 : pair.Key == "Texture2D" ? 8 : 16;
            string verdict = PhoneSaberSoakModel.Growth(pair.Value, floor);
            if (verdict == "growth") return "growth:" + pair.Key;
            if (verdict == "invalid") return verdict;
            if (verdict == "insufficient") result = verdict;
        }
        return samples.Count == 0 ? "insufficient" : result;
    }
    static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
