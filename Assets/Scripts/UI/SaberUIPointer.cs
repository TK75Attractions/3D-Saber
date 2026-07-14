using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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
        private readonly float dwellSeconds;
        private readonly float cooldownSeconds;
        private object current;
        private float progressTime;
        private float cooldownLeft;

        public DwellTracker(float dwell, float cooldown)
        {
            dwellSeconds = Mathf.Max(0.01f, dwell);
            cooldownSeconds = Mathf.Max(0f, cooldown);
        }

        public float Progress01 => Mathf.Clamp01(progressTime / dwellSeconds);
        public bool InCooldown => cooldownLeft > 0f;

        public bool Tick(object target, float dt)
        {
            // クールダウンはフレーム頭で消化し、消化に使ったフレームでは溜めない
            bool wasCooling = cooldownLeft > 0f;
            if (wasCooling) cooldownLeft = Mathf.Max(0f, cooldownLeft - dt);

            if (!ReferenceEquals(target, current))
            {
                // 対象が変わったら溜め直し(乗った瞬間のフレームから溜め始める)
                current = target;
                progressTime = 0f;
            }
            if (current == null)
            {
                progressTime = 0f;
                return false;
            }
            if (wasCooling) return false;

            progressTime += dt;
            if (progressTime >= dwellSeconds)
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
        }
    }

    private Camera cam;
    private Canvas overlayCanvas;
    private Image cursorDot;
    private Image progressRing;
    private readonly DwellTracker tracker = new DwellTracker(DwellSeconds, CooldownSeconds);
    private Button hovered;
    private Vector3 smoothedWorld;
    private bool hasSmoothed;

    public Button HoveredForTest => hovered;

    // 曲選択などのシーンに設置する。UDP受信機も確保する(無ければ作る)。
    public static SaberUIPointer Build()
    {
        InputPoint.EnsureInstance();
        var go = new GameObject("SaberUIPointer");
        var pointer = go.AddComponent<SaberUIPointer>();
        pointer.cam = Camera.main;
        pointer.BuildCursor();
        return pointer;
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
        var ip = InputPoint.Instance;
        bool active = ip != null && ip.IsRecentlyActive(StaleSeconds) && cam != null;
        if (!active)
        {
            SetCursorVisible(false);
            SetHovered(null);
            tracker.Reset();
            hasSmoothed = false;
            return;
        }

        // セーバー位置(world直結座標)→スクリーン座標。軽くスムージングして震えを抑える
        Vector3 world = new Vector3(ip.LocalPosition.x, ip.LocalPosition.y, 0f);
        if (!hasSmoothed)
        {
            smoothedWorld = world;
            hasSmoothed = true;
        }
        else
        {
            float alpha = Time.unscaledDeltaTime / (0.05f + Time.unscaledDeltaTime);
            smoothedWorld = Vector3.Lerp(smoothedWorld, world, alpha);
        }
        Vector3 screen = cam.WorldToScreenPoint(smoothedWorld);

        SetCursorVisible(true);
        cursorDot.rectTransform.position = new Vector3(screen.x, screen.y, 0f);
        progressRing.rectTransform.position = new Vector3(screen.x, screen.y, 0f);

        Button target = RaycastButton(screen);
        SetHovered(target);

        bool fired = tracker.Tick(target, Time.unscaledDeltaTime);
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
        if (es == null || target == null || !target.interactable) return;
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
            if (button != null && button.interactable) return button;
        }
        return null;
    }
}
