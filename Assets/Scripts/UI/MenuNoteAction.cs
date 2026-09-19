using UnityEngine;
using UnityEngine.UI;

// ラベル付きボタンに、本編と同じ割れる立体ノーツを重ねる。クリックの契約はそのまま残す。
[DefaultExecutionOrder(-60)]
public class MenuNoteAction : MonoBehaviour
{
    public CuttableNote Note { get; private set; }
    public RectTransform Anchor { get; private set; }
    Button button;
    Canvas canvas;
    CanvasGroup[] groups;
    Color accent;
    float respawnAt;
    float invokeAt = -1;
    NoteVisuals visuals;
    readonly Vector3[] corners = new Vector3[4];

    public static MenuNoteAction Attach(Button button, Vector2 position, float size, Color color)
    {
        var target = button.GetComponent<MenuNoteAction>() ?? button.gameObject.AddComponent<MenuNoteAction>();
        target.button = button;
        target.accent = color;
        target.canvas = button.GetComponentInParent<Canvas>();
        target.groups = button.GetComponentsInParent<CanvasGroup>();
        if (target.Anchor == null)
            target.Anchor = SongSelectVisuals.Rect(button.transform, "CutNoteAnchor", position, Vector2.one * size);
        target.Anchor.anchoredPosition = position;
        target.Anchor.sizeDelta = Vector2.one * size;
        return target;
    }

    public bool IsAvailable
    {
        get
        {
            if (button == null || !button.isActiveAndEnabled || !button.IsInteractable()) return false;
            foreach (var group in groups)
                if (group != null && (group.alpha < .15f || !group.blocksRaycasts)) return false;
            return true;
        }
    }

    void Update()
    {
        // 別の操作で移動が始まった場合、切断後の遅延操作を次画面へ持ち越さない。
        if (ScreenTransition.IsBusy) invokeAt = -1;
        if (invokeAt >= 0 && Time.unscaledTime >= invokeAt)
        {
            invokeAt = -1;
            if (IsAvailable) button.onClick.Invoke();
        }
        Sync();
    }

    void LateUpdate() { Sync(); }

    // 描画先サイズが変わる撮影時にも明示的に呼べる。
    public void Sync()
    {
        var menu = SongSelectNoteMenu.Instance;
        var camera = Camera.main;
        if (menu == null || camera == null || Anchor == null || canvas == null) return;
        if (!IsAvailable)
        {
            if (Note != null) { Note.IsJudgeable = false; Note.gameObject.SetActive(false); }
            return;
        }
        if (Note == null && Time.unscaledTime >= respawnAt) Spawn(menu);
        if (Note == null) return;
        Note.gameObject.SetActive(true);
        var uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Anchor.GetWorldCorners(corners);
        Vector3 center = OnSaberPlane(camera, RectTransformUtility.WorldToScreenPoint(uiCamera, Anchor.position));
        Vector3 left = OnSaberPlane(camera, RectTransformUtility.WorldToScreenPoint(uiCamera, corners[0]));
        Vector3 right = OnSaberPlane(camera, RectTransformUtility.WorldToScreenPoint(uiCamera, corners[3]));
        Note.transform.position = center;
        // 画面端で立方体の側面が横へ伸びてラベルを圧迫しないよう、各ノーツをカメラへ向ける。
        Note.transform.rotation = Quaternion.LookRotation(center - camera.transform.position, camera.transform.up)
            * Quaternion.Euler(-8f, -12f, 0f);
        Note.transform.localScale = Vector3.one * Vector3.Distance(left, right);
        Note.IsJudgeable = menu.IsReady && invokeAt < 0;
        if (visuals != null) visuals.SetEmissionBoost(Note.IsJudgeable ? 1f : .18f);
    }

    static Vector3 OnSaberPlane(Camera camera, Vector2 screen)
    {
        Ray ray = camera.ScreenPointToRay(screen);
        return new Plane(Vector3.back, Vector3.zero).Raycast(ray, out float distance) ? ray.GetPoint(distance) : Vector3.zero;
    }

    void Spawn(SongSelectNoteMenu menu)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.SetActive(false); // 色と設定を Awake より先に渡す。
        go.name = "MenuNote_" + button.name;
        go.transform.SetParent(menu.transform, false);
        Note = go.AddComponent<CuttableNote>();
        Note.MinimumCutSpeed = SongSelectNoteMenu.MinimumCutSpeed;
        Note.RequireJudgeableOnCut = true;
        Note.pieceLife = .45f;
        Note.pieceFadeStart = .15f;
        Note.sliceSeparationImpulse = 1.2f;
        Note.saberVelocityScale = .06f;
        Note.OnCut += OnCut;
        visuals = go.AddComponent<NoteVisuals>();
        visuals.inheritColorFromMainRenderer = false;
        visuals.baseColor = accent;
        visuals.baseEmissionStrength = 1.1f;
        go.SetActive(true);
    }

    void OnCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        note.OnCut -= OnCut;
        Note = null;
        respawnAt = Time.unscaledTime + .8f;
        SongSelectNoteMenu.Instance.BeginCooldown();
        invokeAt = Time.unscaledTime + .12f; // 画面遷移前に切断を見せる。
    }

    void OnDisable()
    {
        invokeAt = -1;
        if (Note != null) { Note.IsJudgeable = false; Note.gameObject.SetActive(false); }
    }

    void OnDestroy()
    {
        if (Note == null) return;
        Note.OnCut -= OnCut;
        UISkinKit.SafeDestroy(Note.gameObject);
    }
}
