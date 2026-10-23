using TMPro;
using UnityEngine;
using UnityEngine.UI;

// コンボ節目・FC/APの炎・曲の締めの音を管理。達成の文字演出はClearAchievementPresentationに集約する。
// GamePlayManagerが一か所からTickする。
public sealed class GameplayFeedbackPresenter : MonoBehaviour
{
    NoteSpawner spawner;
    ScoreManager score;
    TextMeshProUGUI milestone;
    float milestoneAge = 99;
    int latestMilestone;
    // コンボ数の後ろの炎(2026-09-23 ユーザー依頼): AP中は虹色、FC中は金色、曲の進行で強くなり、条件が崩れたら消える。
    ComboFlameGraphic flame;
    readonly ComboFlameEnvelope flameEnvelope = new ComboFlameEnvelope();
    float flameTime;
    public ComboFlameMode FlameMode => flameEnvelope.Shown;
    public float FlameLevel => flameEnvelope.Level;
    public bool OutroStarted { get; private set; }
    public int LatestMilestone => latestMilestone;
    // 曲の締めの短く上がる音。重複していたFULL COMBO / TRACK CLEARの簡易札は表示しない。
    // FULL COMBO / ALL PERFECT の和音は、続く ClearAchievementPresentation が鳴らす。
    public const float OutroVolume = .5f;
    AudioSource outroSource;
    AudioClip outroClip;
    public bool OutroSoundPlayed { get; private set; }
    public AudioClip OutroClip => outroClip;
    // コンボの節目(派手さカタログ 波1、2026-10-20)。30・50・100・以後50ごとに、G の長調の分散和音を一段ずつ上の形で鳴らし、
    // 100 と 300 では短い歓声を足す。ゲートの外の輪は OnMilestone を聞く GateAccents が描く。
    public event System.Action<int> OnMilestone;
    public int MilestoneSoundCount { get; private set; }
    AudioSource milestoneSource;
    readonly AudioClip[] milestoneClips = new AudioClip[4];
    AudioClip crowdClip;

