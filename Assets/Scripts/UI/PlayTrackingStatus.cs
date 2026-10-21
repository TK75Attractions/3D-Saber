using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 曲の途中で剣を見失ったときの見張り(仕様面の点検 4、2026-10-21)。時刻を受け取って状態を返す純粋な判定。
// ・届いていた色(赤・青)の端点つきの位置が、すべて LostSeconds 届かないと「見失った」(スタッフ向けの1行を出す)。
// ・見失っていて、ノーツが来ている(画面にある・まもなく流れる)ときだけ曲を止める。休みの区間で剣を下ろしても止めない。
// ・どれか1色が、間を MaxGapSeconds 以下で StableSeconds 続けて届いたら 3・2・1 を数え、続きから再開する。数える間にまた途切れたら待ちに戻る。
// 一度も届いていない色は待たない(青を使わない台・マウスで試す台では止めない)。
public sealed class PlayTrackingWatch
{
    public const double LostSeconds = 3, StableSeconds = 1, MaxGapSeconds = .25, ResumeCountdownSeconds = 3;
    // 場面の始まりより、この秒数以上前の受信は「届いていた」に数えない(スマホを止めてからキーやマウスで試すときに止めない)。
    public const double CarryOverSeconds = 30;
    public enum Phase { Playing, Waiting, Countdown }
    public enum Request { None, Pause, Resume }

    struct Feed { public bool seen; public double last, streakStart; }
    readonly Feed[] feeds = new Feed[2];
    double countdownStart;
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

    // どれか1色が、間を空けずに StableSeconds 続けて届いているか。
    public bool Stable(double now)
    {
        foreach (var feed in feeds)
            if (feed.seen && now - feed.last <= MaxGapSeconds && feed.last - feed.streakStart >= StableSeconds) return true;
        return false;
    }

    public double CountdownRemaining(double now) =>
        State == Phase.Countdown ? System.Math.Max(0, ResumeCountdownSeconds - (now - countdownStart)) : 0;

    // 毎フレーム呼ぶ。notesComing=ノーツが画面にあるかまもなく流れる。止める/再開するフレームだけ要求を返す。
    public Request Step(double now, bool notesComing)
    {
        switch (State)
        {
            case Phase.Playing:
                if (notesComing && AllLost(now)) { State = Phase.Waiting; return Request.Pause; }
                return Request.None;
            case Phase.Waiting:
                if (Stable(now)) { State = Phase.Countdown; countdownStart = now; }
                return Request.None;
            default:
                if (!Stable(now)) { State = Phase.Waiting; return Request.None; }
                if (now - countdownStart < ResumeCountdownSeconds) return Request.None;
                State = Phase.Playing;
                return Request.Resume;
        }
    }
}

// 本編と練習の受信表示・一時停止の表示(仕様面の点検 4)。表示だけを受け持ち、止める・再開するのは GamePlayManager。
// 受信表示は左上の列(本編は RANK の下)に小さく。一時停止とスタッフ向けの1行は上の中央。ノーツの近くには何も出さない。
public sealed class PlayTrackingStatus : MonoBehaviour
{
    public const string PausedText = "一時停止中 ― 剣をカメラに映してね";
    public const string StaffText = "スタッフへ：剣の位置が届きません。スマホとカメラを確認（Esc 2回で中断）";
    public static string ResumeText(double remaining) => "再開まで " + System.Math.Max(1, (int)System.Math.Ceiling(remaining));

    TextMeshProUGUI status, headline, staff;
    RectTransform panel;
    string shownStatus, shownHeadline;
    bool shownStaff, shownPanel;

    public static PlayTrackingStatus Create(Transform parent, float statusTop)
    {
        var root = new GameObject("PlayTrackingStatus", typeof(Canvas), typeof(CanvasScaler));
        root.transform.SetParent(parent, false);
        var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 860;
        var scaler = root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        var view = root.AddComponent<PlayTrackingStatus>();
        var jp = UISkinKit.JapaneseFallbackFontAsset();
        view.status = UISkinKit.MakeTMP(root.transform, "PositionStatus", "", 24, Color.white,
            TextAlignmentOptions.MidlineLeft, Vector2.zero, new Vector2(430, 32), FontStyles.Normal, 0, jp);
        var rt = view.status.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(70, -statusTop);
        view.status.raycastTarget = false;
        var face = SongSelectVisuals.Panel(root.transform, "TrackingNotice", new Vector2(0, 330), new Vector2(1160, 136),
            new Color(.015f, .03f, .05f, .94f), SongSelectSkin.Gold, 10);
        face.raycastTarget = false;
        view.panel = face.rectTransform;
        view.headline = UISkinKit.MakeTMP(view.panel, "Headline", "", 44, Color.white, TextAlignmentOptions.Center,
            new Vector2(0, 22), new Vector2(1120, 60), FontStyles.Bold, 0, jp);
        view.staff = UISkinKit.MakeTMP(view.panel, "Staff", StaffText, 26, SongSelectSkin.Gold, TextAlignmentOptions.Center,
            new Vector2(0, -36), new Vector2(1120, 40), FontStyles.Normal, 0, jp);
        view.headline.raycastTarget = view.staff.raycastTarget = false;
        view.panel.gameObject.SetActive(false);
        return view;
    }

    // statusText=受信表示(空なら出さない)。pausedText=一時停止の見出し(空なら一時停止していない)。staffLine=スタッフ向けの1行を出すか。
    public void Show(string statusText, string pausedText, bool staffLine)
    {
        if (status != null && statusText != shownStatus) { shownStatus = statusText; status.text = statusText ?? ""; }
        bool showPanel = !string.IsNullOrEmpty(pausedText) || staffLine;
        if (panel != null && showPanel != shownPanel) { shownPanel = showPanel; panel.gameObject.SetActive(showPanel); }
        if (!showPanel) return;
        string head = pausedText ?? "";
        if (head != shownHeadline) { shownHeadline = head; headline.text = head; headline.gameObject.SetActive(head.Length > 0); }
        if (staffLine != shownStaff) { shownStaff = staffLine; staff.gameObject.SetActive(staffLine); }
        // 見出しが無いとき(休みの区間で見失った)は、スタッフ向けの1行だけを枠の中央に寄せる。
        staff.rectTransform.anchoredPosition = new Vector2(0, head.Length > 0 ? -36 : 0);
    }

    public string StatusTextShown => shownStatus ?? "";
    public bool PanelShown => shownPanel;
    public string HeadlineShown => shownHeadline ?? "";
    public bool StaffShown => shownPanel && shownStaff;
}
