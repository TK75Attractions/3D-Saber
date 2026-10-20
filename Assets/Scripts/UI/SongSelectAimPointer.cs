using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 選曲だけを照準操作にする。本編・タイトル・判定調整のセーバーには触れない。
[DefaultExecutionOrder(100)]
public sealed class SongSelectAimPointer : MonoBehaviour
{
    readonly PhoneSaberPointJumpGate pointJumpGate = new PhoneSaberPointJumpGate();
    readonly SongSelectAimTracker tracker = new SongSelectAimTracker();
    readonly List<RaycastResult> hits = new List<RaycastResult>();
    SongSelectController controller;
    SongSelectSlashNav navigation;
    SongSelectIdleGuide idleGuide;
    RectTransform overlay, upDock, downDock;
    SongSelectAimGraphic reticle, impact;
    Target hovered;
    Vector2 smoothed, lastMouse;
    int sourceKind;
    bool hasPoint, mouseNeedsMove;
    float shotAge = 10;
    public const float ArrivalGraceSeconds = SaberUIArrivalGuard.GraceSeconds;
    // 赤の認識が途切れても、この時間まではマウスへ切り替えない(SaberInputBridge・MenuSaberStatus と同じ1秒)。
    // 2026-10-21 ユーザー「カーソルあってるのにカウント始まんない」: 切り替えを入場扱いにしていたので、構えたまま見失って
    // 戻ると照準の下の的がロックされ、一度外すまで溜まらなかった(iPhone は見失うと3コマで送信を止める)。
    // 今は切り替えてもためを取り消すだけで、ロックはしない。入場のロックは画面に入ったときだけ(BeginArrival)。
    public const float SaberActiveSeconds = 1f;
    // ためを進めるのは新しい座標が届いている間だけ。止まった座標では進めず、外れた扱いにもしない(切り替えまで止めて保つ)。
    public const float SaberFreshSeconds = .2f;
    // セーバー照準だけをならす時定数。マウスはそのまま使う。
    public const float SaberSmoothingSeconds = .08f;
    // 照準で真ん中を切った直後(RUSH E の隠し切断)は、スタートだけこの時間ためない。
    // 入場扱いにはしないので、照準は灰色にならず、ほかの的のためもそのまま進む。
    public const float StartHoldOffSeconds = 1f;
    readonly SaberUIArrivalGuard arrival = new SaberUIArrivalGuard();
    Object guideTarget; float guideHeld, guideAway, startHoldOff;
    // ためる音(派手さカタログ 盤2)。かざし始めの0.15秒は鳴らさず、外れたら0.15秒で下げながら消す。
    // 猶予(0.25秒)の間に同じ的へ戻れば、ためた秒の位置から続きを鳴らす。音は MenuSfx が作って持つ。
    public const float ChargeLeadInSeconds = .15f;
    public const float ChargeFadeSeconds = .15f;
    AudioSource chargeSource; float chargeFade = -1;
    public bool ChargeSounding => chargeSource != null && chargeSource.isPlaying && chargeFade < 0;
    public void BeginArrival() { arrival.Reset(); SwitchSource(); }
    // 入力元の切り替え(赤を見失った・戻った、マウスを動かした)。ためは取り消すが、入場扱いにはしない。
    void SwitchSource() { guideTarget = null; guideHeld = guideAway = 0; CancelCharge(); }
    public void HoldOffStart() { startHoldOff = StartHoldOffSeconds; }
    public float Progress01 => tracker.Progress01;
    public bool NeedsRelease => tracker.NeedsRelease;
    // ロックされたスタートの盤に照準が乗ったままか。スキンが「いちど はずしてから かざしてね」を出す。
    public bool WaitingOnStart { get; private set; }
    public int ShotCount { get; private set; }
    public RectTransform Reticle => reticle.rectTransform;

    struct Target
    {
        public Object key;
        public MenuNoteAction action;
        public SongSelectDiscTarget disc;
        public bool up;
        public Rect area;
    }

