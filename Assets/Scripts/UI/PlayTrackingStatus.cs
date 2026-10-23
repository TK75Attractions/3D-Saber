using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 曲の途中で剣を見失ったときの見張り(仕様面の点検 4、2026-10-21)。時刻を受け取って状態を返す純粋な判定。
// ・届いていた色(赤・青)の端点つきの位置が、すべて LostSeconds 届かないと「見失った」(スタッフ向けの1行を出す)。
// ・見失っていて、ノーツが来ている(画面にある・まもなく流れる)ときだけ曲を止める。休みの区間で剣を下ろしても止めない。
// ・届き直したら、使っていた色の剣を輪に0.3秒そろえてから 3・2・1 を数える。
//   5秒そろわなければ従来の安定受信による再開へ戻す。数える間に必要な色が途切れたら待ち直す。
// 一度も届いていない色は待たない(青を使わない台・マウスで試す台では止めない)。
public sealed class PlayTrackingWatch
{
    public const double LostSeconds = 3, StableSeconds = 1, MaxGapSeconds = .25, ResumeCountdownSeconds = 3;
    public const double OneColorResumeSeconds = 5;
    // 場面の始まりより、この秒数以上前の受信は「届いていた」に数えない(スマホを止めてからキーやマウスで試すときに止めない)。
    public const double CarryOverSeconds = 30;
    // 「ノーツが来ている」の先読み(まだ出ていないノーツが、この秒数以内に流れ始めるか)。
    public const double ComingWithinSeconds = 1;
    public enum Phase { Playing, Waiting, Countdown }
    public enum Request { None, Pause, Resume }

    struct Feed { public bool seen; public double last, streakStart; }
    readonly Feed[] feeds = new Feed[2];
    double countdownStart;
    int countdownMask;
    readonly PlayResumePose pose = new PlayResumePose();
    int SeenMask => (Seen(0) ? 1 : 0) | (Seen(1) ? 2 : 0);
    public int FreshMask(double now) =>
        (Seen(0) && now - feeds[0].last <= MaxGapSeconds ? 1 : 0) |
        (Seen(1) && now - feeds[1].last <= MaxGapSeconds ? 2 : 0);
    public int PoseMask => State == Phase.Waiting && pose.Active && !pose.TimedOut ? SeenMask : 0;
    public Phase State { get; private set; }
    public bool Paused => State != Phase.Playing;

    // 端点つきの新しい位置が届いたとき(0=赤、1=青)。同じ受信時刻をくり返し渡しても数えない。
    public void ObserveUsable(int color, double receivedAt)
    {
        if (color < 0 || color >= feeds.Length || double.IsNaN(receivedAt) || double.IsInfinity(receivedAt)) return;
        ref Feed feed = ref feeds[color];
        if (feed.seen && receivedAt <= feed.last) return;
        if (!feed.seen || receivedAt - feed.last > MaxGapSeconds) feed.streakStart = receivedAt;
        feed.seen = true; feed.last = receivedAt;
    }

    public bool AnySeen => feeds[0].seen || feeds[1].seen;
    public bool Seen(int color) => color >= 0 && color < feeds.Length && feeds[color].seen;
    // その色が届いていたのに、LostSeconds 以上届いていないか。
    public bool Lost(int color, double now) => Seen(color) && now - feeds[color].last >= LostSeconds;

    // 届いていた色がすべて LostSeconds 以上届いていないか。
    public bool AllLost(double now)
    {
        bool any = false;
        foreach (var feed in feeds)
        {
            if (!feed.seen) continue;
            any = true;
            if (now - feed.last < LostSeconds) return false;
        }
        return any;
    }

