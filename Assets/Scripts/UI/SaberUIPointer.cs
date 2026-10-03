using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// セーバーでUIを操作する「セーバーポインタ」。
// セーバー(棒1)の位置に光るカーソルを表示し、UIのButtonの上に DwellSeconds かざすと
// クリックを発火する(進捗はカーソルの円が満ちていく表示)。
// UDP入力が来ているときだけ現れるので、マウス/キーボード操作とは完全に併存する。
// 3Dノーツ・カメラ切替を使わないため、曲選択のUIをそのまま操作できる(旧3Dメニュー方式の代替)。
public class SaberUIPointer : MonoBehaviour
{
    public const float DwellSeconds = 0.45f;
    public const float CooldownSeconds = 0.4f;
    public const float StaleSeconds = 0.6f;

    // 滞留クリックの純ロジック(テストから直接叩く)。
    // 同じターゲットに乗り続けると進捗が溜まり、満ちた瞬間に一度だけ true を返す。
    // ターゲットが変わる/外れると進捗リセット。発火後はクールダウン中、溜め直しを止める。
    public class DwellTracker
    {
        private readonly float defaultDwellSeconds;
        private readonly float cooldownSeconds;
        private object current;
        private float progressTime;
        private float cooldownLeft;
        private float activeDwellSeconds;

        public DwellTracker(float dwell, float cooldown)
        {
            defaultDwellSeconds = Mathf.Max(0.01f, dwell);
            activeDwellSeconds = defaultDwellSeconds;
            cooldownSeconds = Mathf.Max(0f, cooldown);
        }

        public float Progress01 => Mathf.Clamp01(progressTime / activeDwellSeconds);
        public bool InCooldown => cooldownLeft > 0f;

        public bool Tick(object target, float dt)
        {
            return Tick(target, dt, defaultDwellSeconds);
        }

        // 難易度ボタンだけ 1 秒にするなど、対象ごとの滞留時間を受け取る。
        public bool Tick(object target, float dt, float dwellOverrideSeconds)
        {
            float requestedDwell = Mathf.Max(0.01f, dwellOverrideSeconds);
            // クールダウンはフレーム頭で消化し、消化に使ったフレームでは溜めない
            bool wasCooling = cooldownLeft > 0f;
            if (wasCooling) cooldownLeft = Mathf.Max(0f, cooldownLeft - dt);

            if (!ReferenceEquals(target, current))
            {
                // 対象が変わったら溜め直し(乗った瞬間のフレームから溜め始める)
                current = target;
                progressTime = 0f;
                activeDwellSeconds = requestedDwell;
            }
            else activeDwellSeconds = requestedDwell;
            if (current == null)
            {
                progressTime = 0f;
                return false;
            }
            if (wasCooling) return false;

            progressTime += dt;
            if (progressTime >= activeDwellSeconds)
            {
                progressTime = 0f;
                cooldownLeft = cooldownSeconds;
                return true;
            }
            return false;
        }

        public void Reset()
        {
            current = null;
            progressTime = 0f;
            activeDwellSeconds = defaultDwellSeconds;
        }
    }

    private Camera cam;
    private Canvas overlayCanvas;
    private Image cursorDot;
    private Image progressRing;
    private SaberInputBridge bridge; // セーバー本体。ある場合はその実位置に点を重ねる
    private readonly DwellTracker tracker = new DwellTracker(DwellSeconds, CooldownSeconds);
    private Button hovered;
    private Vector3 smoothedPosition;
    private bool hasSmoothed;

    // 判定調整では選曲と同じ照準を使う。従来の結果画面のポインターは維持する。
    private SongSelectAimGraphic aimReticle;
    private readonly SongSelectAimTracker aimTracker = new SongSelectAimTracker();
    private Vector2 aimPosition, lastMouse;
    private int aimSource;
    private bool hasAimPosition, mouseNeedsMove;
    public float AimProgress01 => aimTracker.Progress01;
    public RectTransform AimReticle => aimReticle != null ? aimReticle.rectTransform : null;

