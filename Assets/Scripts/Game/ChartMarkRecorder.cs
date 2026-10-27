using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// プレイ中の F5 で「ここを直したい」印を付ける(ChartMarks、2026-10-27)。F1〜F12 は運営のキーで、押さなければ何も出ない。
// 押したときだけ、画面を撮ってから左下に小さく「印1 ・ 18小節目」を出す(ノーツの近くには出さない)。
// 印があるプレイの結果画面では、一言を打つ欄(ChartMarkCommentPanel)を開く。
public sealed class ChartMarkRecorder : MonoBehaviour
{
    public const Key MarkKey = Key.F5;
    public const string GameScene = "Game", ResultScene = "Result";
    public const float ToastSeconds = 1.6f;
    public const float PanelDelaySeconds = .6f;
    public const float MinIntervalSeconds = .5f;
    float lastMarkAt = -10f;

    GamePlayManager manager;
    NoteSpawner spawner;
    float nextSearchAt;
    readonly List<ChartMarkJudgment> recent = new List<ChartMarkJudgment>();
    Canvas toastCanvas;
    TextMeshProUGUI toastText;
    float toastUntil;
    AudioSource source;
    AudioClip tick;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        var go = new GameObject("ChartMarkRecorder");
        go.AddComponent<ChartMarkRecorder>();
        DontDestroyOnLoad(go);
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        // 一言の欄が閉じきらずに場面が変わっても、Esc を止めたままにしない。
        InputManager.EscapeSuppressed = false;
        Unhook();
        if (scene.name == GameScene) ChartMarks.BeginPlay();
        else if (scene.name == ResultScene && ChartMarks.Count > 0) StartCoroutine(OpenPanelSoon());
    }

    IEnumerator OpenPanelSoon()
    {
        yield return new WaitForSecondsRealtime(PanelDelaySeconds);
        if (SceneManager.GetActiveScene().name == ResultScene && ChartMarks.Count > 0)
            ChartMarkCommentPanel.Open(ChartMarks.Current);
    }

    void Update()
    {
        if (toastCanvas != null && toastCanvas.enabled && Time.unscaledTime > toastUntil) toastCanvas.enabled = false;
        if (SceneManager.GetActiveScene().name != GameScene) return;
        Hook();
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[MarkKey].wasPressedThisFrame) return;
        if (manager == null || spawner == null || spawner.CurrentChart == null || manager.songPlayer == null) return;
        // 判定調整・練習は選んでいる曲の譜面ではないので、その曲の印にしない。
        if (GameSession.IsCalibrationMode || FindFirstObjectByType<CalibrationController>() != null
            || FindFirstObjectByType<TutorialController>() != null)
        {
            ShowToast("判定調整・練習では印を付けません");
            return;
        }
        // 押し続け・連打で同じ所に何個も付けない。
        if (Time.unscaledTime - lastMarkAt < MinIntervalSeconds) return;
        if (ChartMarks.Full) { ShowToast("印は1曲" + ChartMarks.MaxMarksPerPlay + "個までです"); return; }
        var entry = ChartMarks.Add(spawner.CurrentChart, manager.songPlayer.SongTime, spawner.TotalOffsetSeconds, recent,
            GameSession.SelectedSongId, GameSession.SelectedSongTitle, GameSession.SelectedDifficulty, Settings(),
            System.DateTime.Now);
        if (entry == null) return;
        lastMarkAt = Time.unscaledTime;
        bool saved = ChartMarks.LastSaveError.Length == 0;
        bool roomForImage = ChartMarks.FolderBytes() < ChartMarks.MaxFolderBytes;
        // 撮った画面に札が写らないよう、このコマを描き終えてから撮り、札はそのあとに出す。
        StartCoroutine(CaptureThenToast(entry, saved && roomForImage ? ChartMarks.ScreenshotPath(entry) : null, saved, roomForImage));
        Debug.Log("[ChartMarks] 印" + entry.number + " " + entry.id + " t=" + entry.songTime.ToString("0.000", CultureInfo.InvariantCulture));
    }

    IEnumerator CaptureThenToast(ChartMarkEntry entry, string shot, bool saved, bool roomForImage)
    {
        yield return new WaitForEndOfFrame();
        if (shot != null) SaveScreenshot(shot);
        string text = "印" + entry.number + " ・ " + entry.bar + "小節目 ・ " + ChartMarks.Clock(entry.songTime);
        if (!saved) text = "印を保存できませんでした(" + ChartMarks.LastSaveError + ")";
        else if (!roomForImage) text += " ・ 置き場所がいっぱいなので画面は省きました";
        ShowToast(text, saved ? ToastSeconds : 4f);
        PlayTick();
    }

    // 画面は幅 ScreenshotMaxWidth までに縮めた JPEG。書き込みは別のスレッドで行い、プレイを止めない。
    static void SaveScreenshot(string path)
    {
        Texture2D full = null, small = null;
        RenderTexture target = null;
        var previous = RenderTexture.active;
        try
        {
            full = ScreenCapture.CaptureScreenshotAsTexture();
            int width = Mathf.Min(ChartMarks.ScreenshotMaxWidth, full.width);
            int height = Mathf.Max(1, Mathf.RoundToInt(full.height * (width / (float)full.width)));
            target = RenderTexture.GetTemporary(width, height, 0);
            Graphics.Blit(full, target);
            RenderTexture.active = target;
            small = new Texture2D(width, height, TextureFormat.RGB24, false);
            small.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            small.Apply(false);
            byte[] bytes = small.EncodeToJPG(85);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { System.IO.File.WriteAllBytes(path, bytes); }
                catch (System.Exception e) { Debug.LogWarning("[ChartMarks] 画面を書けませんでした: " + e.Message); }
            });
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[ChartMarks] 画面を撮れませんでした: " + e.Message);
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            UISkinKit.SafeDestroy(full);
            UISkinKit.SafeDestroy(small);
        }
    }

    static string Settings()
    {
        return "projector=" + (DisplaySettings.ProjectorMode ? 1 : 0)
            + " reduced=" + (DisplaySettings.ReducedEffects ? 1 : 0)
            + " approach=" + GameSession.NoteApproachTime.ToString("0.00", CultureInfo.InvariantCulture)
            + " judgeOffsetMs=" + GameSession.JudgmentOffsetMs
            + " songsPerVisitor=" + VisitorPlays.SongsPerVisitor
            + " tutorial=" + (GameSession.TutorialPending ? 1 : 0)
            + " calibration=" + (GameSession.IsCalibrationMode ? 1 : 0);
    }

    // 直前の判定を覚えるため、出てきたノーツの判定を聞く(曲が始まってから GamePlayManager を探す)。
    void Hook()
    {
        if (spawner != null || Time.unscaledTime < nextSearchAt) return;
        nextSearchAt = Time.unscaledTime + 1f;
        manager = FindFirstObjectByType<GamePlayManager>();
        if (manager == null || manager.noteSpawner == null) { manager = null; return; }
        spawner = manager.noteSpawner;
        spawner.OnNoteSpawned += Track;
        foreach (var note in spawner.LiveNotes) Track(note);
    }

    void Unhook()
    {
        if (spawner != null) spawner.OnNoteSpawned -= Track;
        spawner = null;
        manager = null;
        nextSearchAt = 0f;
        recent.Clear();
    }

    void Track(CuttableNote note)
    {
        if (note == null) return;
        note.OnJudged += (n, tier, point, velocity) => Remember(n, tier.ToString());
        note.OnMiss += n => Remember(n, JudgmentTier.Miss.ToString());
    }

    void Remember(CuttableNote note, string result)
    {
        if (note == null || spawner == null) return;
        double chartMs = (note.HitTime - spawner.TotalOffsetSeconds) * 1000.0;
        // 同じノーツの Miss を二重に数えない(判定の Miss と見逃しの Miss)。
        if (recent.Count > 0)
        {
            var last = recent[recent.Count - 1];
            if (last.result == result && System.Math.Abs(last.chartMs - chartMs) < .5 && last.hand == note.RequiredHand.ToString()) return;
        }
        Vector3 position = note.transform.position;
        recent.Add(new ChartMarkJudgment
        {
            chartMs = System.Math.Round(chartMs, 1), hand = note.RequiredHand.ToString(),
            x = position.x, y = position.y, result = result
        });
        if (recent.Count > ChartMarks.RecentJudgments) recent.RemoveAt(0);
    }

    public void ShowToast(string text, float seconds = ToastSeconds)
    {
        EnsureToast();
        toastText.text = text;
        toastCanvas.enabled = true;
        toastUntil = Time.unscaledTime + seconds;
    }

    void EnsureToast()
    {
        if (toastCanvas != null) return;
        var go = new GameObject("ChartMarkToast", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler));
        go.transform.SetParent(transform, false);
        toastCanvas = go.GetComponent<Canvas>();
        toastCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        toastCanvas.sortingOrder = 930;
        var scaler = go.GetComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        // 左下の隅。ノーツの通り道とゲートの外。
        toastText = SongSelectVisuals.Label(go.transform, "Text", "", 26, Vector2.zero, new Vector2(760, 44),
            new Color(1f, .82f, .45f, .92f), TextAlignmentOptions.MidlineLeft, true);
        var rt = toastText.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(28, 22);
        toastText.raycastTarget = false;
        toastCanvas.enabled = false;
    }

    void PlayTick()
    {
        if (source == null)
        {
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
        }
        if (tick == null) tick = JudgmentSfx.Beep(1318.5f, .05f);
        source.PlayOneShot(ProceduralSfx.Revive(ref tick), .22f);
    }

    void OnDestroy()
    {
        Unhook();
        UISkinKit.SafeDestroy(tick);
        tick = null;
    }
}