    // 届いていた色がすべて、間を空けずに StableSeconds 続けて届いているか。
    // 片方しか戻らないときは、その色が OneColorResumeSeconds 続けて届けばよい(両手を使う人が片方だけで再開しないように)。
    public bool Stable(double now)
    {
        bool any = false, all = true;
        foreach (var feed in feeds)
        {
            if (!feed.seen) continue;
            bool arriving = now - feed.last <= MaxGapSeconds;
            double streak = feed.last - feed.streakStart;
            if (arriving && streak >= OneColorResumeSeconds) return true;
            if (arriving && streak >= StableSeconds) any = true; else all = false;
        }
        return any && all;
    }

    // どれかの色が、いま届き始めている(まだ数え始めるほど続いていない)。「剣を確認中」の表示に使う。
    public bool Receiving(double now)
    {
        foreach (var feed in feeds)
            if (feed.seen && now - feed.last <= MaxGapSeconds) return true;
        return false;
    }

    public double CountdownRemaining(double now) =>
        State == Phase.Countdown ? System.Math.Max(0, ResumeCountdownSeconds - (now - countdownStart)) : 0;

    // 3・2・1 を数えている途中なら、今から数え直す(音の出力先が変わって曲を読み直したあと。2026-10-22)。
    public void RestartCountdown(double now)
    {
        pose.Reset();
        if (State == Phase.Countdown) countdownStart = now;
    }

    // 毎フレーム呼ぶ。notesComing=ノーツが画面にあるかまもなく流れる。止める/再開するフレームだけ要求を返す。
    public Request Step(double now, bool notesComing, int insideMask = 0)
    {
        switch (State)
        {
            case Phase.Playing:
                pose.Reset();
                if (notesComing && AllLost(now)) { State = Phase.Waiting; return Request.Pause; }
                return Request.None;
            case Phase.Waiting:
                int fresh = FreshMask(now);
                pose.Step(now, true, SeenMask, fresh, insideMask);
                if (pose.Held || (pose.TimedOut && Stable(now)))
                {
                    countdownMask = fresh;
                    if (!pose.Held)
                        for (int i = 0; i < feeds.Length; i++)
                            if ((fresh & (1 << i)) != 0 &&
                                feeds[i].last - feeds[i].streakStart >= OneColorResumeSeconds)
                            { countdownMask = 1 << i; break; }
                    State = Phase.Countdown; countdownStart = now;
                    pose.Reset();
                }
                return Request.None;
            default:
                if ((FreshMask(now) & countdownMask) != countdownMask)
                { State = Phase.Waiting; pose.Reset(); return Request.None; }
                if (now - countdownStart < ResumeCountdownSeconds) return Request.None;
                State = Phase.Playing;
                return Request.Resume;
        }
    }

    // ---- 「ノーツが来ている」の数え方(2026-10-22)。純関数 ----
    // 画面のノーツのうち、まだ切られていない・MISS でもないもの(ロングは切り終わるまで)に、その手のノーツがあるか。
    // MISS のノーツは後ろへ 1.5 秒流れて残るので数えない(休みの区間でまちがって止めない)。
    // hand=Any は色を問わない。赤・青を指定したときは、どちらの手でも切れる金ノーツを数えない。
    public static bool LiveNeeds(IReadOnlyList<CuttableNote> live, SaberHand hand)
    {
        if (live == null) return false;
        foreach (var note in live)
            if (note != null && !note.IsCut && !note.IsMissed && (hand == SaberHand.Any || note.RequiredHand == hand)) return true;
        return false;
    }

    // まだ出ていないノーツ(next から、時刻順)のうち、withinSeconds 以内に流れ始めるものに、その手のノーツがあるか。
    public static bool UpcomingNeeds(IList<NoteData> notes, int next, Func<NoteData, double> effectiveTime,
        double approachTime, double songTime, SaberHand hand, double withinSeconds = ComingWithinSeconds)
    {
        if (notes == null || effectiveTime == null) return false;
        for (int i = Math.Max(0, next); i < notes.Count; i++)
        {
            if (notes[i] == null) continue;
            if (effectiveTime(notes[i]) - approachTime - songTime > withinSeconds) break;
            if (hand == SaberHand.Any || SaberHandHelper.FromColor(notes[i].color) == hand) return true;
        }
        return false;
    }

