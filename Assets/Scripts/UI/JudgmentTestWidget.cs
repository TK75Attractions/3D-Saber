using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// SongSelect 画面の右下に置く、判定オフセットを「その場で」試せる練習ウィジェット。
// BPM 120 のメトロノームクリックと、落下するノートが同時に動き、Space で判定。
// GameSession.JudgmentOffsetMs を即時参照するので、左の JudgmentOffsetWidget の値変更が
// すぐに練習に反映される。
[RequireComponent(typeof(AudioSource))]
public class JudgmentTestWidget : MonoBehaviour
{
    [Header("Tempo")]
    public float bpm = 120f;
    public float noteTravelSeconds = 1.5f;

    [Header("Audio")]
    public float clickFrequency = 880f;
    public float clickDuration = 0.05f;
    [Range(0f, 1f)] public float clickVolume = 0.45f;

    [Header("Visual")]
    public float laneHeight = 150f;

    private AudioSource audioSource;
    private AudioClip clickClip;
    private double startDspTime;
    private bool running;
    private int nextBeatIdx;

    private Text feedbackText;
    private Text errorText;
    private Text toggleText;
    private RectTransform laneRT;
    private RectTransform targetRT;
    private Image beatIndicator;

    private struct TestNote
    {
        public GameObject go;
        public RectTransform rt;
        public double hitTime;
        public bool scored;
    }
    private readonly List<TestNote> notes = new List<TestNote>();
    private readonly Queue<double> pendingClickTimes = new Queue<double>();

    public bool IsRunning => running;
    public int LiveNoteCount => notes.Count;

