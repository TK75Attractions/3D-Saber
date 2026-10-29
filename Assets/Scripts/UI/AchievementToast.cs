using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 実績解除の知らせ(2026-10-29 ユーザー「参加者が達成したときにxboxみたいな感じで上にポップアップ？が出てきて~をした ~人目ってのが
// わかるだけにして(5秒以内に消す)」。同日 v2.2「高難易度と思われる実績はふちを銅、銀、金、虹に光らせて効果音も変えて。表示の順番は
// 簡単なものから。あと右上のスペースにポップアップすればもっと大きく実績を出せるよね」)。
// 画面の右上(曲名の右、判定の表の上の空き)に大きいカードを2枚まで: トロフィーの印・小さく「実績解除」・「〜した ○人目」(○人目は金)。
// 1枚は入り 0.3 秒・止まり 3.7 秒・消え 0.5 秒の 4.5 秒。ただし段なしのカードは、後ろに待っているカードがあるうちは 2.2 秒で消す
// (簡単なものから出すので、いちばん難しい実績を来場者が帰る前に出す)。寿命と光の動きは出し始めた実時刻から数える。
// 段の低い順に並べ、同じ段では渡された順を保つ。段によってふち・トロフィー・鈴を変える(AchievementCardFrame)。
// 演奏中(Game シーン)と幕の間は新しく出さない。出したシーンを出たら、残りは出さない(次の人へ持ち越さない)。
// すべて raycastTarget=false。並び順は照準(900)より上、印の一言の欄(920)・運営の知らせ(950)より下。
public sealed class AchievementToast : MonoBehaviour
{
    public const float EnterSeconds = .3f, HoldSeconds = 3.7f, ExitSeconds = .5f;
    public const float LifeSeconds = EnterSeconds + HoldSeconds + ExitSeconds;
    // 段なしのカードの、後ろに待っているカードがあるときの止まりの秒(読める短さ)。
    public const float ShortHoldSeconds = 1.4f;
    public const float ShortLifeSeconds = EnterSeconds + ShortHoldSeconds + ExitSeconds;
    public const int MaxVisible = 2;
    public const float StaggerSeconds = .12f;
    // 結果画面で出し始める、結果の演出の時刻(ランクの札の着地。演出を飛ばすと演出の時刻はこれを超える)。
    public const float ResultLandingSeconds = ResultSkin.RankLandingTime;
    public const int SortingOrder = 910;
    public const float ReferenceWidth = 1920f, ReferenceHeight = 1080f;
    public const float CardWidth = 540f, CardHeight = 126f, RightMargin = 24f, TopMargin = 18f, Gap = 12f, IconSize = 84f;
    public const float EnterSlide = 40f, ExitSlide = 20f;
    public const float CaptionSize = 20f, BodyFontMin = 24f, BodyFontMax = 32f;
    public const string Caption = "実績解除";
    // 「実績解除」の横に段の数だけ並べる小さな印(銅 1・銀 2・金 3・虹 4)。色だけでなく形でも段が分かるように
    // (白く浮く投影や色の見え方で、銅と金・銀となしが近く見えることがある)。
    public const char TierMark = '◆';
    public const string NumberColor = "#FFD84D";
    public const string ChimeKey = "AchievementChime";
    public const double ChimePeakDb = -18;
    // 直前に鳴った音と同じ段以下は、この間隔をあける。上の段は間隔によらず鳴る。
    public const double ChimeGapSeconds = .4;
    const string GameScene = "Game";

    static readonly Color PanelFill = new Color(.025f, .035f, .085f, .94f);

    public static AchievementToast Instance { get; private set; }

    readonly AchievementToastQueue queue = new AchievementToastQueue(MaxVisible);
    readonly CardView[] views = new CardView[MaxVisible];
    double lastChime = double.NegativeInfinity;
    AchievementTier lastChimeTier;
    bool built;

    sealed class CardView
    {
        public RectTransform Root;
        public CanvasGroup Group;
        public TextMeshProUGUI Body, Caption;
        public AchievementCardFrame Frame;
        public AchievementTrophyGraphic Trophy;
        public string Shown;
        public AchievementTier ShownTier;
    }

    public AchievementToastQueue Queue => queue;

    // OrderBy は安定な並べ替え。同じ段の中では、呼ぶ側が渡したカタログの順を保つ。
    public static void Show(IEnumerable<AchievementUnlock> unlocks)
    {
        if (unlocks == null) return;
        AchievementToast host = null;
        foreach (var unlock in unlocks.Where(u => u.Definition != null).OrderBy(u => u.Tier))
        {
            if (host == null) host = Ensure();
            host.queue.Enqueue(Message(unlock.Text, unlock.Number), unlock.Tier);
        }
    }

    public static void Show(string text, int number, AchievementTier tier = AchievementTier.None) =>
        Ensure().queue.Enqueue(Message(text, number), tier);

    // 通知の文: 「〜した ○人目」(○人目は金の太字)。番号が無い(記録できなかった)ときは文だけ。
    public static string Message(string text, int number) =>
        number > 0 ? (text ?? "") + "  <color=" + NumberColor + "><b>" + number + "人目</b></color>" : text ?? "";

    // カードの上の小さな見出し(段のあるカードは、段の数の◆をつける)。
    public static string CaptionFor(AchievementTier tier) =>
        tier > AchievementTier.None && tier <= AchievementTier.Rainbow ? Caption + "  " + new string(TierMark, (int)tier) : Caption;

    // 新しいカードを出してよいか(演奏中・幕の間・結果画面の順位のリールが回っている間は出さない)。
    public static bool CanStart(string sceneName, bool transitionBusy, bool rankingWheel = false) =>
        sceneName != GameScene && !transitionBusy && !rankingWheel;

    // 今のシーンに1つ。
    public static AchievementToast Ensure()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("AchievementToast", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        var host = go.AddComponent<AchievementToast>();
        host.Build();
        Instance = host;
        return host;
    }

    void Build()
    {
        if (built) return;
        built = true;
        var canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        var scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        for (int slot = 0; slot < MaxVisible; slot++) views[slot] = BuildCard(transform, slot);
    }

    static CardView BuildCard(Transform parent, int slot)
    {
        var root = SongSelectVisuals.Rect(parent, "AchievementCard" + slot, SlotPosition(slot), new Vector2(CardWidth, CardHeight));
        root.anchorMin = root.anchorMax = Vector2.one;
        root.pivot = Vector2.one;
        root.anchoredPosition = SlotPosition(slot);
        var group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false;
        var panel = SongSelectVisuals.Panel(root, "Panel", Vector2.zero, new Vector2(CardWidth, CardHeight),
            PanelFill, Color.clear, AchievementCardFrame.CornerCut);
        panel.borderWidth = 0f;
        var frame = SongSelectVisuals.Rect(root, "Frame", Vector2.zero, new Vector2(CardWidth, CardHeight))
            .gameObject.AddComponent<AchievementCardFrame>();
        frame.raycastTarget = false;
        float iconX = -CardWidth * .5f + 18f + IconSize * .5f;
        var icon = SongSelectVisuals.Rect(root, "Trophy", new Vector2(iconX, 0f), new Vector2(IconSize, IconSize))
            .gameObject.AddComponent<AchievementTrophyGraphic>();
        icon.raycastTarget = false;
        float left = iconX + IconSize * .5f + 16f, right = CardWidth * .5f - 18f;
        var font = UISkinKit.FontAsset("Oxanium-Bold");
        var caption = UISkinKit.MakeTMP(root, "Caption", Caption, CaptionSize, AchievementCardFrame.TierColor(AchievementTier.None),
            TextAlignmentOptions.MidlineLeft, new Vector2((left + right) * .5f, 38f),
            new Vector2(right - left, 24f), FontStyles.Bold, 2f, font);
        caption.raycastTarget = false;
        var body = UISkinKit.MakeTMP(root, "Body", "", BodyFontMax, UISkinPalette.OffWhite, TextAlignmentOptions.MidlineLeft,
            new Vector2((left + right) * .5f, -16f), new Vector2(right - left, 72f), FontStyles.Bold, 0f, font);
        body.enableAutoSizing = true; body.fontSizeMin = BodyFontMin; body.fontSizeMax = BodyFontMax;
        body.textWrappingMode = TextWrappingModes.Normal;
        body.maxVisibleLines = 2;
        body.overflowMode = TextOverflowModes.Ellipsis;
        body.raycastTarget = false;
        root.gameObject.SetActive(false);
        return new CardView { Root = root, Group = group, Body = body, Caption = caption, Frame = frame, Trophy = icon };
    }

    // 上から slot 番目のカードの右上。画面の右上を基準にする。
    public static Vector2 SlotPosition(int slot) => new Vector2(-RightMargin, -(TopMargin + slot * (CardHeight + Gap)));

    // 見えるカードが占める、画面の上からの高さ(1920×1080 基準)。
    public static float CoveredHeight => TopMargin + MaxVisible * CardHeight + (MaxVisible - 1) * Gap;

    public struct Pose { public float Alpha, Offset, Scale; }

    // カードを出し始めるときに決める寿命: 段なしで後ろに待っているカードがあれば短く、ほかは 4.5 秒。
    public static float LifeFor(AchievementTier tier, bool moreWaiting) =>
        tier == AchievementTier.None && moreWaiting ? ShortLifeSeconds : LifeSeconds;

    // Offset は右へずらす量(px)。演出を下げる設定では移動・拡大をせず、透明度だけで出入りする。
    public static Pose CardPose(double age, bool reducedEffects = false) => CardPose(age, LifeSeconds, reducedEffects);

    // 寿命 life のカードの見え方(入りと消えの長さは同じで、止まりが短くなる)。
    public static Pose CardPose(double age, float life, bool reducedEffects)
    {
        double holdSeconds = Math.Max(0, life - EnterSeconds - ExitSeconds);
        // 消える時刻は寿命そのもので比べる(入り・止まり・消えを足し直すと、丸めで寿命ちょうどに透明になりきらない)。
        if (age < 0 || age >= life) return new Pose { Alpha = 0f, Offset = 0f, Scale = 1f };
        if (age < EnterSeconds)
        {
            float k = (float)(age / EnterSeconds), e = 1f - (1f - k) * (1f - k) * (1f - k);
            return new Pose
            {
                Alpha = e,
                Offset = reducedEffects ? 0f : (1f - e) * EnterSlide,
                Scale = reducedEffects ? 1f : Mathf.Lerp(.96f, 1f, e),
            };
        }
        double leave = age - EnterSeconds - holdSeconds;
        if (leave <= 0) return new Pose { Alpha = 1f, Offset = 0f, Scale = 1f };
        float t = Mathf.Clamp01((float)(leave / ExitSeconds));
        return new Pose { Alpha = 1f - t * t, Offset = reducedEffects ? 0f : t * ExitSlide, Scale = 1f };
    }

    void Update()
    {
        Step(Time.realtimeSinceStartupAsDouble,
            CanStart(SceneManager.GetActiveScene().name, ScreenTransition.IsBusy, DailyRankingWheel.Showing));
    }

    // 実時刻 now で進める(テストからも呼ぶ)。何もかも消えたら自分も消える。
    public void Step(double now, bool canStart)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return;
        Build();
        queue.Tick(now, canStart, PlayChime);
        bool reduced = DisplaySettings.ReducedEffects;
        for (int slot = 0; slot < MaxVisible; slot++) Refresh(views[slot], queue.At(slot), slot, now, reduced);
        if (queue.Idle && Application.isPlaying) Destroy(gameObject);
    }

    void PlayChime(AchievementToastQueue.Card card)
    {
        if (card.StartedAt - lastChime < ChimeGapSeconds - 1e-9 && (int)card.Tier <= (int)lastChimeTier) return;
        lastChime = card.StartedAt;
        lastChimeTier = card.Tier;
        AchievementTier tier = card.Tier;
        MenuSfx.Play(ChimeKeyFor(tier), () => ChimeSamples(tier));
    }

    static void Refresh(CardView view, AchievementToastQueue.Card card, int slot, double now, bool reduced)
    {
        if (view == null) return;
        if (card == null)
        {
            if (view.Root.gameObject.activeSelf) view.Root.gameObject.SetActive(false);
            view.Shown = null;
            return;
        }
        if (!view.Root.gameObject.activeSelf) view.Root.gameObject.SetActive(true);
        if (view.Shown != card.Message || view.ShownTier != card.Tier)
        {
            view.Shown = card.Message; view.ShownTier = card.Tier;
            view.Body.text = card.Message; view.Caption.text = CaptionFor(card.Tier);
        }
        double age = now - card.StartedAt;
        var pose = CardPose(age, card.Life, reduced);
        view.Group.alpha = pose.Alpha;
        view.Root.anchoredPosition = SlotPosition(slot) + new Vector2(pose.Offset, 0f);
        view.Root.localScale = new Vector3(pose.Scale, pose.Scale, 1f);
        view.Frame.SetAppearance(card.Tier, age, reduced);
        view.Trophy.SetAppearance(card.Tier, age, reduced);
        view.Caption.color = AchievementCardFrame.EdgeColor(card.Tier, 0f, age, reduced);
    }

    // 見えているカード(上から)。テストと確認用。
    public IEnumerable<RectTransform> VisibleCards()
    {
        foreach (var view in views)
            if (view != null && view.Root != null && view.Root.gameObject.activeSelf) yield return view.Root;
    }

    void OnDestroy()
    {
        // 出したシーンを出たら、残りは出さない。
        queue.Clear();
        if (Instance == this) Instance = null;
    }

    public static string ChimeKeyFor(AchievementTier tier)
    {
        switch (tier)
        {
            case AchievementTier.Bronze: return "AchievementChimeBronze";
            case AchievementTier.Silver: return "AchievementChimeSilver";
            case AchievementTier.Gold: return "AchievementChimeGold";
            case AchievementTier.Rainbow: return "AchievementChimeRainbow";
            default: return ChimeKey;
        }
    }

    public static float[] ChimeSamples() => ChimeSamples(AchievementTier.None);

    // None の2音は従来の波形のまま。段が上がるごとに音を足し、ピークは -18〜-14 dBFS にそろえる。
    public static float[] ChimeSamples(AchievementTier tier)
    {
        if ((int)tier < (int)AchievementTier.None || (int)tier > (int)AchievementTier.Rainbow) tier = AchievementTier.None;
        if (tier == AchievementTier.None)
        {
            SfxLab.Reseed(29);
            return SfxLab.Finish(SfxLab.Mix(.6, new SfxLab.Layer(Bell(.45, SfxLab.Midi(86))), new SfxLab.Layer(Bell(.5, SfxLab.Midi(91)), .08, -2)),
                ChimePeakDb);
        }

        SfxLab.Reseed(29u + 101u * (uint)tier);
        var layers = new List<SfxLab.Layer>();
        float[] samples;
        switch (tier)
        {
            case AchievementTier.Bronze:
                AddBells(layers, new[] { 79, 83, 86 }, 0, .1);
                samples = SfxLab.Mix(.7, layers.ToArray());
                break;
            case AchievementTier.Silver:
                AddBells(layers, new[] { 91, 95, 98, 103 }, 0, .09);
                layers.Add(new SfxLab.Layer(Sparkle(), .58, -6));
                samples = SfxLab.Mix(.9, layers.ToArray());
                break;
            case AchievementTier.Gold:
                foreach (int note in new[] { 67, 71, 74 })
                    layers.Add(new SfxLab.Layer(Brass(.85, SfxLab.Midi(note)), 0, -8));
                AddBells(layers, new[] { 79, 83, 86, 91 }, .18, .12, -3);
                samples = SfxLab.Reverb(SfxLab.Mix(1.05, layers.ToArray()), .35, .18);
                break;
            default:
                // G の長音階を2オクターブ駆け上がり、広い音域の長三和音を重ねる。
                AddBells(layers, new[] { 67, 69, 71, 72, 74, 76, 78, 79, 81, 83, 84, 86, 88, 90, 91 }, 0, .055, -3);
                foreach (int note in new[] { 55, 71, 86 })
                    layers.Add(new SfxLab.Layer(Brass(1.05, SfxLab.Midi(note)), .35, -9));
                layers.Add(new SfxLab.Layer(Sparkle(), .95, -5));
                samples = SfxLab.Reverb(SfxLab.Mix(1.5, layers.ToArray()), .5, .24);
                break;
        }
        return SfxLab.Finish(samples, ChimePeakDb + (int)tier);
    }

    static void AddBells(List<SfxLab.Layer> layers, int[] notes, double start, double gap, double gainDb = 0)
    {
        for (int i = 0; i < notes.Length; i++)
            layers.Add(new SfxLab.Layer(Bell(.5, SfxLab.Midi(notes[i])), start + gap * i, gainDb));
    }

    static float[] Bell(double seconds, double hz) =>
        SfxLab.Env(SfxLab.Additive(seconds, new[] { (hz, 1.0), (hz * 2.01, .22), (hz * 3.02, .06) }), SfxLab.AdExp(.004, .14));

    static float[] Brass(double seconds, double hz) =>
        SfxLab.Env(SfxLab.Additive(seconds, new[] { (hz, 1.0), (hz * 2, .55), (hz * 3, .28), (hz * 4, .12) }, .003),
            t => Math.Min(1, t / .16) * Math.Exp(-Math.Max(0, t - .16) / .35)
                * Math.Min(1, Math.Max(0, (seconds - t) / .15)));

    static float[] Sparkle() =>
        SfxLab.Mix(.24,
            new SfxLab.Layer(Bell(.2, SfxLab.Midi(103)), 0, -10),
            new SfxLab.Layer(Bell(.2, SfxLab.Midi(108)), .035, -12),
            new SfxLab.Layer(SfxLab.Env(SfxLab.Hp(SfxLab.Noise(.2), 7000), SfxLab.AdExp(.002, .045)), 0, -24));
}

