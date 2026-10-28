using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// プレイ中の F5(とスマホ)で「ここを直したい」印を付ける(ChartMarks、2026-10-27)。F1〜F12 は運営のキーで、押さなければ何も出ない。
// 押したときだけ、画面を撮ってから左下に小さく「印1 ・ 18小節目」を出す(ノーツの近くには出さない)。
// 印があるプレイの結果画面では、一言を打つ欄(ChartMarkCommentPanel)を開く。結果画面で F5 を押すと何度でも開き直せる。
// スマホの部品は Instance.TryMark("phone", ...) と PlayState を使う(どちらもメインスレッドで)。
public sealed class ChartMarkRecorder : MonoBehaviour
{
    public const Key MarkKey = Key.F5;
    public const string GameScene = "Game", ResultScene = "Result";
    public const string StatePlaying = "playing", StateResult = "result", StateOther = "other";
    public const float ToastSeconds = 1.6f;
    public const float PanelDelaySeconds = .6f;
    // 暗転が明けるのを待つ長さ(これを過ぎても暗転中なら、その回は自動では開かない。F5 で開ける)。
    public const float PanelWaitSeconds = 10f;
    // 書けなかった印を書き直す間隔(演奏中は書かない)。
    public const float RetrySeconds = 5f;
    public const float MinIntervalSeconds = .5f;
    public const string NotPlayingMessage = "演奏中ではありません";
    public const string PracticeMessage = "判定調整・練習では印を付けません";
    public const string TooSoonMessage = "続けて付けるときは0.5秒あけてください";

    // 常駐の1つ(テスト中は作らない。無ければ null)。
    public static ChartMarkRecorder Instance { get; private set; }
    // 別のスレッドで書けなかった画面(印の ID)。メインスレッドで screenshot を "" にする。
    static readonly ConcurrentQueue<string> LostShots = new ConcurrentQueue<string>();