    public Button HoveredForTest => hovered;
    // メニュー時に、判定面の入力範囲を画面全体の UI へ写す。
    // セーバー本体・判定用の座標は変えず、ライブ試し切り中は下端の操作帯だけを使う。
    public bool RemapToFullScreen { get; set; }
    // ライブ判定調整だけ、下端の操作帯を対象にする。ほかの画面は従来どおり。
    public bool BottomControlsOnly { get; set; }
    public RectTransform BottomControlArea { get; set; }
    public bool RespectRaycastBlockers { get; set; }
    // 選曲の立体ノーツは速度付き斬撃のみ。滞留クリックを重ねて発火しない。
    public bool SlashOnly { get; set; }
    public static bool IsInsideBottomControls(Vector2 position, float width, float height) =>
        width > 0 && height > 0 && position.x >= 0 && position.x <= width && position.y >= 0 && position.y <= height * .18f;

    // 曲選択などのシーンに設置する。UDP受信機も確保する(無ければ作る)。
    public static SaberUIPointer Build(bool useAimReticle = false)
    {
        InputPoint.EnsureInstance();
        var go = new GameObject("SaberUIPointer");
        var pointer = go.AddComponent<SaberUIPointer>();
        pointer.cam = Camera.main;
        if (useAimReticle) pointer.BuildAimCursor();
        else pointer.BuildCursor();
        return pointer;
    }