// 実績の知らせの並び(画面の部品を持たない。テストから直接使う)。上から Capacity 枚まで出し、残りは空いた所へ順番に。
// 時刻はすべて実時刻(秒)。カードは出し始めた時刻から、そのとき決めた寿命(AchievementToast.LifeFor)で消える
// (処理落ちのコマがあっても実時間で消える)。
public sealed class AchievementToastQueue
{
    public sealed class Card
    {
        public string Message;
        public AchievementTier Tier;
        public double StartedAt;
        public float Life = AchievementToast.LifeSeconds;
    }

    readonly Queue<Card> waiting = new Queue<Card>();
    readonly Card[] slots;
    double lastStart = double.NegativeInfinity;

    public AchievementToastQueue(int capacity) { slots = new Card[Math.Max(1, capacity)]; }

    public int Capacity => slots.Length;
    public int Waiting => waiting.Count;
    public int VisibleCount { get { int n = 0; foreach (var card in slots) if (card != null) n++; return n; } }
    public bool Idle => waiting.Count == 0 && VisibleCount == 0;
    public Card At(int slot) => slot >= 0 && slot < slots.Length ? slots[slot] : null;

    public void Enqueue(string message, AchievementTier tier = AchievementTier.None) =>
        waiting.Enqueue(new Card { Message = message ?? "", Tier = tier });