    public static SongSelectAimPointer Build(SongSelectController ctl, Canvas canvas, SongSelectSlashNav nav, SongSelectIdleGuide guide = null)
    {
        var aim = new GameObject("SongSelectAimPointer").AddComponent<SongSelectAimPointer>();
        aim.controller = ctl; aim.navigation = nav;
        aim.idleGuide = guide;
        aim.upDock = canvas.transform.Find("NavUpDock") as RectTransform;
        aim.downDock = canvas.transform.Find("NavDownDock") as RectTransform;
        foreach (var dock in new[] { aim.upDock, aim.downDock })
            if (dock != null) dock.GetComponent<Graphic>().raycastTarget = true;
        InputPoint.EnsureInstance();
        aim.BuildGraphics();
        aim.BeginArrival();
        aim.chargeSource = aim.gameObject.AddComponent<AudioSource>();
        aim.chargeSource.playOnAwake = false; aim.chargeSource.spatialBlend = 0;
        ctl.OnSelectionChanged += aim.SelectionChanged;
        ctl.OnDifficultyChanged += aim.SelectionChanged;
        return aim;
    }

    void BuildGraphics()
    {
        var go = new GameObject("SongSelectAimCanvas", typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(transform, false);
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 900;
        var scaler = go.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        overlay = (RectTransform)go.transform;
        impact = SongSelectVisuals.Rect(overlay, "ShotImpact", Vector2.zero, new Vector2(160, 160)).gameObject.AddComponent<SongSelectAimGraphic>();
        impact.raycastTarget = false; impact.ImpactOnly = true;
        float reticleBox = (SongSelectAimGraphic.ReticleRadius + SongSelectAimGraphic.RingWidth) * 2f + 12f;
        reticle = SongSelectVisuals.Rect(overlay, "AimReticle", Vector2.zero, new Vector2(reticleBox, reticleBox)).gameObject.AddComponent<SongSelectAimGraphic>();
        reticle.raycastTarget = false;
        reticle.gameObject.SetActive(false);
    }

    void Update()
    {
        bool focused = Application.isFocused || Application.isBatchMode;
        var input = InputPoint.Instance;
        var mouse = Mouse.current;
        Vector2 mousePoint = mouse != null ? mouse.position.ReadValue() : lastMouse;
        int kind = input != null && input.IsRecentlyActive(SaberActiveSeconds) ? 1 : mouse != null ? 2 : 0;
        if (sourceKind != kind)
        {
            SwitchSource(); hasPoint = false;
            if (sourceKind == 1 && kind != 1) { mouseNeedsMove = true; lastMouse = mousePoint; }
            if (kind == 1) mouseNeedsMove = false;
            sourceKind = kind;
        }
        if (mouseNeedsMove && Vector2.Distance(lastMouse, mousePoint) > 3) mouseNeedsMove = false;
        Vector2 point = kind == 1 ? Vector2.Scale(input.NormalizedPosition, new Vector2(Screen.width, Screen.height)) : mousePoint;
        // 誤認識（会場の赤い物など）で照準が一瞬遠くへ飛ぶのを、最大2受信だけ無視する（PhoneSaberPointJumpGate）。
        if (kind == 1) point = pointJumpGate.Apply(input.LastReceivedMonotonicTime, point, Screen.height * .25f);
        else pointJumpGate.Reset();
        bool valid = focused && kind != 0 && !mouseNeedsMove && new Rect(0, 0, Screen.width, Screen.height).Contains(point);
        if (!hasPoint) smoothed = point;
        else if (kind == 1) smoothed = Vector2.Lerp(smoothed, point, 1 - Mathf.Exp(-Time.unscaledDeltaTime / SaberSmoothingSeconds));
        else smoothed = point;
        hasPoint = valid;
        // 通常クリックをした直後に、そのまま照準が溜まって同じ操作を再実行しない。
        if (valid && kind == 2 && mouse.leftButton.wasPressedThisFrame)
        {
            Target clicked = Resolve(point);
            if (clicked.key != null) tracker.BlockUntilExit(Expand(clicked.area, Padding), clicked.disc != null && clicked.disc.Circle);
        }
        TickAt(smoothed, Time.unscaledDeltaTime, valid, kind != 1 || input.IsRecentlyActive(SaberFreshSeconds));
    }

    float Padding => 8 * (overlay != null ? overlay.GetComponent<Canvas>().scaleFactor : 1);

    // 実入力と検証・撮影で共通の入口。引数は画面ピクセル。
    // fresh=false は座標が一時的に届いていない状態。ためも外れた猶予も止めたまま保ち、照準は最後の位置に薄く残す。
    // 同じ的へ戻れば続きから溜まる。1秒(SaberActiveSeconds)を超えて途切れたら、Update が入力元の切り替えとして取り消す。
    public void TickAt(Vector2 point, float dt, bool inputAvailable = true, bool fresh = true)
    {
        shotAge += Mathf.Max(0, dt);
        if (dt > 0) startHoldOff = Mathf.Max(0, startHoldOff - dt);
        if (impact != null) impact.Show(0, false, shotAge);
        if (ScreenTransition.IsBusy) BeginArrival();
        bool active = inputAvailable && !ScreenTransition.IsBusy;
        WaitingOnStart = false;
        if (!active)
        {
            CancelCharge(); guideTarget = null; guideHeld = 0;
            if (reticle != null) reticle.gameObject.SetActive(false);
            UpdateChargeSound(false, default, dt);
            return;
        }
        if (!fresh)
        {
            if (reticle != null)
            {
                reticle.gameObject.SetActive(true); Place(reticle.rectTransform, point);
                reticle.Show(tracker.Progress01, tracker.NeedsRelease, 10, true);
            }
            // 曲送りをためたまま止まっている間も、試聴を始めない。
            if (hovered.disc != null && hovered.disc.RepeatWhileHeld && tracker.Progress01 > 0 && controller != null && controller.ChartPreview != null) controller.ChartPreview.HoldOff();
            UpdateChargeSound(false, default, dt);
            return;
        }
        Target next = Resolve(point);
        // 対象の端での小さな震えは許容。他のボタンへ移ったときは必ずため直す。
        if (next.key == null && Available(hovered) && (hovered.disc != null ? hovered.disc.ContainsPoint(point, Padding) : SongSelectAimTracker.Contains(Expand(ScreenArea(hovered), Padding), point, false)))
            next = hovered;
        // 揺れで一瞬外れた間は、同じ的のためと表示を残す(ためは進めない)。取り消されたら外す。
        bool coasting = next.key == null && Available(hovered) && tracker.IsCharging(hovered.key);
        if (!coasting)
        {
            if (next.key != hovered.key) SetHovered(next);
            else hovered = next;
        }
        Rect area = next.key != null ? ScreenArea(next) : default;
        // 滑っている盤はため始めない。連続送りの次の1秒も、盤が止まってから数える。
        bool ready = Available(next) && !(next.disc != null && next.disc.Sliding)
            && (next.disc != null || SongSelectNoteMenu.Instance != null && SongSelectNoteMenu.Instance.IsReady);
        bool arriving = arrival.Tick(dt);
        if (arrival.CaptureTarget && next.key != null)
            tracker.BlockUntilExit(Expand(area, Padding), next.disc != null && next.disc.Circle);
        ready &= !arriving;
        bool startDisc = next.disc != null && controller != null && next.disc.Button == controller.startButton;
        // 案内を閉じるための狙いと、スタートの2秒は分けて数える。
        bool guideBlocksStart = idleGuide != null && idleGuide.IsVisible && startDisc;
        bool validDt = !(dt < 0 || dt > .2f || float.IsNaN(dt));
        // 案内を閉じるための狙いも、短く外れただけなら数え直さない(外れていた時間は数えない)。
        bool guideCoasting = next.key == null && guideTarget != null && guideHeld > 0 && validDt && !tracker.NeedsRelease
            && (guideAway += dt) + .00001f < SongSelectAimTracker.LeaveGraceSeconds;
        if (!guideCoasting)
        {
            guideAway = 0;
            if (!ReferenceEquals(guideTarget, next.key) || !ready || tracker.NeedsRelease || !validDt) guideHeld = 0;
            guideTarget = next.key;
        }
        if (guideBlocksStart && ready && !tracker.NeedsRelease)
        {
            guideHeld += dt;
            if (guideHeld >= SongSelectIdleState.AimActivitySeconds) idleGuide.RegisterActivity();
        }
        ready &= !guideBlocksStart;
        // 切った直後はスタートのためだけを取り消して止める(ロックしない)。
        ready &= !(startDisc && startHoldOff > 0);
        bool repeat = next.disc != null && next.disc.RepeatWhileHeld;
        bool fire = tracker.Tick(next.key, Expand(area, Padding), point, dt, ready,
            next.disc != null ? next.disc.HoldSeconds : 1, next.disc != null && next.disc.Circle, repeat);
        if (coasting && !tracker.IsCharging(hovered.key)) SetHovered(default);
        WaitingOnStart = !fire && startDisc && tracker.WaitingInside;
        // 手ぶれ・持ち直し・的の横切りでは案内を止めず、同じ的を狙い続けたときに操作とみなす。
        if (idleGuide != null && (fire || tracker.HeldSeconds >= SongSelectIdleState.AimActivitySeconds)) idleGuide.RegisterActivity();
        // 曲送りをため続けている間は試聴を始めない。送るたびに鳴りかけて途切れるのを防ぐ。
        if (repeat && tracker.Progress01 > 0 && controller != null && controller.ChartPreview != null) controller.ChartPreview.HoldOff();
        if (reticle != null)
        {
            reticle.gameObject.SetActive(true); Place(reticle.rectTransform, point);
            reticle.Show(tracker.Progress01, tracker.NeedsRelease);
        }
        if (next.action != null) next.action.SetAimProgress(tracker.Progress01);
        if (next.disc != null) next.disc.SetAimProgress(tracker.Progress01);
        UpdateChargeSound(!fire && next.key != null && tracker.IsCharging(next.key) && tracker.HeldSeconds >= ChargeLeadInSeconds, next, dt);
        if (fire)
        {
            StopChargeSound();
            CuttableNote note = next.action != null ? next.action.Note : navigation != null ? next.up ? navigation.UpNote : navigation.DownNote : null;
            Vector2 hit = note != null ? (Vector2)Camera.main.WorldToScreenPoint(note.transform.position) : point;
            bool accepted = next.disc != null ? next.disc.TryShoot() : next.action != null ? next.action.TryShoot() : navigation != null && navigation.TryShoot(next.up);
            if (accepted)
            {
                ShotCount++; shotAge = 0;
                // ためきった音(盤2)。的ごとの音(盤1)は操作の側で鳴る。スタートは StartGame が専用の決めを鳴らす。
                if (!startDisc) MenuSfx.Fire();
                Place(impact.rectTransform, hit); impact.Show(0, false, 0);
            }
        }
    }

    // ためている的に合わせた音を、ためた秒の位置から鳴らす。止まったら下げながら消す。
    void UpdateChargeSound(bool charging, Target target, float dt)
    {
        if (chargeSource == null) return;
        if (charging)
        {
            bool start = target.disc != null && controller != null && target.disc.Button == controller.startButton;
            var clip = MenuSfx.ChargeClip(target.disc != null ? target.disc.HoldSeconds : 1, start);
            if (clip == null) return;
            float at = Mathf.Clamp(tracker.HeldSeconds, 0, Mathf.Max(0, clip.length - .02f));
            if (chargeSource.clip != clip || !chargeSource.isPlaying || chargeFade >= 0 || Mathf.Abs(chargeSource.time - at) > .1f)
            {
                chargeSource.clip = clip; chargeSource.volume = 1; chargeSource.pitch = 1;
                chargeSource.time = at; chargeSource.Play();
            }
            chargeFade = -1;
            return;
        }
        if (!chargeSource.isPlaying) { chargeFade = -1; return; }
        if (chargeFade < 0) chargeFade = 0;
        chargeFade += Mathf.Max(0, dt);
        float k = 1 - Mathf.Clamp01(chargeFade / ChargeFadeSeconds);
        chargeSource.volume = k; chargeSource.pitch = Mathf.Lerp(.75f, 1f, k);
        if (k <= 0) StopChargeSound();
    }
    void StopChargeSound()
    {
        if (chargeSource != null) { chargeSource.Stop(); chargeSource.volume = 1; chargeSource.pitch = 1; }
        chargeFade = -1;
    }

    Target Resolve(Vector2 point)
    {
        var events = EventSystem.current;
        if (events == null) return default;
        hits.Clear(); events.RaycastAll(new PointerEventData(events) { position = point }, hits);
        foreach (var hit in hits)
        {
            var disc = hit.gameObject.GetComponentInParent<SongSelectDiscTarget>();
            if (disc != null)
                return disc.Available ? new Target { key = disc, disc = disc, area = disc.ScreenRect() } : default;
            var action = hit.gameObject.GetComponentInParent<MenuNoteAction>();
            if (action != null)
                return action.IsAvailable ? new Target { key = action, action = action, area = action.ScreenRect() } : default;
            if (navigation != null && (hit.gameObject.transform == upDock || hit.gameObject.transform == downDock))
            {
                bool up = hit.gameObject.transform == upDock;
                return new Target { key = up ? upDock : downDock, up = up, area = RectOnScreen(up ? upDock : downDock) };
            }
            // 前景のUIで遮られた対象を撃たない。
            return default;
        }
        return default;
    }

    bool Available(Target target) => target.key != null && (target.disc != null ? target.disc.Available : target.action != null ? target.action.CanShoot : navigation != null && navigation.CanShoot(target.up));
    Rect ScreenArea(Target target) => target.disc != null ? target.disc.ScreenRect() : target.action != null ? target.action.ScreenRect() : RectOnScreen(target.up ? upDock : downDock);
    public static Rect RectOnScreen(RectTransform rect)
    {
        if (rect == null) return default;
        var canvas = rect.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.min));
        Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.max));
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }
    static Rect Expand(Rect r, float pad) => Rect.MinMaxRect(r.xMin - pad, r.yMin - pad, r.xMax + pad, r.yMax + pad);
    void Place(RectTransform rect, Vector2 point)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(overlay, point, null, out Vector2 local);
        rect.anchoredPosition = local;
    }
    void SetHovered(Target next)
    {
        var es = EventSystem.current;
        if (hovered.disc != null)
        {
            hovered.disc.SetAimProgress(0);
            hovered.disc.OnPointerExit(null);
        }
        if (hovered.action != null)
        {
            hovered.action.SetAimProgress(0);
            if (es != null) ExecuteEvents.Execute(hovered.action.gameObject, new PointerEventData(es), ExecuteEvents.pointerExitHandler);
        }
        hovered = next;
        if (next.disc != null) next.disc.OnPointerEnter(null);
        if (next.action != null && es != null) ExecuteEvents.Execute(next.action.gameObject, new PointerEventData(es), ExecuteEvents.pointerEnterHandler);
    }
    void SelectionChanged(int index)
    {
        CancelCharge();
        // 発射演出中にクリックやキーで選び直した場合、古い遅延操作で上書きしない。
        foreach (var action in Object.FindObjectsByType<MenuNoteAction>(FindObjectsSortMode.None)) action.CancelPendingShot();
    }
    void CancelCharge() { tracker.Cancel(); SetHovered(default); }
    void OnApplicationFocus(bool focused) { if (!focused) BeginArrival(); }
    void OnDisable() { BeginArrival(); StopChargeSound(); if (reticle != null) reticle.gameObject.SetActive(false); }
    void OnDestroy()
    {
        if (controller != null) { controller.OnSelectionChanged -= SelectionChanged; controller.OnDifficultyChanged -= SelectionChanged; }
    }
}