    private void BuildAimCursor()
    {
        var go = new GameObject("SaberAimCanvas", typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(transform, false);
        overlayCanvas = go.GetComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = 900;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        float size = (SongSelectAimGraphic.ReticleRadius + SongSelectAimGraphic.RingWidth) * 2 + 12;
        aimReticle = SongSelectVisuals.Rect(go.transform, "AimReticle", Vector2.zero, new Vector2(size, size))
            .gameObject.AddComponent<SongSelectAimGraphic>();
        aimReticle.raycastTarget = false;
        aimReticle.gameObject.SetActive(false);
    }

    private void UpdateAim()
    {
        var input = InputPoint.Instance;
        var mouse = Mouse.current;
        Vector2 mousePoint = mouse != null ? mouse.position.ReadValue() : lastMouse;
        int source = input != null && input.IsRecentlyActive(.2f) ? 1 : mouse != null ? 2 : 0;
        if (source != aimSource)
        {
            aimTracker.Cancel(); hasAimPosition = false;
            // 追跡が途切れても、古いマウス位置で勝手に操作を始めない。
            if (aimSource == 1 && source != 1) { mouseNeedsMove = true; lastMouse = mousePoint; }
            if (source == 1) mouseNeedsMove = false;
            aimSource = source;
        }
        if (mouseNeedsMove && Vector2.Distance(lastMouse, mousePoint) > 3) mouseNeedsMove = false;
        // 選曲と同じ入力座標。ゲーム内のセーバーの可動域や表示位置は変えない。
        Vector2 point = source == 1 ? Vector2.Scale(input.NormalizedPosition, new Vector2(Screen.width, Screen.height)) : mousePoint;
        bool valid = (Application.isFocused || Application.isBatchMode) && source != 0 && !mouseNeedsMove;
        if (!hasAimPosition || source != 1) aimPosition = point;
        else aimPosition = Vector2.Lerp(aimPosition, point, 1 - Mathf.Exp(-Time.unscaledDeltaTime / .045f));
        hasAimPosition = valid;
        TickAimAt(aimPosition, Time.unscaledDeltaTime, valid, source == 2 && mouse.leftButton.wasPressedThisFrame);
    }

    // 実入力と実シーン試験で共通の入口。照準は常に表示し、試し切り中は下端のボタンだけ受け付ける。
    public void TickAimAt(Vector2 point, float dt, bool inputAvailable = true, bool clicked = false)
    {
        if (aimReticle == null) return;
        if (!inputAvailable || ScreenTransition.IsBusy || !new Rect(0, 0, Screen.width, Screen.height).Contains(point))
        {
            aimTracker.Cancel(); SetHovered(null); aimReticle.gameObject.SetActive(false); return;
        }
        // 4:3などでも実際の操作帯に合わせる。画面高の固定割合ではボタンが範囲外になる。
        bool allowed = !BottomControlsOnly || (BottomControlArea != null
            ? SongSelectAimPointer.RectOnScreen(BottomControlArea).Contains(point)
            : IsInsideBottomControls(point, Screen.width, Screen.height));
        Button target = allowed ? RaycastButton(point) : null;
        SetHovered(target);
        Rect area = target != null ? SongSelectAimPointer.RectOnScreen((RectTransform)target.transform) : default;
        // 通常クリックでもホールドを解除するまで同じ操作を繰り返さない。
        if (clicked && target != null) aimTracker.BlockUntilExit(area);
        var dwell = target != null ? target.GetComponent<SaberDwellTarget>() : null;
        bool fire = aimTracker.Tick(target, area, point, dt, target != null && !SlashOnly,
            dwell != null ? dwell.dwellSeconds : SongSelectAimTracker.HoldSeconds);
        aimReticle.gameObject.SetActive(true);
        var canvasCamera = overlayCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : overlayCanvas.worldCamera;
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)overlayCanvas.transform, point, canvasCamera, out var local);
        aimReticle.rectTransform.anchoredPosition = local;
        aimReticle.Show(aimTracker.Progress01, aimTracker.NeedsRelease);
        if (fire) Click(target, point);
    }

    private void OnDisable()
    {
        if (aimReticle == null) return;
        aimTracker.Cancel(); SetHovered(null); hasAimPosition = false;
        aimReticle.gameObject.SetActive(false);
    }

    private void OnApplicationFocus(bool focused)
    {
        // 非アクティブ中にUpdateが止まる設定でも、復帰時へ進捗を持ち越さない。
        if (!focused) OnDisable();
    }

    private void BuildCursor()
    {
        // 最前面のオーバーレイCanvas(既存UIより上)にカーソルを描く
        var canvasGo = new GameObject("SaberPointerCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);
        overlayCanvas = canvasGo.GetComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = 900;

        // 進捗サークル(滞留でラジアルに満ちる)
        var ringGo = new GameObject("Progress", typeof(RectTransform), typeof(Image));
        ringGo.transform.SetParent(canvasGo.transform, false);
        progressRing = ringGo.GetComponent<Image>();
        progressRing.sprite = UISkinKit.SoftGlow();
        progressRing.type = Image.Type.Filled;
        progressRing.fillMethod = Image.FillMethod.Radial360;
        progressRing.fillOrigin = (int)Image.Origin360.Top;
        progressRing.fillClockwise = true;
        progressRing.fillAmount = 0f;
        progressRing.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.55f);
        progressRing.raycastTarget = false;
        progressRing.rectTransform.sizeDelta = new Vector2(56f, 56f);

        // カーソル本体(常時見える小さな光点)
        var dotGo = new GameObject("Cursor", typeof(RectTransform), typeof(Image));
        dotGo.transform.SetParent(canvasGo.transform, false);
        cursorDot = dotGo.GetComponent<Image>();
        cursorDot.sprite = UISkinKit.SoftGlow();
        cursorDot.color = new Color(UISkinPalette.Cyan.r, UISkinPalette.Cyan.g, UISkinPalette.Cyan.b, 0.95f);
        cursorDot.raycastTarget = false;
        cursorDot.rectTransform.sizeDelta = new Vector2(22f, 22f);

        SetCursorVisible(false);
    }

    private void SetCursorVisible(bool visible)
    {
        if (cursorDot != null && cursorDot.gameObject.activeSelf != visible)
        {
            cursorDot.gameObject.SetActive(visible);
        }
        if (progressRing != null && progressRing.gameObject.activeSelf != visible)
        {
            progressRing.gameObject.SetActive(visible);
        }
    }

    void Update()
    {
        if (aimReticle != null) { UpdateAim(); return; }
        var ip = InputPoint.Instance;
        bool active = !ScreenTransition.IsBusy && ip != null && ip.IsRecentlyActive(StaleSeconds) && cam != null;
        if (!active)
        {
            SetCursorVisible(false);
            SetHovered(null);
            tracker.Reset();
            hasSmoothed = false;
            return;
        }

        // セーバー位置→スクリーン座標。軽くスムージングして震えを抑える。
        // SaberInputBridge がいる画面では、その実位置(リマップ等を反映済み)を使い、
        // 青点がブレードから絶対にずれないようにする。無い画面のみ InputPoint 直読み。
        if (bridge == null) bridge = Object.FindFirstObjectByType<SaberInputBridge>();
        // 結果画面など3Dブレードの無いUIでは、カメラの画角に依存せず下端まで届かせる。
        bool normalizedUI = RemapToFullScreen && bridge == null;
        Vector3 position = normalizedUI ? (Vector3)ip.NormalizedPosition : bridge != null && !bridge.UsingMouseFallback
            ? bridge.transform.position
            : new Vector3(ip.LocalPosition.x, ip.LocalPosition.y, 0f);
        if (!hasSmoothed)
        {
            smoothedPosition = position;
            hasSmoothed = true;
        }
        else
        {
            float alpha = Time.unscaledDeltaTime / (0.05f + Time.unscaledDeltaTime);
            smoothedPosition = Vector3.Lerp(smoothedPosition, position, alpha);
        }
        Vector3 screen;
        if (normalizedUI)
        {
            screen = new Vector3(smoothedPosition.x * Screen.width, smoothedPosition.y * Screen.height, 0f);
        }
        else if (RemapToFullScreen && bridge != null)
        {
            screen = new Vector3(
                Mathf.InverseLerp(bridge.minBounds.x, bridge.maxBounds.x, smoothedPosition.x) * Screen.width,
                Mathf.InverseLerp(bridge.minBounds.y, bridge.maxBounds.y, smoothedPosition.y) * Screen.height, 0f);
        }
        else screen = cam.WorldToScreenPoint(smoothedPosition);

        if (BottomControlsOnly && !IsInsideBottomControls(screen, Screen.width, Screen.height))
        {
            SetCursorVisible(false); SetHovered(null); tracker.Reset(); return;
        }
        SetCursorVisible(true);
        cursorDot.rectTransform.position = new Vector3(screen.x, screen.y, 0f);
        progressRing.rectTransform.position = new Vector3(screen.x, screen.y, 0f);

        Button target = RaycastButton(screen);
        SetHovered(target);

        if (SlashOnly || (target != null && target.GetComponent<MenuNoteAction>() != null))
        {
            tracker.Reset();
            progressRing.fillAmount = 0f;
            return;
        }

        var dwellTarget = target != null ? target.GetComponent<SaberDwellTarget>() : null;
        float dwellSeconds = dwellTarget != null ? dwellTarget.dwellSeconds : DwellSeconds;
        Color progressColor = dwellTarget != null ? dwellTarget.progressColor : UISkinPalette.Cyan;
        progressRing.color = new Color(progressColor.r, progressColor.g, progressColor.b, 0.62f);

        bool fired = tracker.Tick(target, Time.unscaledDeltaTime, dwellSeconds);
        progressRing.fillAmount = tracker.Progress01;
        if (fired && target != null)
        {
            Click(target, screen);
        }
    }

    private void SetHovered(Button next)
    {
        if (ReferenceEquals(hovered, next)) return;
        var es = EventSystem.current;
        var data = es != null ? new PointerEventData(es) : null;
        // ホバーFX(SongRowFX 等)がマウスと同じように反応するよう enter/exit を合成する
        if (hovered != null && data != null)
        {
            ExecuteEvents.ExecuteHierarchy(hovered.gameObject, data, ExecuteEvents.pointerExitHandler);
        }
        if (next != null && data != null)
        {
            ExecuteEvents.ExecuteHierarchy(next.gameObject, data, ExecuteEvents.pointerEnterHandler);
        }
        hovered = next;
    }

    private void Click(Button target, Vector3 screen)
    {
        var es = EventSystem.current;
        if (es == null || target == null || !target.IsInteractable()) return;
        var data = new PointerEventData(es) { position = screen };
        ExecuteEvents.Execute(target.gameObject, data, ExecuteEvents.pointerClickHandler);
    }

    private static readonly List<RaycastResult> raycastResults = new List<RaycastResult>();

    private Button RaycastButton(Vector3 screen)
    {
        var es = EventSystem.current;
        if (es == null) return null;
        var data = new PointerEventData(es) { position = screen };
        raycastResults.Clear();
        es.RaycastAll(data, raycastResults);
        foreach (var hit in raycastResults)
        {
            var button = hit.gameObject.GetComponentInParent<Button>();
            if (button != null && button.IsInteractable()) return button;
            if (RespectRaycastBlockers) return null;
        }
        return null;
    }
}