    public static GameplayFeedbackPresenter Create(NoteSpawner spawner, ScoreManager score)
    {
        var go = new GameObject("GameplayFeedback", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        var result = go.AddComponent<GameplayFeedbackPresenter>();
        result.Build(spawner, score);
        return result;
    }

    void Build(NoteSpawner owner, ScoreManager scoring)
    {
        spawner = owner; score = scoring;
        var canvas = GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 505;
        var scaler = GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        // 曲中の初回判定でフォントアトラスを増やさないよう、使う字を開始前に準備する。
        var labelFont = UISkinKit.FontAsset("Oxanium-Bold");
        if (labelFont != null) labelFont.TryAddCharacters("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 /,.ms");
        milestone = Text(transform, "ComboMilestone", "", 25, new Vector2(300, 36), Vector2.zero, TextAlignmentOptions.TopRight);
        TopRight(milestone.rectTransform, new Vector2(-74, -414));
        BuildFlame();
        if (Application.isPlaying)
        {
            milestoneSource = gameObject.AddComponent<AudioSource>();
            milestoneSource.playOnAwake = false; milestoneSource.spatialBlend = 0;
            // 曲中に合成して処理落ちしないよう、開始前に作っておく。
            for (int i = 0; i < milestoneClips.Length; i++)
            {
                int step = i;
                milestoneClips[i] = ProceduralSfx.Clip("ComboMilestone" + i, FlourishSfx.Cached("Milestone" + i, () => FlourishSfx.Milestone(step)));
            }
            crowdClip = ProceduralSfx.Clip("ComboCrowd", FlourishSfx.Cached("CrowdShort", FlourishSfx.CrowdShort));
        }
        // ロングは途中の1回ごとにコンボが増えるので、その回の通知でも節目を確かめる。
        if (score != null) { score.OnJudgment += Scored; score.OnLongCut += LongCut; }
    }

    // 炎は HUD(sortingOrder 500)のコンボ数字の下に描く。数字の右端(-70)と下端(約-292)に矩形の右下を合わせる。
    void BuildFlame()
    {
        var holder = new GameObject("ComboFlame", typeof(RectTransform), typeof(Canvas));
        holder.transform.SetParent(transform, false);
        var canvas = holder.GetComponent<Canvas>(); canvas.overrideSorting = true; canvas.sortingOrder = 499;
        var holderRect = (RectTransform)holder.transform;
        holderRect.anchorMin = holderRect.anchorMax = Vector2.one; holderRect.pivot = new Vector2(1, 0);
        holderRect.anchoredPosition = new Vector2(-70, -292); holderRect.sizeDelta = new Vector2(340, 260);
        var graphicGo = new GameObject("ComboFlameGraphic", typeof(RectTransform));
        graphicGo.transform.SetParent(holder.transform, false);
        var graphicRect = (RectTransform)graphicGo.transform;
        graphicRect.anchorMin = graphicRect.anchorMax = new Vector2(1, 0); graphicRect.pivot = new Vector2(1, 0);
        graphicRect.anchoredPosition = Vector2.zero; graphicRect.sizeDelta = new Vector2(340, 260);
        flame = graphicGo.AddComponent<ComboFlameGraphic>(); flame.raycastTarget = false;
    }

    void TickFlame(float delta, double songTime, double duration)
    {
        flameTime += delta;
        var desired = score == null ? ComboFlameMode.None
            : ComboFlameLogic.DesiredMode(score.HitCount, score.PerfectCount, score.BadCount, score.MissCount);
        flameEnvelope.Tick(desired, delta);
        if (flame == null) return;
        int digits = score != null ? Mathf.Max(1, score.Combo).ToString().Length : 1;
        flame.SetState(flameEnvelope.Shown, ComboFlameLogic.Intensity(songTime, duration), flameEnvelope.Level, flameTime,
            ComboFlameLogic.FlameWidth(digits));
    }

    public static bool FullComboEligible(int hits, int bad, int miss) => hits > 0 && bad == 0 && miss == 0;
    // 節目は 30・50・100、そのあとは50ごと。初めての人にも早めに1回目を見せる(osu! は30から)。
    public static bool IsMilestone(int combo) => combo == 30 || combo == 50 || combo >= 100 && combo % 50 == 0;
    // previous から current までの間に節目を通ったか(赤青を同じコマで切ると 29→31 のように飛ぶ)。
    public static bool CrossedMilestone(int previous, int current)
    {
        for (int c = System.Math.Max(previous + 1, 1); c <= current; c++) if (IsMilestone(c)) return true;
        return false;
    }
    // 何段目の形で鳴らすか(30=0、50=1、100=2、それ以降=3)。
    public static int MilestoneStep(int combo) => combo < 50 ? 0 : combo < 100 ? 1 : combo < 150 ? 2 : 3;
    // 輪を二重にし、歓声を足す節目。
    public static bool IsBigMilestone(int combo) => combo == 100 || combo == 300;
    public static bool CanEnd(double time, double duration, double lastNote, int total, int judged, int spawned)
        => total > 0 && judged >= total && spawned >= total && duration > 0 && time >= System.Math.Max(duration, lastNote);

    void LongCut(JudgmentTier tier, int award, bool wasWrongFlick) => Scored(tier, award);

    void Scored(JudgmentTier tier, int award)
    {
        if (score == null || !isActiveAndEnabled) return;
        if (score.Combo > 0 && IsMilestone(score.Combo) && score.Combo != latestMilestone)
        {
            latestMilestone = score.Combo; milestoneAge = 0; milestone.text = score.Combo + " COMBO";
            PlayMilestone(score.Combo);
            OnMilestone?.Invoke(score.Combo);
        }
        if (score.Combo == 0) { milestoneAge = 99; milestone.text = ""; latestMilestone = 0; }
    }

    void PlayMilestone(int combo)
    {
        MilestoneSoundCount++;
        if (milestoneSource == null) return;
        // 作った音は鳴らす前に中身を確かめる(音の出力先が変わると消えるため。ProceduralSfx.Revive)。
        var clip = ProceduralSfx.Revive(ref milestoneClips[MilestoneStep(combo)]);
        if (clip != null) milestoneSource.PlayOneShot(clip, 1f);
        if (IsBigMilestone(combo) && crowdClip != null) milestoneSource.PlayOneShot(ProceduralSfx.Revive(ref crowdClip), 1f);
    }

    public void Tick(float delta, double songTime, double duration, double lastNote)
    {
        if (!isActiveAndEnabled) return;
        delta = Mathf.Clamp(delta, 0, .1f);
        TickFlame(delta, songTime, duration);
        milestoneAge += delta;
        milestone.color = new Color(.79f, .9f, .93f, 1 - Mathf.InverseLerp(.6f, 1.1f, milestoneAge));
        milestone.rectTransform.localScale = Vector3.one * (1 + (DisplaySettings.ReducedEffects ? 0 : .12f) * Mathf.Clamp01(1 - milestoneAge / .22f));
        // 判定の数はロングの回ごとに増えるので、判定し終えたノーツの数で比べる。
        if (!OutroStarted && score != null && spawner != null && CanEnd(songTime, duration, lastNote, spawner.TotalNoteCount,
            score.JudgedNoteCount, spawner.NextIndex)) BeginOutro();
    }
    public void BeginOutro()
    {
        if (OutroStarted) return;
        OutroStarted = true;
        PlayOutroSound();
    }
    void PlayOutroSound()
    {
        if (outroClip == null) outroClip = ProceduralSfx.Clip("TrackClearRise", FlourishSfx.Cached("TrackClearRise", () => ProceduralSfx.Rise()));
        OutroSoundPlayed = true;
        if (!Application.isPlaying) return;
        if (outroSource == null)
        {
            outroSource = gameObject.AddComponent<AudioSource>();
            outroSource.playOnAwake = false; outroSource.spatialBlend = 0;
        }
        outroSource.PlayOneShot(ProceduralSfx.Revive(ref outroClip), OutroVolume);
    }
    void OnDestroy()
    {
        if (score != null) { score.OnJudgment -= Scored; score.OnLongCut -= LongCut; }
        UISkinKit.SafeDestroy(outroClip);
        for (int i = 0; i < milestoneClips.Length; i++) { UISkinKit.SafeDestroy(milestoneClips[i]); milestoneClips[i] = null; }
        UISkinKit.SafeDestroy(crowdClip);
    }
    static RectTransform Rect(Transform parent, string name, Vector2 size, Vector2 position)
    {
        var root = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(parent, false); root.anchorMin = root.anchorMax = root.pivot = new Vector2(.5f, .5f);
        root.sizeDelta = size; root.anchoredPosition = position; return root;
    }
    static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Vector2 bounds, Vector2 position, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
    {
        var result = UISkinKit.MakeTMP(parent, name, text, size, new Color(.83f, .89f, .92f), alignment,
            position, bounds, FontStyles.Normal, 0, UISkinKit.FontAsset("Oxanium-Bold"));
        result.raycastTarget = false; result.enableAutoSizing = true; result.fontSizeMin = size * .8f; result.fontSizeMax = size;
        return result;
    }
    static void TopRight(RectTransform root, Vector2 position)
    { root.anchorMin = root.anchorMax = root.pivot = Vector2.one; root.anchoredPosition = position; }
}