    float lastMarkAt = -10f, nextRetryAt;
    GamePlayManager manager;
    NoteSpawner spawner;
    float nextSearchAt;
    readonly List<ChartMarkJudgment> recent = new List<ChartMarkJudgment>();
    Canvas toastCanvas;
    TextMeshProUGUI toastText;
    float toastUntil;
    AudioSource source;
    AudioClip tick;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        while (LostShots.TryDequeue(out _)) { }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled) return; // テスト中は作らない
        var go = new GameObject("ChartMarkRecorder");
        go.AddComponent<ChartMarkRecorder>();
        DontDestroyOnLoad(go);
    }

    // "playing"(Game の本編の演奏中) / "result"(結果画面) / "other"(判定調整・練習・ほかの画面)。
    public static string PlayState
    {
        get
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene == ResultScene) return StateResult;
            if (scene != GameScene) return StateOther;
            var recorder = Instance;
            return recorder != null && recorder.NotPlayingReason() == null ? StatePlaying : StateOther;
        }
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single) return;
        // 一言の欄が閉じきらずに場面が変わっても、Esc を止めたままにしない。
        InputManager.EscapeSuppressed = false;
        PhoneSaberOperatorKeys.TextEntryActive = false;
        Unhook();
        if (scene.name == GameScene) ChartMarks.BeginPlay();
        // スマホだけで付けた回は自動では開かない(運営が来場者のプレイにスマホで付けても、結果画面を覆わない)。結果画面の F5 では開く。
        else if (scene.name == ResultScene && ChartMarks.HasKeyMarks) StartCoroutine(OpenPanelSoon());
    }

    IEnumerator OpenPanelSoon()
    {
        yield return new WaitForSecondsRealtime(PanelDelaySeconds);
        // 暗転(tips)が明けて結果画面の入力が戻ってから開く。暗転中は EventSystem が止まっていて、欄が選択と Enter を止められない。
        float giveUpAt = Time.unscaledTime + PanelWaitSeconds;
        while ((ScreenTransition.IsBusy || EventSystem.current == null) && Time.unscaledTime < giveUpAt) yield return null;
        if (SceneManager.GetActiveScene().name == ResultScene && ChartMarks.HasKeyMarks && !ScreenTransition.IsBusy
            && EventSystem.current != null)
            ChartMarkCommentPanel.Open(ChartMarks.Current);
    }

    void Update()
    {
        if (toastCanvas != null && toastCanvas.enabled && Time.unscaledTime > toastUntil) toastCanvas.enabled = false;
        while (LostShots.TryDequeue(out string lost)) ChartMarks.ForgetScreenshot(lost);
        string scene = SceneManager.GetActiveScene().name;
        if (scene != GameScene && ChartMarks.UnsavedCount > 0 && Time.unscaledTime >= nextRetryAt)
        {
            nextRetryAt = Time.unscaledTime + RetrySeconds;
            ChartMarks.RetryUnsaved();
        }
        var keyboard = Keyboard.current;
        bool pressed = keyboard != null && keyboard[MarkKey].wasPressedThisFrame;
        if (scene != GameScene)
        {
            if (pressed) PanelOrPhoneCard(scene);
            return;
        }
        Hook();
        if (!pressed) return;
        // 連打は今までどおり黙って無視する。ほかの理由(演奏中でない・判定調整・30個)は左下に出す。
        if (!TryMark(ChartMarks.SourceKey, out string message) && message != TooSoonMessage) ShowToast(message);
    }

    // 演奏中でない画面の F5: 結果画面でこの回の印があれば一言の欄を開き直す(閉じたあとでも何度でも。欄の右に QR も出る)。
    // ほか(印の無い回の結果画面・タイトル・選曲)は、スマホで印の QR を出す / 消す。閉じた F5 と同じコマでは何もしない。
    void PanelOrPhoneCard(string scene)
    {
        if (ChartMarkCommentPanel.IsOpen || Time.frameCount - ChartMarkCommentPanel.LastClosedFrame <= 1) return;
        if (ScreenTransition.IsBusy) return;
        if (scene == ResultScene && ChartMarks.Count > 0) ChartMarkCommentPanel.Open(ChartMarks.Current);
        else ChartMarkPhoneCard.ToggleStandalone();
    }

    // F5 とスマホの印(メインスレッドで)。付けられないときは false と理由(演奏中でない・判定調整/練習・連打・30個)。
    // 付けたときの message は「印3 ・ 18小節目」。画面はこのコマを描き終えてから撮り、左下の札と短い音はそのあと。
    public bool TryMark(string source, out string message)
    {
        source = source == ChartMarks.SourcePhone ? ChartMarks.SourcePhone : ChartMarks.SourceKey;
        message = NotPlayingReason();
        if (message != null) return false;
        // 押し続け・連打で同じ所に何個も付けない(キーとスマホで共通)。
        if (Time.unscaledTime - lastMarkAt < MinIntervalSeconds) { message = TooSoonMessage; return false; }
        if (ChartMarks.Full) { message = "印は1曲" + ChartMarks.MaxMarksPerPlay + "個までです"; return false; }
        bool roomForImage = ChartMarks.FolderBytes() < ChartMarks.MaxFolderBytes;
        var entry = ChartMarks.Add(spawner.CurrentChart, manager.songPlayer.SongTime, spawner.TotalOffsetSeconds, recent,
            GameSession.SelectedSongId, GameSession.SelectedSongTitle, GameSession.SelectedDifficulty, Settings(),
            System.DateTime.Now, source, roomForImage, spawner.secondsPerLongCut);
        if (entry == null) { message = "印を付けられませんでした"; return false; }
        lastMarkAt = Time.unscaledTime;
        bool saved = ChartMarks.LastSaveError.Length == 0;
        // 保存できなかった回は画面も撮らない(あとで保存できたときも「画像なし」)。
        if (!saved) entry.screenshot = "";
        message = "印" + entry.number + " ・ " + entry.bar + "小節目";
        if (!saved) message += " ・ 保存できませんでした(" + ChartMarks.LastSaveError + ")";
        // 撮った画面に札が写らないよう、このコマを描き終えてから撮り、札はそのあとに出す。
        StartCoroutine(CaptureThenToast(entry, ChartMarks.ScreenshotPath(entry), saved, roomForImage));
        Debug.Log("[ChartMarks] 印" + entry.number + " " + entry.id + " source=" + source
            + " t=" + entry.songTime.ToString("0.000", CultureInfo.InvariantCulture));
        return true;
    }

    // 印を付けられないときの理由(付けられるなら null)。Game の本編の演奏中だけ付ける。
    string NotPlayingReason()
    {
        if (SceneManager.GetActiveScene().name != GameScene) return NotPlayingMessage;
        if (spawner == null) Hook(true);
        if (manager == null || spawner == null || spawner.CurrentChart == null || manager.songPlayer == null) return NotPlayingMessage;
        // 判定調整・練習は選んでいる曲の譜面ではないので、その曲の印にしない。
        if (GameSession.IsCalibrationMode || FindFirstObjectByType<CalibrationController>() != null
            || FindFirstObjectByType<TutorialController>() != null) return PracticeMessage;
        return null;
    }

    IEnumerator CaptureThenToast(ChartMarkEntry entry, string shot, bool saved, bool roomForImage)
    {
        yield return new WaitForEndOfFrame();
        if (shot != null && !SaveScreenshot(shot, entry.id)) ChartMarks.ForgetScreenshot(entry.id);
        // スマホの印は、返事をスマホに返す(PC の画面には札も音も出さない。来場者が遊んでいても気づかせない)。
        if (entry.source == ChartMarks.SourcePhone) yield break;
        string text = "印" + entry.number + " ・ " + entry.bar + "小節目 ・ " + ChartMarks.Clock(entry.songTime);
        if (!saved) text = "印を保存できませんでした(" + ChartMarks.LastSaveError + ")";
        else if (!roomForImage) text += " ・ 置き場所がいっぱいなので画面は省きました";
        ShowToast(text, saved ? ToastSeconds : 4f);
        PlayTick();
    }

    // 画面は幅 ScreenshotMaxWidth までに縮めた JPEG。書き込みは別のスレッドで行い、プレイを止めない。
    // 撮れなければ false。書けなかったときは別のスレッドから LostShots に入れ、メインスレッドで「画像なし」にする。
    static bool SaveScreenshot(string path, string markId)
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
                catch (System.Exception e)
                {
                    Debug.LogWarning("[ChartMarks] 画面を書けませんでした: " + e.Message);
                    LostShots.Enqueue(markId);
                }
            });
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[ChartMarks] 画面を撮れませんでした: " + e.Message);
            return false;
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
    // force はスマホの印・PlayState から(1秒に1回の間引きを待たない)。
    void Hook(bool force = false)
    {
        if (spawner != null || (!force && Time.unscaledTime < nextSearchAt)) return;
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
        toastText = SongSelectVisuals.Label(go.transform, "Text", "", 26, Vector2.zero, new Vector2(1240, 44),
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

    // 終わるとき: 開いている欄の一言を書き、書けなかった回を書き直す。それでも書けなければ Player.log に中身を残す。
    void OnApplicationQuit()
    {
        if (ChartMarkCommentPanel.Current != null) ChartMarkCommentPanel.Current.Flush();
        if (ChartMarks.UnsavedCount > 0 && ChartMarks.RetryUnsaved() > 0) ChartMarks.LogAllUnsaved();
    }

    void OnDestroy()
    {
        Unhook();
        UISkinKit.SafeDestroy(tick);
        tick = null;
        if (Instance == this) Instance = null;
    }
}