    // 片方の色の警告(スタッフ向け)。その色のノーツが来ているのに、届いていた位置が LostSeconds 届かない。
    // 一度も届いていない色は、warnNeverSeen のとき(本番の Player)だけ、曲が鳴り始めて LostSeconds たってから知らせる。
    public bool ShouldWarn(int color, double now, bool needed, bool warnNeverSeen, double playingSince)
    {
        if (!needed || color < 0 || color >= feeds.Length) return false;
        if (Seen(color)) return Lost(color, now);
        return warnNeverSeen && !double.IsNaN(playingSince) && now - playingSince >= LostSeconds;
    }
}

// 本編と練習の受信表示・一時停止の表示(仕様面の点検 4)。表示だけを受け持ち、止める・再開するのは GamePlayManager。
// 受信表示は左上の列(本編は RANK の下)に小さく、片方の色の警告はそのすぐ下。一時停止とスタッフ向けの1行は上の中央。ノーツの近くには何も出さない。
// 2026-10-22: 止まっている間は画面を少し暗くし、剣が戻りかけたら「確認中」、3・2・1 は輪で進み具合を見せ、止まる・数える・再開に音を付けた。
public sealed class PlayTrackingStatus : MonoBehaviour
{
    public const string PausedText = "一時停止中 ― 剣をカメラに映してね";
    public const string CheckingText = "剣を確認中…そのまま映してね";
    public const string ReadyText = "同じ色の輪に、剣の真ん中を合わせてね";
    public const string ResumedText = "再開！";
    public const string StaffText = "スタッフへ：剣の位置が届きません。スマホとカメラを確認（Esc 2回で中断）";
    public static string ResumeText(double remaining) => "再開まで " + System.Math.Max(1, (int)System.Math.Ceiling(remaining));
    // 片方(または両方)の色の警告。赤=右手・青=左手(ノーツの色と同じ)。どちらも無ければ null。
    public static string WarningText(bool red, bool blue, bool neverSeen)
    {
        if (!red && !blue) return null;
        string colors = red && blue ? "赤と青" : red ? "赤" : "青";
        return "スタッフへ：" + colors + (neverSeen ? "の剣の位置をまだ受信していません" : "の剣の位置が届いていません");
    }
    public static string WarningText(SaberHand hand, bool neverSeen) => WarningText(hand != SaberHand.Left, hand == SaberHand.Left, neverSeen);
    // 止まっている間の暗さ(黒の不透明度)と、変わる速さ(秒)。止まった場面は見えるまま(構え直せるように)。
    public const float DimAlpha = .35f, DimFadeSeconds = .2f;
    public const float ResumedSeconds = .5f;
    // 音は曲の前のカウントイン(刻み 0.55×0.68=0.374、START 0.55×0.9=0.495)の約 50〜75%。
    public const float PauseCueVolume = .18f, PauseCuePitch = .65f, TickVolume = .28f, ResumeVolume = .30f;

    TextMeshProUGUI status, warning, headline, staff;
    RectTransform panel;
    Image dim;
    SongSelectDiscGraphic ring;
    AudioSource cueSource, lowSource;
    AudioClip tickClip, startClip;
    string shownStatus, shownHeadline, shownWarning;
    bool shownStaff, shownPanel, shownRing;
    float dimLevel;
    PlayRecoveryView recovery;
    public PlayRecoveryView Recovery => recovery;
    public void ConfigureRecovery(NoteSpawner notes, SaberCutJudge first, SaberCutJudge second) =>
        recovery.Configure(notes, first, second);
    public int RecoveryInsideMask() => recovery.InsideMask();
    public void ShowRecovery(bool visible, InputPoint input, double now, int poseMask) =>
        recovery.Show(visible, input, now, poseMask);