    // 実時刻 now まで進める。消える時刻を過ぎたカードを外し、canStart なら空いた段へ次を出す(前に出してから StaggerSeconds あける)。
    // canStart が false の間(演奏中・幕の間)は新しく出さない(出ているカードは時刻どおり消えていく)。出した枚数を返す。
    public int Tick(double now, bool canStart, Action<Card> onStarted = null)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return 0;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null && now - slots[i].StartedAt >= slots[i].Life) slots[i] = null;
        if (!canStart) return 0;
        int started = 0;
        while (waiting.Count > 0 && now - lastStart >= AchievementToast.StaggerSeconds - 1e-9)
        {
            int free = Array.IndexOf(slots, null);
            if (free < 0) break;
            var card = waiting.Dequeue();
            card.StartedAt = now;
            card.Life = AchievementToast.LifeFor(card.Tier, waiting.Count > 0);
            slots[free] = card;
            lastStart = now;
            started++;
            onStarted?.Invoke(card);
        }
        return started;
    }

    public void Clear()
    {
        waiting.Clear();
        for (int i = 0; i < slots.Length; i++) slots[i] = null;
        lastStart = double.NegativeInfinity;
    }
}

// 知らせのカードの左の丸い印。段の色の輪・暗い円・トロフィーを、画像を使わずに描く。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class AchievementTrophyGraphic : MaskableGraphic
{
    static readonly Color QuietPlatinum = new Color(.78f, .8f, .76f);
    public Color ringColor = QuietPlatinum;
    public Color discColor = new Color(.05f, .07f, .15f, 1f);
    public Color trophyColor = QuietPlatinum;
    const int Segments = 48;

    AchievementTier tier;
    double age;
    bool reducedEffects;

    public void SetAppearance(AchievementTier value, double seconds, bool reduced)
    {
        tier = value;
        age = reduced ? 0 : seconds;
        reducedEffects = reduced;
        ringColor = value == AchievementTier.None
            ? QuietPlatinum : AchievementCardFrame.EdgeColor(value, 0f, age, reduced);
        trophyColor = value == AchievementTier.Rainbow ? AchievementCardFrame.TierColor(AchievementTier.Gold) : ringColor;
        raycastTarget = false;
        SetVerticesDirty();
    }

    // 杯の形(半径を 1 とした座標。上の縁から丸い底まで、凸の多角形)。
    static readonly Vector2[] Cup =
    {
        new Vector2(-.36f, .42f), new Vector2(.36f, .42f), new Vector2(.35f, .30f), new Vector2(.32f, .18f), new Vector2(.27f, .07f),
        new Vector2(.19f, -.02f), new Vector2(.09f, -.09f), new Vector2(0f, -.11f), new Vector2(-.09f, -.09f), new Vector2(-.19f, -.02f),
        new Vector2(-.27f, .07f), new Vector2(-.32f, .18f), new Vector2(-.35f, .30f),
    };

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = rectTransform.rect;
        float radius = Mathf.Min(r.width, r.height) * .5f;
        if (radius <= 0f) return;
        Vector2 c = r.center;
        Ring(vh, c, radius);
        Disc(vh, c, radius * .86f, Tint(discColor));
        Color gold = Tint(trophyColor);
        var cup = new Vector2[Cup.Length];
        for (int i = 0; i < Cup.Length; i++) cup[i] = c + Cup[i] * radius;
        Fan(vh, c + new Vector2(0f, .16f) * radius, cup, gold);
        // 取っ手(杯の縁の外側の弧)。
        Arc(vh, c + new Vector2(-.36f, .25f) * radius, .14f * radius, .05f * radius, 90f, 260f, gold);
        Arc(vh, c + new Vector2(.36f, .25f) * radius, .14f * radius, .05f * radius, -80f, 90f, gold);
        // 脚と台。
        Box(vh, c, radius, -.055f, -.30f, .055f, -.10f, gold);
        Box(vh, c, radius, -.17f, -.35f, .17f, -.29f, gold);
        Box(vh, c, radius, -.27f, -.46f, .27f, -.35f, gold);
    }

    void Ring(VertexHelper vh, Vector2 center, float radius)
    {
        int start = vh.currentVertCount;
        for (int i = 0; i <= Segments; i++)
        {
            float along = i / (float)Segments, a = along * Mathf.PI * 2f;
            var direction = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Color part = tier == AchievementTier.Rainbow
                ? AchievementCardFrame.EdgeColor(tier, along, age, reducedEffects) : ringColor;
            vh.AddVert(center + direction * radius, Tint(part), Vector2.zero);
            vh.AddVert(center + direction * radius * .86f, Tint(part), Vector2.zero);
        }
        for (int i = 0; i < Segments; i++)
        {
            int k = start + i * 2;
            vh.AddTriangle(k, k + 2, k + 3);
            vh.AddTriangle(k, k + 3, k + 1);
        }
    }

    Color Tint(Color part) => new Color(part.r * color.r, part.g * color.g, part.b * color.b, part.a * color.a);

    static void Disc(VertexHelper vh, Vector2 center, float radius, Color color)
    {
        int start = vh.currentVertCount;
        vh.AddVert(center, color, Vector2.zero);
        for (int i = 0; i <= Segments; i++)
        {
            float a = i * Mathf.PI * 2f / Segments;
            vh.AddVert(center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, color, Vector2.zero);
        }
        for (int i = 0; i < Segments; i++) vh.AddTriangle(start, start + 1 + i, start + 2 + i);
    }

    static void Fan(VertexHelper vh, Vector2 center, Vector2[] points, Color color)
    {
        int start = vh.currentVertCount;
        vh.AddVert(center, color, Vector2.zero);
        foreach (var p in points) vh.AddVert(p, color, Vector2.zero);
        for (int i = 0; i < points.Length; i++) vh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % points.Length);
    }

    static void Arc(VertexHelper vh, Vector2 center, float radius, float width, float fromDegrees, float toDegrees, Color color)
    {
        const int steps = 10;
        int start = vh.currentVertCount;
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Lerp(fromDegrees, toDegrees, i / (float)steps) * Mathf.Deg2Rad;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            vh.AddVert(center + d * (radius - width * .5f), color, Vector2.zero);
            vh.AddVert(center + d * (radius + width * .5f), color, Vector2.zero);
        }
        for (int i = 0; i < steps; i++)
        {
            int k = start + i * 2;
            vh.AddTriangle(k, k + 1, k + 3);
            vh.AddTriangle(k, k + 3, k + 2);
        }
    }

    static void Box(VertexHelper vh, Vector2 center, float radius, float x0, float y0, float x1, float y1, Color color)
    {
        int start = vh.currentVertCount;
        vh.AddVert(center + new Vector2(x0, y0) * radius, color, Vector2.zero);
        vh.AddVert(center + new Vector2(x0, y1) * radius, color, Vector2.zero);
        vh.AddVert(center + new Vector2(x1, y1) * radius, color, Vector2.zero);
        vh.AddVert(center + new Vector2(x1, y0) * radius, color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
