using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 実績解除の知らせ(2026-10-29 ユーザー「参加者が達成したときにxboxみたいな感じで上にポップアップ？が出てきて~をした ~人目ってのが
// わかるだけにして(5秒以内に消す)」)。画面の上のまん中に横長のカード: 丸いトロフィーの印・小さく「実績解除」・「〜した ○人目」(○人目は金)。
// 1枚は入り 0.3 秒・止まり 3.7 秒・消え 0.5 秒の 4.5 秒で消える。寿命は出し始めた実時刻から数える(処理落ちしても実時間で消える)。
// 一度に見えるのは上から2枚まで(0.12 秒ずらして入る)、残りは空いた段へ順番に。2枚で上から 150px までに収め、結果画面の RANK の見出し
// (上から約189px)にかからない(曲名は短い間なら隠れてよい)。いつ出し始めるかは呼ぶ側が決める(結果画面はランクの札の着地か、演出を飛ばしたとき)。
// 演奏中(Game シーン)と幕の間は新しく出さない。出したシーンを出たら、残りは出さない(次の人へ持ち越さない)。
// すべて raycastTarget=false(結果画面の操作を止めない)。並び順は照準(900)より上、印の一言の欄(920)・運営の知らせ(950)より下。
// 音は控えめな短い鈴(続けて出るときは1回だけ)。
public sealed class AchievementToast : MonoBehaviour
{
    public const float EnterSeconds = .3f, HoldSeconds = 3.7f, ExitSeconds = .5f;
    public const float LifeSeconds = EnterSeconds + HoldSeconds + ExitSeconds;
    public const int MaxVisible = 2;
    public const float StaggerSeconds = .12f;
    // 結果画面で出し始める、結果の演出の時刻(ランクの札の着地。演出を飛ばすと演出の時刻はこれを超える)。
    public const float ResultLandingSeconds = ResultSkin.RankLandingTime;
    public const int SortingOrder = 910;
    public const float CardWidth = 760f, CardHeight = 68f, TopMargin = 8f, Gap = 6f, IconSize = 50f;
    // 入るときは上から少し降り、消えるときは少し上がる(px)。
    public const float EnterDrop = 20f, ExitRise = 10f;
    public const string Caption = "実績解除";
    public const string NumberColor = "#FFD84D";
    public const string ChimeKey = "AchievementChime";
    public const double ChimePeakDb = -18;
    // 鈴を続けて鳴らさない間隔(1つの波に1回)。
    public const double ChimeGapSeconds = .4;
    const string GameScene = "Game";

    static readonly Color PanelFill = new Color(.025f, .035f, .085f, .94f);
    static readonly Color PanelEdge = new Color(1f, .85f, .3f, .7f);
    static readonly Color CaptionColor = new Color(1f, .86f, .46f, .95f);

    public static AchievementToast Instance { get; private set; }

    readonly AchievementToastQueue queue = new AchievementToastQueue(MaxVisible);
    readonly CardView[] views = new CardView[MaxVisible];
    double lastChime = double.NegativeInfinity;
    bool built;

    sealed class CardView
    {
        public RectTransform Root;
        public CanvasGroup Group;
        public TextMeshProUGUI Body;
        public string Shown;
    }

    public AchievementToastQueue Queue => queue;

    // 番号つきで出す(表示の順のまま、上から)。すぐに出し始める(演奏中・幕の間は待つ)。
    public static void Show(IEnumerable<AchievementUnlock> unlocks)
    {
        if (unlocks == null) return;
        AchievementToast host = null;
        foreach (var unlock in unlocks)
        {
            if (unlock.Definition == null) continue;
            if (host == null) host = Ensure();
            host.queue.Enqueue(Message(unlock.Text, unlock.Number));
        }
    }

    public static void Show(string text, int number) => Ensure().queue.Enqueue(Message(text, number));

    // 通知の文: 「〜した ○人目」(○人目は金の太字)。番号が無い(記録できなかった)ときは文だけ。
    public static string Message(string text, int number) =>
        number > 0 ? (text ?? "") + "  <color=" + NumberColor + "><b>" + number + "人目</b></color>" : text ?? "";

    // 新しいカードを出してよいか(演奏中と幕の間は出さない)。
    public static bool CanStart(string sceneName, bool transitionBusy) => sceneName != GameScene && !transitionBusy;

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
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        for (int slot = 0; slot < MaxVisible; slot++) views[slot] = BuildCard(transform, slot);
    }

    static CardView BuildCard(Transform parent, int slot)
    {
        var root = SongSelectVisuals.Rect(parent, "AchievementCard" + slot, SlotPosition(slot), new Vector2(CardWidth, CardHeight));
        root.anchorMin = root.anchorMax = new Vector2(.5f, 1f);
        root.pivot = new Vector2(.5f, 1f);
        root.anchoredPosition = SlotPosition(slot);
        var group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false;
        var panel = SongSelectVisuals.Panel(root, "Panel", Vector2.zero, new Vector2(CardWidth, CardHeight), PanelFill, PanelEdge, 12f);
        panel.borderWidth = 2f;
        float iconX = -CardWidth * .5f + 14f + IconSize * .5f;
        var icon = SongSelectVisuals.Rect(root, "Trophy", new Vector2(iconX, 0f), new Vector2(IconSize, IconSize))
            .gameObject.AddComponent<AchievementTrophyGraphic>();
        icon.raycastTarget = false;
        float left = iconX + IconSize * .5f + 14f, right = CardWidth * .5f - 18f;
        var font = UISkinKit.FontAsset("Oxanium-Bold");
        var caption = UISkinKit.MakeTMP(root, "Caption", Caption, 17f, CaptionColor, TextAlignmentOptions.MidlineLeft,
            new Vector2((left + right) * .5f, 16f), new Vector2(right - left, 22f), FontStyles.Bold, 2f, font);
        caption.raycastTarget = false;
        var body = UISkinKit.MakeTMP(root, "Body", "", 26f, UISkinPalette.OffWhite, TextAlignmentOptions.MidlineLeft,
            new Vector2((left + right) * .5f, -10f), new Vector2(right - left, 34f), FontStyles.Bold, 0f, font);
        body.enableAutoSizing = true; body.fontSizeMin = 18f; body.fontSizeMax = 26f;
        body.overflowMode = TextOverflowModes.Ellipsis;
        body.raycastTarget = false;
        root.gameObject.SetActive(false);
        return new CardView { Root = root, Group = group, Body = body };
    }

    // 上から slot 番目のカードの置き場(上のまん中を基準に、カードの上の辺)。
    public static Vector2 SlotPosition(int slot) => new Vector2(0f, -(TopMargin + slot * (CardHeight + Gap)));

    // 見えるカードが占める、画面の上からの高さ(1920×1080 基準)。
    public static float CoveredHeight => TopMargin + MaxVisible * CardHeight + (MaxVisible - 1) * Gap;

    public struct Pose { public float Alpha, Offset, Scale; }

    // 出てからの秒 age のカードの見え方。Offset は上へずらす量(px)。LifeSeconds で消えきる。
    public static Pose CardPose(double age)
    {
        if (age < 0 || age >= LifeSeconds) return new Pose { Alpha = 0f, Offset = 0f, Scale = 1f };
        if (age < EnterSeconds)
        {
            float k = (float)(age / EnterSeconds), e = 1f - (1f - k) * (1f - k) * (1f - k);
            return new Pose { Alpha = e, Offset = (1f - e) * EnterDrop, Scale = Mathf.Lerp(.96f, 1f, e) };
        }
        double leave = age - EnterSeconds - HoldSeconds;
        if (leave <= 0) return new Pose { Alpha = 1f, Offset = 0f, Scale = 1f };
        float t = Mathf.Clamp01((float)(leave / ExitSeconds));
        return new Pose { Alpha = 1f - t * t, Offset = t * ExitRise, Scale = 1f };
    }

    void Update()
    {
        Step(Time.realtimeSinceStartupAsDouble, CanStart(SceneManager.GetActiveScene().name, ScreenTransition.IsBusy));
    }

    // 実時刻 now で進める(テストからも呼ぶ)。何もかも消えたら自分も消える。
    public void Step(double now, bool canStart)
    {
        Build();
        int started = queue.Tick(now, canStart);
        if (started > 0 && now - lastChime >= ChimeGapSeconds)
        {
            lastChime = now;
            MenuSfx.Play(ChimeKey, ChimeSamples);
        }
        for (int slot = 0; slot < MaxVisible; slot++) Refresh(views[slot], queue.At(slot), slot, now);
        if (queue.Idle && Application.isPlaying) Destroy(gameObject);
    }

    static void Refresh(CardView view, AchievementToastQueue.Card card, int slot, double now)
    {
        if (view == null) return;
        if (card == null)
        {
            if (view.Root.gameObject.activeSelf) view.Root.gameObject.SetActive(false);
            view.Shown = null;
            return;
        }
        if (!view.Root.gameObject.activeSelf) view.Root.gameObject.SetActive(true);
        if (view.Shown != card.Message) { view.Shown = card.Message; view.Body.text = card.Message; }
        var pose = CardPose(now - card.StartedAt);
        view.Group.alpha = pose.Alpha;
        view.Root.anchoredPosition = SlotPosition(slot) + new Vector2(0f, pose.Offset);
        view.Root.localScale = new Vector3(pose.Scale, pose.Scale, 1f);
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

    // 控えめな短い鈴(D6 → G6 の2音。ピーク -18 dBFS)。結果画面の音楽の上で邪魔をしない大きさ。
    public static float[] ChimeSamples()
    {
        SfxLab.Reseed(29);
        return SfxLab.Finish(SfxLab.Mix(.6, new SfxLab.Layer(Bell(.45, SfxLab.Midi(86))), new SfxLab.Layer(Bell(.5, SfxLab.Midi(91)), .08, -2)),
            ChimePeakDb);
    }

    static float[] Bell(double seconds, double hz) =>
        SfxLab.Env(SfxLab.Additive(seconds, new[] { (hz, 1.0), (hz * 2.01, .22), (hz * 3.02, .06) }), SfxLab.AdExp(.004, .14));
}

// 実績の知らせの並び(画面の部品を持たない。テストから直接使う)。上から Capacity 枚まで出し、残りは空いた所へ順番に。
// 時刻はすべて実時刻(秒)。カードは出し始めた時刻から LifeSeconds で消える(処理落ちのコマがあっても実時間で消える)。
public sealed class AchievementToastQueue
{
    public sealed class Card
    {
        public string Message;
        public double StartedAt;
    }

    readonly Queue<string> waiting = new Queue<string>();
    readonly Card[] slots;
    double lastStart = double.NegativeInfinity;

    public AchievementToastQueue(int capacity) { slots = new Card[Math.Max(1, capacity)]; }

    public int Capacity => slots.Length;
    public int Waiting => waiting.Count;
    public int VisibleCount { get { int n = 0; foreach (var card in slots) if (card != null) n++; return n; } }
    public bool Idle => waiting.Count == 0 && VisibleCount == 0;
    public Card At(int slot) => slot >= 0 && slot < slots.Length ? slots[slot] : null;

    public void Enqueue(string message) => waiting.Enqueue(message ?? "");

    // 実時刻 now まで進める。消える時刻を過ぎたカードを外し、canStart なら空いた段へ次を出す(前に出してから StaggerSeconds あける)。
    // canStart が false の間(演奏中・幕の間)は新しく出さない(出ているカードは時刻どおり消えていく)。出した枚数を返す。
    public int Tick(double now, bool canStart)
    {
        if (double.IsNaN(now) || double.IsInfinity(now)) return 0;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] != null && now - slots[i].StartedAt >= AchievementToast.LifeSeconds) slots[i] = null;
        if (!canStart) return 0;
        int started = 0;
        while (waiting.Count > 0 && now - lastStart >= AchievementToast.StaggerSeconds - 1e-9)
        {
            int free = Array.IndexOf(slots, null);
            if (free < 0) break;
            slots[free] = new Card { Message = waiting.Dequeue(), StartedAt = now };
            lastStart = now;
            started++;
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

// 知らせのカードの左の丸い印(金の輪・暗い円・金のトロフィー)。画像を使わずに描く。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class AchievementTrophyGraphic : MaskableGraphic
{
    public Color ringColor = UISkinPalette.NoteGold;
    public Color discColor = new Color(.05f, .07f, .15f, 1f);
    public Color trophyColor = UISkinPalette.NoteGold;
    const int Segments = 48;

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
        Disc(vh, c, radius, Tint(ringColor));
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