    public static PlayTrackingStatus Create(Transform parent, float statusTop)
    {
        var root = new GameObject("PlayTrackingStatus", typeof(Canvas), typeof(CanvasScaler));
        root.transform.SetParent(parent, false);
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 860;
        var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var view = root.AddComponent<PlayTrackingStatus>();
        // いちばん奥に、止まっている間だけの暗い幕(画面いっぱい・入力は受けない)。
        var dimGo = new GameObject("PauseDim", typeof(RectTransform), typeof(Image));
        dimGo.transform.SetParent(root.transform, false);
        view.dim = dimGo.GetComponent<Image>();
        view.dim.color = new Color(0, 0, 0, 0); view.dim.raycastTarget = false;
        var dimRt = view.dim.rectTransform; dimRt.anchorMin = Vector2.zero; dimRt.anchorMax = Vector2.one; dimRt.offsetMin = dimRt.offsetMax = Vector2.zero;
        dimGo.SetActive(false);
        var jp = UISkinKit.JapaneseFallbackFontAsset();
        view.status = UISkinKit.MakeTMP(root.transform, "PositionStatus", "", 24, Color.white,
            TextAlignmentOptions.MidlineLeft, Vector2.zero, new Vector2(430, 32), FontStyles.Normal, 0, jp);
        var rt = view.status.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(70, -statusTop);
        view.status.raycastTarget = false;
        view.warning = UISkinKit.MakeTMP(root.transform, "ColorWarning", "", 26, SongSelectSkin.Gold,
            TextAlignmentOptions.MidlineLeft, Vector2.zero, new Vector2(820, 36), FontStyles.Normal, 0, jp);
        var wrt = view.warning.rectTransform;
        wrt.anchorMin = wrt.anchorMax = wrt.pivot = new Vector2(0, 1);
        wrt.anchoredPosition = new Vector2(70, -statusTop - 38);
        view.warning.raycastTarget = false;
        view.warning.gameObject.SetActive(false);
        var face = SongSelectVisuals.Panel(root.transform, "TrackingNotice", new Vector2(0, 330), new Vector2(1160, 136),
            new Color(.015f, .03f, .05f, .94f), SongSelectSkin.Gold, 10);
        face.raycastTarget = false;
        view.panel = face.rectTransform;
        view.headline = UISkinKit.MakeTMP(view.panel, "Headline", "", 44, Color.white, TextAlignmentOptions.Center,
            new Vector2(0, 22), new Vector2(1120, 60), FontStyles.Bold, 0, jp);
        view.staff = UISkinKit.MakeTMP(view.panel, "Staff", StaffText, 26, SongSelectSkin.Gold, TextAlignmentOptions.Center,
            new Vector2(0, -36), new Vector2(1120, 40), FontStyles.Normal, 0, jp);
        view.headline.raycastTarget = view.staff.raycastTarget = false;
        // 3・2・1 の進み具合の輪(見出しの左)。数えている間だけ出す。
        view.ring = SongSelectSkin.Graphic(view.panel, "ResumeRing", new Vector2(-470, 0), new Vector2(84, 84), SongSelectDiscGraphic.Shape.Ring, SongSelectSkin.Gold);
        view.ring.Width = 7; view.ring.Progress = 0; view.ring.gameObject.SetActive(false);
        view.recovery = new PlayRecoveryView((RectTransform)root.transform, view.panel);
        view.panel.gameObject.SetActive(false);
        if (Application.isPlaying)
        {
            view.cueSource = root.AddComponent<AudioSource>();
            view.lowSource = root.AddComponent<AudioSource>();
            foreach (var source in new[] { view.cueSource, view.lowSource }) { source.playOnAwake = false; source.spatialBlend = 0; }
            view.lowSource.pitch = PauseCuePitch;
        }
        return view;
    }

