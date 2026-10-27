using TMPro;
using UnityEngine;

// PlayTrackingStatus から駆動する表示。図形と参照は生成時に保持し、フレームごとに作らない。
public sealed class PlayRecoveryView
{
    readonly RectTransform canvas, panel;
    readonly SongSelectDiscGraphic mini;
    readonly TextMeshProUGUI hint;
    readonly SongSelectDiscGraphic[] dots = new SongSelectDiscGraphic[2];
    readonly SongSelectDiscGraphic[] arrows = new SongSelectDiscGraphic[8];
    readonly SongSelectDiscGraphic[] poses = new SongSelectDiscGraphic[2];
    readonly SaberInputBridge[] bridges = new SaberInputBridge[2];
    readonly Vector3[] targets = new Vector3[2];
    readonly float[] diameters = new float[2];
    readonly Color[] colors = { UISkinPalette.LogoRed, UISkinPalette.LogoBlue };
    Camera camera;
    public bool MiniShown => mini.gameObject.activeInHierarchy;
    public int ShownPoseMask { get; private set; }

    public PlayRecoveryView(RectTransform canvas, RectTransform panel)
    {
        this.canvas = canvas; this.panel = panel;
        mini = SongSelectSkin.Graphic(panel, "LastCameraPosition", new Vector2(760, 0),
            new Vector2(320, 180), SongSelectDiscGraphic.Shape.Panel, new Color(.015f, .025f, .04f, .96f));
        mini.Edge = Color.white; mini.Width = 2; mini.Radius = 0;
        hint = UISkinKit.MakeTMP(mini.transform, "LastPositionHint", "", 26, SongSelectSkin.Gold,
            TextAlignmentOptions.MidlineRight, new Vector2(-380, -136), new Vector2(1080, 36),
            FontStyles.Normal, 0, UISkinKit.JapaneseFallbackFontAsset());
        hint.raycastTarget = false; hint.textWrappingMode = TextWrappingModes.NoWrap;
        for (int i = 0; i < 2; i++)
        {
            // 幅9の輪を直径18に描くと、内径0の色の点になる。
            dots[i] = SongSelectSkin.Graphic(mini.transform, i == 0 ? "RedPoint" : "BluePoint",
                Vector2.zero, new Vector2(18, 18), SongSelectDiscGraphic.Shape.Ring, colors[i]);
            dots[i].Width = 9;
            poses[i] = SongSelectSkin.Graphic(canvas, i == 0 ? "RedReadyRing" : "BlueReadyRing",
                Vector2.zero, Vector2.one, SongSelectDiscGraphic.Shape.Ring, colors[i]);
            poses[i].Width = 4;
            for (int edge = 0; edge < 4; edge++)
            {
                var arrow = SongSelectSkin.Graphic(mini.transform, "Edge" + i + "_" + edge,
                    Vector2.zero, new Vector2(14, 14), SongSelectDiscGraphic.Shape.Arrow, colors[i]);
                // 元の三角は左向き。右・下・上の順に回す。
                arrow.rectTransform.localRotation = Quaternion.Euler(0, 0,
                    edge == 0 ? 0 : edge == 1 ? 180 : edge == 2 ? 90 : -90);
                arrows[i * 4 + edge] = arrow;
            }
        }
        Show(false, null, 0, 0);
    }

    public void Configure(NoteSpawner notes, SaberCutJudge first, SaberCutJudge second)
    {
        camera = Camera.main;
        Bind(first); Bind(second);
        var gate = Object.FindFirstObjectByType<JudgeGateFrame>();
        Vector3 center = gate != null ? gate.transform.position : new Vector3(0, 0, notes.judgeZ);
        Vector3 right = gate != null ? gate.transform.TransformVector(Vector3.right) : Vector3.right;
        float half = gate != null ? gate.HalfWidth : 5.5f;
        if (camera != null && camera.WorldToScreenPoint(center + right).x < camera.WorldToScreenPoint(center).x)
            right = -right;
        targets[0] = center + right * (half * .35f);
        targets[1] = center - right * (half * .35f);
        diameters[0] = .6f * NoteWidth(notes.notePrefabRed != null ? notes.notePrefabRed : notes.notePrefab, notes.noteRoot);
        diameters[1] = .6f * NoteWidth(notes.notePrefabBlue != null ? notes.notePrefabBlue : notes.notePrefab, notes.noteRoot);
    }

    void Bind(SaberCutJudge judge)
    {
        if (judge == null || judge.bladeProvider == null) return;
        var bridge = judge.bladeProvider;
        if (bridge.stickIndex == 1 || bridge.stickIndex == 2) bridges[bridge.stickIndex - 1] = bridge;
        if (bridge.targetCamera != null) camera = bridge.targetCamera;
    }