    public static JudgmentTestWidget Ensure(Canvas canvas)
    {
        if (canvas == null) return null;
        var existing = canvas.GetComponentInChildren<JudgmentTestWidget>(true);
        if (existing != null) return existing;

        var go = new GameObject("JudgmentTestWidget",
            typeof(RectTransform), typeof(Image), typeof(AudioSource));
        go.transform.SetParent(canvas.transform, false);
        var w = go.AddComponent<JudgmentTestWidget>();
        w.Build();
        return w;
    }

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
    }

    void OnDestroy()
    {
        Stop();
        if (clickClip != null)
        {
            if (Application.isPlaying) Destroy(clickClip);
            else DestroyImmediate(clickClip);
            clickClip = null;
        }
    }

    void Build()
    {
        var rt = GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        // JudgmentOffsetWidget (30,30, 440x210) の右隣
        rt.anchoredPosition = new Vector2(490f, 30f);
        rt.sizeDelta = new Vector2(540f, 210f);

        var bg = GetComponent<Image>();
        bg.color = new Color(0.08f, 0.06f, 0.18f, 0.75f);
        var ol = gameObject.AddComponent<Outline>();
        ol.effectColor = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.55f);
        ol.effectDistance = new Vector2(2f, -2f);

        // ヘッダー
        var header = MakeLabel("TIMING PRACTICE", new Vector2(-80f, 78f), 22, FontStyle.Bold, TextAnchor.MiddleLeft);
        header.color = UISkinPalette.Cyan;

        // 開始/停止ボタン（右上）
        BuildToggleButton(new Vector2(195f, 78f));

        // レーン（左側、縦長）
        BuildLane();

        // 判定結果テキスト
        feedbackText = MakeLabel("--", new Vector2(120f, 22f), 38, FontStyle.Bold, TextAnchor.MiddleLeft);
        feedbackText.color = UISkinPalette.OffWhite;

        // 誤差ms
        errorText = MakeLabel("", new Vector2(120f, -22f), 22, FontStyle.Normal, TextAnchor.MiddleLeft);
        errorText.color = UISkinPalette.SubtleGray;

        // 指示
        var inst = MakeLabel("SPACE in time with click", new Vector2(120f, -66f), 18, FontStyle.Normal, TextAnchor.MiddleLeft);
        inst.color = UISkinPalette.SubtleGray;

        // ビートインジケータ
        BuildBeatIndicator();

        // クリック音を事前生成（毎フレ生成しない）
        clickClip = JudgmentSfx.Beep(clickFrequency, clickDuration);
    }

    void BuildLane()
    {
        var lane = new GameObject("Lane", typeof(RectTransform), typeof(Image));
        lane.transform.SetParent(transform, false);
        laneRT = lane.GetComponent<RectTransform>();
        laneRT.anchorMin = new Vector2(0f, 0.5f);
        laneRT.anchorMax = new Vector2(0f, 0.5f);
        laneRT.pivot = new Vector2(0.5f, 0.5f);
        laneRT.anchoredPosition = new Vector2(55f, -12f);
        laneRT.sizeDelta = new Vector2(64f, laneHeight);
        lane.GetComponent<Image>().color = new Color(0.05f, 0.08f, 0.15f, 0.85f);

        // 判定ライン（下端）
        var target = new GameObject("Target", typeof(RectTransform), typeof(Image));
        target.transform.SetParent(laneRT, false);
        targetRT = target.GetComponent<RectTransform>();
        targetRT.anchorMin = new Vector2(0f, 0f);
        targetRT.anchorMax = new Vector2(1f, 0f);
        targetRT.pivot = new Vector2(0.5f, 0.5f);
        targetRT.anchoredPosition = new Vector2(0f, 15f);
        targetRT.sizeDelta = new Vector2(8f, 4f);
        target.GetComponent<Image>().color = UISkinPalette.Cyan;
    }

    void BuildBeatIndicator()
    {
        var go = new GameObject("BeatIndicator", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(55f, 25f);
        rt.sizeDelta = new Vector2(22f, 22f);
        beatIndicator = go.GetComponent<Image>();
        beatIndicator.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.4f);
    }

    void BuildToggleButton(Vector2 pos)
    {
        var go = new GameObject("ToggleBtn", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(110f, 40f);
        rt.anchoredPosition = pos;
        go.GetComponent<Image>().color = new Color(UISkinPalette.Cyan.r * 0.2f, UISkinPalette.Cyan.g * 0.2f, UISkinPalette.Cyan.b * 0.35f, 0.85f);
        var ol = go.AddComponent<Outline>();
        ol.effectColor = UISkinPalette.Cyan;
        ol.effectDistance = new Vector2(2f, -2f);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(Toggle);

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelGo.transform.SetParent(go.transform, false);
        var lrt = labelGo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;
        var t = labelGo.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 20;
        t.fontStyle = FontStyle.Bold;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = UISkinPalette.Cyan;
        t.text = "START";
        t.raycastTarget = false;
        toggleText = t;
    }

    Text MakeLabel(string text, Vector2 pos, int size, FontStyle style, TextAnchor anchor)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(transform, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(380f, 50f);
        rt.anchoredPosition = pos;
        var t = go.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = anchor;
        t.text = text;
        t.color = UISkinPalette.OffWhite;
        t.raycastTarget = false;
        return t;
    }

    public void Toggle()
    {
        if (running) Stop();
        else StartPractice();
    }

    public void StartPractice()
    {
        running = true;
        nextBeatIdx = 0;
        startDspTime = AudioSettings.dspTime + 0.5; // 半秒のプリロール
        pendingClickTimes.Clear();
        if (toggleText) toggleText.text = "STOP";
        ResetFeedback();
    }

    public void Stop()
    {
        running = false;
        ClearNotes();
        pendingClickTimes.Clear();
        if (toggleText) toggleText.text = "START";
        ResetFeedback();
    }

    void ResetFeedback()
    {
        if (feedbackText) { feedbackText.text = "--"; feedbackText.color = UISkinPalette.OffWhite; }
        if (errorText) errorText.text = "";
    }

    void ClearNotes()
    {
        foreach (var n in notes)
        {
            if (n.go != null) Destroy(n.go);
        }
        notes.Clear();
    }

    void Update()
    {
        if (!running) return;
        if (laneRT == null) return;

        double dspNow = AudioSettings.dspTime;
        double songTime = dspNow - startDspTime;
        double offsetSec = GameSession.JudgmentOffsetMs / 1000.0;
        double beatInterval = 60.0 / Mathf.Max(1f, bpm);

        // 必要なビートを先読みでスポーン
        while (true)
        {
            double nextBeatTime = nextBeatIdx * beatInterval;
            double hitTime = nextBeatTime + offsetSec;
            double spawnTime = hitTime - noteTravelSeconds;
            if (songTime < spawnTime) break;
            SpawnNote(hitTime);
            pendingClickTimes.Enqueue(startDspTime + nextBeatTime);
            nextBeatIdx++;
        }

        // 予定クリックを再生
        while (pendingClickTimes.Count > 0 && pendingClickTimes.Peek() <= dspNow)
        {
            pendingClickTimes.Dequeue();
            if (audioSource != null && clickClip != null)
            {
                audioSource.PlayOneShot(clickClip, clickVolume);
            }
        }

        UpdateNotes(songTime);
        UpdateBeatIndicator(songTime, beatInterval);

        // 入力
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            HitAttempt(songTime);
        }
    }

    void UpdateNotes(double songTime)
    {
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var n = notes[i];
            if (n.go == null) { notes.RemoveAt(i); continue; }
            double dt = n.hitTime - songTime;
            float progress = 1f - Mathf.Clamp01((float)(dt / noteTravelSeconds));
            float yTop = -8f;
            float yBottom = -(laneHeight - 15f);
            n.rt.anchoredPosition = new Vector2(0f, Mathf.Lerp(yTop, yBottom, progress));
            // 0.4s 過ぎたら回収（スコア未済なら Miss）
            if (dt < -0.4)
            {
                if (!n.scored)
                {
                    ShowFeedback(JudgmentTier.Miss, double.NaN);
                }
                Destroy(n.go);
                notes.RemoveAt(i);
            }
        }
    }

    void UpdateBeatIndicator(double songTime, double beatInterval)
    {
        if (beatIndicator == null) return;
        if (songTime < 0) { SetIndicatorAlpha(0.25f); return; }
        double phase = songTime / beatInterval;
        double sinceBeat = (phase - System.Math.Floor(phase)) * beatInterval;
        float pulse = 1f - Mathf.Clamp01((float)(sinceBeat / 0.18));
        SetIndicatorAlpha(0.25f + 0.65f * pulse);
    }

    void SetIndicatorAlpha(float a)
    {
        if (beatIndicator == null) return;
        Color c = UISkinPalette.Cyan;
        c.a = Mathf.Clamp01(a);
        beatIndicator.color = c;
    }

    void SpawnNote(double hitTime)
    {
        if (laneRT == null) return;
        var go = new GameObject("TestNote", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(laneRT, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(44f, 22f);
        rt.anchoredPosition = new Vector2(0f, -8f);
        go.GetComponent<Image>().color = UISkinPalette.Cyan;
        notes.Add(new TestNote { go = go, rt = rt, hitTime = hitTime, scored = false });
    }

    public void HitAttempt(double songTime)
    {
        int idx = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < notes.Count; i++)
        {
            if (notes[i].scored) continue;
            double err = System.Math.Abs(songTime - notes[i].hitTime);
            if (err < bestDist) { bestDist = err; idx = i; }
        }
        if (idx < 0 || bestDist > 0.27)
        {
            ShowFeedback(JudgmentTier.Miss, double.NaN);
            return;
        }
        double error = songTime - notes[idx].hitTime;
        var tier = JudgmentTierHelper.Classify(error);
        var n = notes[idx];
        n.scored = true;
        notes[idx] = n;
        ShowFeedback(tier, error);
        if (n.go != null) Destroy(n.go, 0.1f);
    }

    void ShowFeedback(JudgmentTier tier, double errorSec)
    {
        if (feedbackText != null)
        {
            feedbackText.text = JudgmentTierHelper.Label(tier);
            feedbackText.color = TierColor(tier);
        }
        if (errorText != null)
        {
            if (double.IsNaN(errorSec))
            {
                errorText.text = "";
            }
            else
            {
                int ms = (int)System.Math.Round(errorSec * 1000.0);
                string sign = ms > 0 ? "+" : "";
                errorText.text = $"{sign}{ms} ms";
            }
        }
    }

    Color TierColor(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return UISkinPalette.Cyan;
            case JudgmentTier.Great: return UISkinPalette.Yellow;
            case JudgmentTier.Good: return UISkinPalette.Orange;
            case JudgmentTier.Bad: return UISkinPalette.Magenta;
            default: return UISkinPalette.SubtleGray;
        }
    }
}