    // statusText=受信表示(空なら出さない)。pausedText=上の見出し(空なら出さない)。staffLine=スタッフ向けの1行を出すか。
    // warningText=片方の色の警告(左上、空なら出さない)。countdown=3・2・1 の進み具合 0〜1(負なら輪を出さない)。dimmed=画面を暗くするか。
    public void Show(string statusText, string pausedText, bool staffLine, string warningText = null, float countdown = -1, bool dimmed = false)
    {
        if (status != null && statusText != shownStatus) { shownStatus = statusText; status.text = statusText ?? ""; }
        string warn = warningText ?? "";
        if (warning != null && warn != shownWarning)
        {
            shownWarning = warn; warning.text = warn; warning.gameObject.SetActive(warn.Length > 0);
        }
        Fade(dimmed);
        bool showPanel = !string.IsNullOrEmpty(pausedText) || staffLine;
        if (panel != null && showPanel != shownPanel) { shownPanel = showPanel; panel.gameObject.SetActive(showPanel); }
        if (!showPanel) return;
        string head = pausedText ?? "";
        if (head != shownHeadline) { shownHeadline = head; headline.text = head; headline.gameObject.SetActive(head.Length > 0); }
        if (staffLine != shownStaff) { shownStaff = staffLine; staff.gameObject.SetActive(staffLine); }
        // 見出しが無いとき(休みの区間で見失った)は、スタッフ向けの1行だけを枠の中央に寄せる。
        staff.rectTransform.anchoredPosition = new Vector2(0, head.Length > 0 ? -36 : 0);
        bool showRing = countdown >= 0;
        if (ring != null)
        {
            if (showRing != shownRing) { shownRing = showRing; ring.gameObject.SetActive(showRing); }
            if (showRing) { ring.Progress = Mathf.Clamp01(countdown); ring.Refresh(); }
        }
    }

    void Fade(bool dimmed)
    {
        if (dim == null) return;
        float target = dimmed ? DimAlpha : 0;
        float step = Time.unscaledDeltaTime / DimFadeSeconds * DimAlpha;
        dimLevel = Mathf.MoveTowards(dimLevel, target, Mathf.Max(step, 0));
        if (!Application.isPlaying) dimLevel = target;
        bool visible = dimLevel > .001f;
        if (dim.gameObject.activeSelf != visible) dim.gameObject.SetActive(visible);
        if (visible) dim.color = new Color(0, 0, 0, dimLevel);
    }

    // 止まった瞬間: カウントの刻みを低く1回。
    public void PlayPauseCue() { if (Clip(ref tickClip, "TrackingPauseTick", () => ProceduralSfx.CountTick()) && lowSource != null) lowSource.PlayOneShot(tickClip, PauseCueVolume); CueCount++; }
    // 3・2・1 の数字が変わるたびに1回。
    public void PlayCountTick() { if (Clip(ref tickClip, "TrackingPauseTick", () => ProceduralSfx.CountTick()) && cueSource != null) cueSource.PlayOneShot(tickClip, TickVolume); CueCount++; }
    // 再開の瞬間: START の音を1回。
    public void PlayResumeCue() { if (Clip(ref startClip, "TrackingResumeStart", () => ProceduralSfx.CountStart()) && cueSource != null) cueSource.PlayOneShot(startClip, ResumeVolume); CueCount++; }

    // 鳴らすときに作る。音の出力先が変わって中身が消えていたら作り直す。
    static bool Clip(ref AudioClip clip, string name, Func<float[]> build)
    {
        if (!Application.isPlaying) return false;
        if (clip == null || clip.samples <= 0)
        {
            if (clip != null) Destroy(clip);
            clip = ProceduralSfx.Clip(name, build());
        }
        return clip != null;
    }

    void OnDestroy()
    {
        if (tickClip != null) Destroy(tickClip);
        if (startClip != null) Destroy(startClip);
    }

    public string StatusTextShown => shownStatus ?? "";
    public bool PanelShown => shownPanel;
    public string HeadlineShown => shownHeadline ?? "";
    public bool StaffShown => shownPanel && shownStaff;
    public string WarningShown => shownWarning ?? "";
    public bool RingShown => shownPanel && shownRing;
    public float DimLevel => dimLevel;
    // 鳴らした合図の数(テスト用)。
    public int CueCount { get; private set; }
}
