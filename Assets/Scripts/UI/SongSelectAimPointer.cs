using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 選曲だけを照準操作にする。本編・タイトル・判定調整のセーバーには触れない。
[DefaultExecutionOrder(100)]
public sealed class SongSelectAimPointer : MonoBehaviour
{
    readonly SongSelectAimTracker tracker = new SongSelectAimTracker();
    readonly List<RaycastResult> hits = new List<RaycastResult>();
    SongSelectController controller;
    SongSelectSlashNav navigation;
    RectTransform overlay, upDock, downDock;
    SongSelectAimGraphic reticle, impact;
    Target hovered;
    Vector2 smoothed, lastMouse;
    int sourceKind;
    bool hasPoint, mouseNeedsMove;
    float shotAge = 10;
    public float Progress01 => tracker.Progress01;
    public bool NeedsRelease => tracker.NeedsRelease;
    public int ShotCount { get; private set; }
    public RectTransform Reticle => reticle.rectTransform;

    struct Target
    {
        public Object key;
        public MenuNoteAction action;
        public bool up;
        public Rect area;
    }

    public static SongSelectAimPointer Build(SongSelectController ctl, Canvas canvas, SongSelectSlashNav nav)
    {
        var aim = new GameObject("SongSelectAimPointer").AddComponent<SongSelectAimPointer>();
        aim.controller = ctl; aim.navigation = nav;
        aim.upDock = canvas.transform.Find("NavUpDock") as RectTransform;
        aim.downDock = canvas.transform.Find("NavDownDock") as RectTransform;
        foreach (var dock in new[] { aim.upDock, aim.downDock })
            if (dock != null) dock.GetComponent<Graphic>().raycastTarget = true;
        InputPoint.EnsureInstance();
        aim.BuildGraphics();
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
        int kind = input != null && input.IsRecentlyActive(.2f) ? 1 : mouse != null ? 2 : 0;
        if (sourceKind != kind)
        {
            CancelCharge(); hasPoint = false;
            if (sourceKind == 1 && kind != 1) { mouseNeedsMove = true; lastMouse = mousePoint; }
            if (kind == 1) mouseNeedsMove = false;
            sourceKind = kind;
        }
        if (mouseNeedsMove && Vector2.Distance(lastMouse, mousePoint) > 3) mouseNeedsMove = false;
        Vector2 point = kind == 1 ? Vector2.Scale(input.NormalizedPosition, new Vector2(Screen.width, Screen.height)) : mousePoint;
        bool valid = focused && kind != 0 && !mouseNeedsMove && new Rect(0, 0, Screen.width, Screen.height).Contains(point);
        if (!hasPoint) smoothed = point;
        else if (kind == 1) smoothed = Vector2.Lerp(smoothed, point, 1 - Mathf.Exp(-Time.unscaledDeltaTime / .045f));
        else smoothed = point;
        hasPoint = valid;
        // 通常クリックをした直後に、そのまま照準が溜まって同じ操作を再実行しない。
        if (valid && kind == 2 && mouse.leftButton.wasPressedThisFrame)
        {
            Target clicked = Resolve(point);
            if (clicked.key != null) tracker.BlockUntilExit(Expand(clicked.area, Padding));
        }
        TickAt(smoothed, Time.unscaledDeltaTime, valid);
    }

    float Padding => 8 * (overlay != null ? overlay.GetComponent<Canvas>().scaleFactor : 1);

    // 実入力と検証・撮影で共通の入口。引数は画面ピクセル。
    public void TickAt(Vector2 point, float dt, bool inputAvailable = true)
    {
        shotAge += Mathf.Max(0, dt);
        if (impact != null) impact.Show(0, false, shotAge);
        bool active = inputAvailable && !ScreenTransition.IsBusy;
        if (!active)
        {
            CancelCharge();
            if (reticle != null) reticle.gameObject.SetActive(false);
            return;
        }
        Target next = Resolve(point);
        // 対象の端での小さな震えは許容。他のボタンへ移ったときは必ずため直す。
        if (next.key == null && Available(hovered) && Expand(ScreenArea(hovered), Padding).Contains(point))
            next = hovered;
        if (next.key != hovered.key) SetHovered(next);
        else hovered = next;
        Rect area = next.key != null ? ScreenArea(next) : default;
        bool ready = Available(next) && SongSelectNoteMenu.Instance != null && SongSelectNoteMenu.Instance.IsReady;
        bool fire = tracker.Tick(next.key, Expand(area, Padding), point, dt, ready);
        if (reticle != null)
        {
            reticle.gameObject.SetActive(true); Place(reticle.rectTransform, point);
            reticle.Show(tracker.Progress01, tracker.NeedsRelease);
        }
        if (next.action != null) next.action.SetAimProgress(tracker.Progress01);
        if (fire)
        {
            CuttableNote note = next.action != null ? next.action.Note : next.up ? navigation.UpNote : navigation.DownNote;
            Vector2 hit = note != null ? (Vector2)Camera.main.WorldToScreenPoint(note.transform.position) : point;
            bool accepted = next.action != null ? next.action.TryShoot() : navigation.TryShoot(next.up);
            if (accepted)
            {
                ShotCount++; shotAge = 0;
                Place(impact.rectTransform, hit); impact.Show(0, false, 0);
            }
        }
    }

    Target Resolve(Vector2 point)
    {
        var events = EventSystem.current;
        if (events == null) return default;
        hits.Clear(); events.RaycastAll(new PointerEventData(events) { position = point }, hits);
        foreach (var hit in hits)
        {
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

    bool Available(Target target) => target.key != null && (target.action != null ? target.action.CanShoot : navigation != null && navigation.CanShoot(target.up));
    Rect ScreenArea(Target target) => target.action != null ? target.action.ScreenRect() : RectOnScreen(target.up ? upDock : downDock);
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
        if (hovered.action != null)
        {
            hovered.action.SetAimProgress(0);
            if (es != null) ExecuteEvents.Execute(hovered.action.gameObject, new PointerEventData(es), ExecuteEvents.pointerExitHandler);
        }
        hovered = next;
        if (next.action != null && es != null) ExecuteEvents.Execute(next.action.gameObject, new PointerEventData(es), ExecuteEvents.pointerEnterHandler);
    }
    void SelectionChanged(int index)
    {
        CancelCharge();
        // 発射演出中にクリックやキーで選び直した場合、古い遅延操作で上書きしない。
        foreach (var action in Object.FindObjectsByType<MenuNoteAction>(FindObjectsSortMode.None)) action.CancelPendingShot();
    }
    void CancelCharge() { tracker.Cancel(); SetHovered(default); }
    void OnDisable() { CancelCharge(); if (reticle != null) reticle.gameObject.SetActive(false); }
    void OnDestroy()
    {
        if (controller != null) { controller.OnSelectionChanged -= SelectionChanged; controller.OnDifficultyChanged -= SelectionChanged; }
    }
}