    static float NoteWidth(GameObject prefab, Transform noteRoot)
    {
        if (prefab == null) return 1;
        var mesh = prefab.GetComponent<MeshFilter>();
        float width = mesh != null && mesh.sharedMesh != null ? mesh.sharedMesh.bounds.size.x : 1;
        return width * Mathf.Abs(prefab.transform.localScale.x) *
            (noteRoot != null ? Mathf.Abs(noteRoot.lossyScale.x) : 1) *
            (DisplaySettings.ProjectorMode ? ProjectorMode.NoteScale : 1);
    }

    bool Project(Vector3 world, out Vector2 local)
    {
        local = default;
        if (camera == null) return false;
        Vector3 screen = camera.WorldToScreenPoint(world);
        return screen.z > 0 && RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screen, null, out local);
    }

    // R の天地返しの間は、画面の絵が回り、剣の入力もカメラの経路で写されている(WorldTurn.MapInput)。
    // ワールドの点を同じ写しで写してから画面に写すと、回す前と同じ画面の位置に出る(輪は画面の左右のまま、カメラの映像の点は反転しない)。
    static Vector3 Turned(Vector3 world) => WorldTurn.MapInput(world, WorldTurn.CurrentDegrees);

    // 輪と判定に同じ画面座標を使う。表示中の剣の両端の中点を取り、感度を二重に掛けない。
    public int InsideMask()
    {
        int inside = 0;
        for (int i = 0; i < 2; i++)
        {
            Vector3 target = Turned(targets[i]);
            if (!Project(target, out Vector2 center) ||
                !Project(target + Vector3.right * diameters[i], out Vector2 end)) continue;
            float diameter = Vector2.Distance(center, end);
            poses[i].rectTransform.anchoredPosition = center;
            poses[i].rectTransform.sizeDelta = Vector2.one * diameter;
            var bridge = bridges[i];
            if (bridge != null && bridge.isActiveAndEnabled && bridge.HasBlade && !bridge.UsingMouseFallback &&
                Project((bridge.WorldEndA + bridge.WorldEndB) * .5f, out Vector2 midpoint) &&
                PlayRecoveryRules.Inside(midpoint, center, diameter)) inside |= 1 << i;
        }
        return inside;
    }

    public void Show(bool visible, InputPoint input, double now, int poseMask)
    {
        SetActive(mini.gameObject, visible);
        panel.anchoredPosition = new Vector2(visible ? -180 : 0, 330);
        ShownPoseMask = visible && camera != null ? poseMask : 0;
        for (int i = 0; i < 2; i++) SetActive(poses[i].gameObject, (ShownPoseMask & (1 << i)) != 0);
        if (!visible) return;
        bool flipX = input != null && input.useDirectWorldMapping && input.worldScale.x < 0;
        bool flipY = input != null && input.useDirectWorldMapping && input.worldScale.y < 0;
        if (camera != null)
        {
            // 天地返しの間も、回す前の画面の向きで決める(入力も回っているので、カメラの映像の点は反転しない)。
            Vector3 origin = camera.WorldToScreenPoint(Turned(Vector3.zero));
            flipX ^= camera.WorldToScreenPoint(Turned(Vector3.right)).x < origin.x;
            flipY ^= camera.WorldToScreenPoint(Turned(Vector3.up)).y < origin.y;
        }
        // InputPoint/SaberInputBridge と同じ左右・上下。iPhoneの鏡像設定と4隅補正を含む実機の向きは要確認。
        string line = "";
        double newestEdge = double.NegativeInfinity;
        for (int i = 0; i < 2; i++)
        {
            double received = input == null ? -1 :
                i == 0 ? input.LastCameraReceivedTime : input.LastCameraReceivedTime2;
            Vector2 raw = input == null ? Vector2.zero :
                i == 0 ? input.LastCameraPosition01 : input.LastCameraPosition012;
            Vector2 p = PlayRecoveryRules.Display01(raw, flipX, flipY);
            float alpha = PlayRecoveryRules.PointAlpha(now, received);
            SetActive(dots[i].gameObject, alpha > 0);
            Vector2 position = new Vector2(Mathf.Clamp((p.x - .5f) * 320, -149, 149),
                Mathf.Clamp((p.y - .5f) * 180, -79, 79));
            dots[i].rectTransform.anchoredPosition = position;
            Color color = colors[i]; color.a = alpha; dots[i].color = color;
            int edges = alpha > 0 ? PlayRecoveryRules.Edges(p) : 0;
            for (int edge = 0; edge < 4; edge++)
            {
                var arrow = arrows[i * 4 + edge];
                SetActive(arrow.gameObject, (edges & (1 << edge)) != 0);
                arrow.color = color;
                arrow.rectTransform.anchoredPosition = edge < 2 ?
                    new Vector2(edge == 0 ? -170 : 170, position.y) :
                    new Vector2(position.x, edge == 2 ? -100 : 100);
            }
            if (edges != 0 && received > newestEdge)
            {
                newestEdge = received;
                line = PlayRecoveryRules.Hint(p);
            }
        }
        if (hint.text != line) hint.text = line;
    }

    static void SetActive(GameObject go, bool active)
    {
        if (go.activeSelf != active) go.SetActive(active);
    }
}
