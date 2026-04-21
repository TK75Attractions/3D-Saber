#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// バッチモードから -executeMethod SaberExperimentBuilder.BuildAll で呼び、
// Title / SongSelect / Game / Result / Experiment の5シーンとノーツ Prefab を自動生成する。
// 冪等：既にあれば上書きする。
public static class SaberExperimentBuilder
{
    private const string PrefabDir = "Assets/Prefabs";
    private const string NotePrefabPath = PrefabDir + "/NotePrefab.prefab";
    private const string NotePrefabRedPath = PrefabDir + "/NotePrefabRed.prefab";
    private const string NotePrefabBluePath = PrefabDir + "/NotePrefabBlue.prefab";
    private const string NoteGamePrefabPath = PrefabDir + "/NotePrefab_Game.prefab";  // 互換保持

    private const string SongButtonPath = PrefabDir + "/SongButton.prefab";

    private const string TitleScenePath = "Assets/Scenes/Title.unity";
    private const string SongSelectScenePath = "Assets/Scenes/SongSelect.unity";
    private const string GameScenePath = "Assets/Scenes/Game.unity";
    private const string ResultScenePath = "Assets/Scenes/Result.unity";
    private const string ExperimentScenePath = "Assets/Scenes/Experiment.unity";

    private static readonly Color RedNoteColor = new Color(1f, 0.25f, 0.3f);
    private static readonly Color BlueNoteColor = new Color(0.25f, 0.55f, 1f);
    private static readonly Color SaberColor = new Color(0.3f, 1f, 0.95f);
    private static readonly Color GuideColor = new Color(1f, 1f, 1f, 0.25f);

    public static void BuildAll()
    {
        Debug.Log("[Builder] Start");
        EnsureDir(PrefabDir);
        EnsureDir("Assets/Scenes");

        BuildNotePrefab(NotePrefabPath, RedNoteColor, addAlwaysJudgeable: true);
        BuildNotePrefab(NotePrefabRedPath, RedNoteColor, addAlwaysJudgeable: false);
        BuildNotePrefab(NotePrefabBluePath, BlueNoteColor, addAlwaysJudgeable: false);
        BuildNotePrefab(NoteGamePrefabPath, RedNoteColor, addAlwaysJudgeable: false);

        BuildSongButtonPrefab();

        BuildTitleScene();
        BuildSongSelectScene();
        BuildGameScene();
        BuildResultScene();
        BuildExperimentScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[Builder] Done");
    }

    // ---------- 共通ヘルパー ----------

    private static void EnsureDir(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }

    private static Material MakeLit(Color color, float emissionStrength = 0.6f)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        else m.color = color;
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", color * emissionStrength);
        }
        return m;
    }

    private static Material MakeTransparentLit(Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(sh);
        // URP Lit を透過モードに
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3000;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        else m.color = color;
        return m;
    }

    private static GameObject CreateCamera(Vector3 pos, Vector3 rotEuler)
    {
        var cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.transform.position = pos;
        cameraGo.transform.rotation = Quaternion.Euler(rotEuler);
        var cam = cameraGo.AddComponent<Camera>();
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cameraGo.AddComponent<AudioListener>();
        return cameraGo;
    }

    private static GameObject CreateLight()
    {
        var lightGo = new GameObject("Directional Light");
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        var l = lightGo.AddComponent<Light>();
        l.type = LightType.Directional;
        l.intensity = 1f;
        return lightGo;
    }

    private static GameObject CreateUnlockFrameRate()
    {
        var go = new GameObject("FrameRate");
        go.AddComponent<UnlockFrameRate>();
        return go;
    }

    // Input System 有効時は InputSystemUIInputModule を使う必要がある。
    private static GameObject CreateEventSystem()
    {
        var go = new GameObject("EventSystem");
        go.AddComponent<UnityEngine.EventSystems.EventSystem>();
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        return go;
    }

    // ---------- Prefab ----------

    private static void BuildNotePrefab(string path, Color baseColor, bool addAlwaysJudgeable)
    {
        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
        root.name = Path.GetFileNameWithoutExtension(path);
        root.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
        root.GetComponent<Renderer>().sharedMaterial = MakeLit(baseColor, 0.4f);
        root.AddComponent<CuttableNote>();
        if (addAlwaysJudgeable) root.AddComponent<AlwaysJudgeable>();

        // 6 面ステッカー：それぞれ違う色で面の向きが一目でわかるようにする
        AddFaceSticker(root, "Front", new Vector3(0, 0, -0.51f), new Vector3(0.7f, 0.7f, 0.02f), Color.white, 1.1f);
        AddFaceSticker(root, "Back", new Vector3(0, 0, 0.51f), new Vector3(0.7f, 0.7f, 0.02f), new Color(0.35f, 0.35f, 0.35f), 0.2f);
        AddFaceSticker(root, "Right", new Vector3(0.51f, 0, 0), new Vector3(0.02f, 0.7f, 0.7f), new Color(1f, 0.95f, 0.2f), 0.8f);
        AddFaceSticker(root, "Left", new Vector3(-0.51f, 0, 0), new Vector3(0.02f, 0.7f, 0.7f), new Color(1f, 0.5f, 0.1f), 0.8f);
        AddFaceSticker(root, "Top", new Vector3(0, 0.51f, 0), new Vector3(0.7f, 0.02f, 0.7f), new Color(0.3f, 1f, 0.8f), 0.8f);
        AddFaceSticker(root, "Bottom", new Vector3(0, -0.51f, 0), new Vector3(0.7f, 0.02f, 0.7f), new Color(1f, 0.3f, 0.8f), 0.8f);

        // プレイヤー側（-Z）に的マーカー
        var target = AddFaceSticker(root, "Target", new Vector3(0, 0, -0.53f), new Vector3(0.25f, 0.25f, 0.02f), Color.white, 2.0f);
        target.transform.localRotation = Quaternion.Euler(0, 0, 45f);

        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        Debug.Log($"[Builder] Prefab saved: {path}");
    }

    private static GameObject AddFaceSticker(GameObject root, string name, Vector3 localPos, Vector3 localScale, Color color, float emission)
    {
        GameObject s = GameObject.CreatePrimitive(PrimitiveType.Cube);
        s.name = name;
        s.transform.SetParent(root.transform, false);
        s.transform.localPosition = localPos;
        s.transform.localScale = localScale;
        Object.DestroyImmediate(s.GetComponent<BoxCollider>());
        s.GetComponent<Renderer>().sharedMaterial = MakeLit(color, emission);
        return s;
    }

    private static void BuildSongButtonPrefab()
    {
        var go = new GameObject("SongButton", typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(400f, 70f);
        go.GetComponent<Image>().color = new Color(0.12f, 0.14f, 0.2f);

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        labelGo.transform.SetParent(go.transform, false);
        var lrt = labelGo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = Vector2.zero;
        lrt.offsetMax = Vector2.zero;
        var text = labelGo.GetComponent<Text>();
        text.alignment = TextAnchor.MiddleCenter;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 28;
        text.color = Color.white;
        text.text = "Song";

        PrefabUtility.SaveAsPrefabAsset(go, SongButtonPath);
        Object.DestroyImmediate(go);
        Debug.Log($"[Builder] Prefab saved: {SongButtonPath}");
    }

    // ---------- 共通：Saber（薄いブレード） ----------

    private static GameObject BuildSaber(bool useMouse)
    {
        GameObject saber = new GameObject("Saber");
        saber.transform.position = Vector3.zero;

        var tracker = saber.AddComponent<SaberTracker>();
        var judge = saber.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeRadius = 0.3f;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.noteHitRadiusXY = 0.55f;

        if (useMouse)
        {
            var mouse = saber.AddComponent<MouseSaberInput>();
            mouse.fixedZ = 0f;
            mouse.minBounds = new Vector2(-5.5f, -3f);
            mouse.maxBounds = new Vector2(5.5f, 3f);
        }

        // 見た目：小さな発光点
        GameObject point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        point.name = "Point";
        point.transform.SetParent(saber.transform, false);
        point.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
        Object.DestroyImmediate(point.GetComponent<SphereCollider>());
        point.GetComponent<Renderer>().sharedMaterial = MakeLit(SaberColor, 2.2f);

        // 点の周りに薄いリング（視認性補強）
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ring.name = "PointGlow";
        ring.transform.SetParent(saber.transform, false);
        ring.transform.localScale = new Vector3(0.55f, 0.55f, 0.55f);
        Object.DestroyImmediate(ring.GetComponent<SphereCollider>());
        ring.GetComponent<Renderer>().sharedMaterial = MakeTransparentLit(new Color(SaberColor.r, SaberColor.g, SaberColor.b, 0.25f));

        return saber;
    }

    // ---------- 共通：切断面ガイド ----------

    private static GameObject BuildJudgeGuide(float judgeZ)
    {
        var guide = new GameObject("JudgeGuide");
        guide.transform.position = new Vector3(0f, 0f, judgeZ);

        // 水平ライン（X 軸）
        GameObject hLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hLine.name = "HLine";
        hLine.transform.SetParent(guide.transform, false);
        hLine.transform.localScale = new Vector3(14f, 0.02f, 0.02f);
        Object.DestroyImmediate(hLine.GetComponent<BoxCollider>());
        hLine.GetComponent<Renderer>().sharedMaterial = MakeTransparentLit(GuideColor);

        // 垂直ライン（Y 軸）
        GameObject vLine = GameObject.CreatePrimitive(PrimitiveType.Cube);
        vLine.name = "VLine";
        vLine.transform.SetParent(guide.transform, false);
        vLine.transform.localScale = new Vector3(0.02f, 8f, 0.02f);
        Object.DestroyImmediate(vLine.GetComponent<BoxCollider>());
        vLine.GetComponent<Renderer>().sharedMaterial = MakeTransparentLit(GuideColor);

        // 判定面の矩形枠
        GameObject frameTop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        frameTop.name = "FrameTop";
        frameTop.transform.SetParent(guide.transform, false);
        frameTop.transform.localScale = new Vector3(11f, 0.04f, 0.02f);
        frameTop.transform.localPosition = new Vector3(0f, 3f, 0f);
        Object.DestroyImmediate(frameTop.GetComponent<BoxCollider>());
        frameTop.GetComponent<Renderer>().sharedMaterial = MakeTransparentLit(GuideColor);

        GameObject frameBot = GameObject.Instantiate(frameTop, guide.transform);
        frameBot.name = "FrameBottom";
        frameBot.transform.localPosition = new Vector3(0f, -3f, 0f);

        return guide;
    }

    // ---------- Title シーン ----------

    private static void BuildTitleScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera(new Vector3(0, 0, -10), Vector3.zero);
        CreateLight();

        GameObject canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1920, 1080);

        CreateEventSystem();

        // タイトル文字
        CreateLabel(canvasGo.transform, "3D SABER", new Vector2(0, 200), 120, FontStyle.Bold);

        // Start ボタン
        var startBtn = CreateButton(canvasGo.transform, "START", new Vector2(0, -40), new Vector2(340, 100));
        var titleCtl = canvasGo.AddComponent<TitleMenuController>();
        titleCtl.songSelectSceneName = "SongSelect";
        UnityEventTools.AddPersistentListener(startBtn.onClick, titleCtl.OnStartButton);

        var quitBtn = CreateButton(canvasGo.transform, "QUIT", new Vector2(0, -180), new Vector2(340, 80));
        UnityEventTools.AddPersistentListener(quitBtn.onClick, titleCtl.OnQuitButton);

        EditorSceneManager.SaveScene(scene, TitleScenePath);
        AddSceneToBuildSettings(TitleScenePath);
        Debug.Log($"[Builder] Scene saved: {TitleScenePath}");
    }

    // ---------- SongSelect シーン ----------

    private static void BuildSongSelectScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera(new Vector3(0, 0, -10), Vector3.zero);
        CreateLight();

        GameObject canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1920, 1080);

        CreateEventSystem();

        CreateLabel(canvasGo.transform, "Select Song", new Vector2(0, 380), 72, FontStyle.Bold);

        var listGo = new GameObject("List", typeof(RectTransform), typeof(VerticalLayoutGroup));
        listGo.transform.SetParent(canvasGo.transform, false);
        var lrt = listGo.GetComponent<RectTransform>();
        lrt.sizeDelta = new Vector2(500, 400);
        lrt.anchoredPosition = new Vector2(0, -20);
        var layout = listGo.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 18;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlHeight = false;
        layout.childControlWidth = false;

        var selector = canvasGo.AddComponent<SongSelectController>();
        selector.gameSceneName = "Game";
        selector.listRoot = listGo.transform;
        selector.buttonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SongButtonPath);

        EditorSceneManager.SaveScene(scene, SongSelectScenePath);
        AddSceneToBuildSettings(SongSelectScenePath);
        Debug.Log($"[Builder] Scene saved: {SongSelectScenePath}");
    }

    // ---------- Game シーン ----------

    private static void BuildGameScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        CreateCamera(new Vector3(0f, 0.8f, -7f), new Vector3(5f, 0f, 0f));
        CreateLight();
        CreateUnlockFrameRate();

        BuildSaber(useMouse: true);
        BuildJudgeGuide(judgeZ: 0f);

        GameObject noteRoot = new GameObject("NoteRoot");
        GameObject gameRoot = new GameObject("GameRoot");

        var audioHolder = new GameObject("SongPlayer");
        audioHolder.transform.SetParent(gameRoot.transform, false);
        audioHolder.AddComponent<AudioSource>();
        var songPlayer = audioHolder.AddComponent<SongPlayer>();
        songPlayer.startDelay = 0.2f;

        var spawnerGo = new GameObject("NoteSpawner");
        spawnerGo.transform.SetParent(gameRoot.transform, false);
        var spawner = spawnerGo.AddComponent<NoteSpawner>();
        spawner.notePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NotePrefabRedPath);
        spawner.notePrefabRed = AssetDatabase.LoadAssetAtPath<GameObject>(NotePrefabRedPath);
        spawner.notePrefabBlue = AssetDatabase.LoadAssetAtPath<GameObject>(NotePrefabBluePath);
        spawner.noteRoot = noteRoot.transform;
        spawner.approachTime = 2.0f;
        spawner.spawnZ = 20f;
        spawner.judgeZ = 0f;
        spawner.judgeWindow = 0.2f;
        spawner.missGrace = 0.05f;

        var scoreGo = new GameObject("ScoreManager");
        scoreGo.transform.SetParent(gameRoot.transform, false);
        var score = scoreGo.AddComponent<ScoreManager>();
        score.songPlayer = songPlayer;

        var gpmGo = new GameObject("GamePlayManager");
        gpmGo.transform.SetParent(gameRoot.transform, false);
        var gpm = gpmGo.AddComponent<GamePlayManager>();
        gpm.songPlayer = songPlayer;
        gpm.noteSpawner = spawner;
        gpm.scoreManager = score;
        gpm.resultSceneName = "Result";
        gpm.endWaitSeconds = 2.0f;

        // HUD
        GameObject canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1920, 1080);

        CreateEventSystem();

        var scoreText = CreateLabel(canvasGo.transform, "Score 0", new Vector2(-720, 450), 56, FontStyle.Bold);
        var comboText = CreateLabel(canvasGo.transform, "", new Vector2(720, 450), 56, FontStyle.Bold);
        comboText.alignment = TextAnchor.UpperRight;
        var tierText = CreateLabel(canvasGo.transform, "", new Vector2(0, 250), 140, FontStyle.Bold);
        tierText.color = new Color(1f, 1f, 1f, 0f);

        var hud = canvasGo.AddComponent<ScoreHUD>();
        hud.score = score;
        hud.scoreText = scoreText;
        hud.comboText = comboText;
        hud.tierText = tierText;

        EditorSceneManager.SaveScene(scene, GameScenePath);
        AddSceneToBuildSettings(GameScenePath);
        Debug.Log($"[Builder] Scene saved: {GameScenePath}");
    }

    // ---------- Result シーン ----------

    private static void BuildResultScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        CreateCamera(new Vector3(0, 0, -10), Vector3.zero);
        CreateLight();

        GameObject canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var cs = canvasGo.GetComponent<CanvasScaler>();
        cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        cs.referenceResolution = new Vector2(1920, 1080);

        CreateEventSystem();

        var titleText = CreateLabel(canvasGo.transform, "", new Vector2(0, 400), 64, FontStyle.Bold);
        var scoreText = CreateLabel(canvasGo.transform, "Score 0", new Vector2(0, 260), 96, FontStyle.Bold);
        var comboText = CreateLabel(canvasGo.transform, "Max Combo 0", new Vector2(0, 160), 48, FontStyle.Normal);

        var perfectText = CreateLabel(canvasGo.transform, "", new Vector2(0, 40), 40, FontStyle.Normal);
        var greatText = CreateLabel(canvasGo.transform, "", new Vector2(0, -20), 40, FontStyle.Normal);
        var goodText = CreateLabel(canvasGo.transform, "", new Vector2(0, -80), 40, FontStyle.Normal);
        var badText = CreateLabel(canvasGo.transform, "", new Vector2(0, -140), 40, FontStyle.Normal);
        var missText = CreateLabel(canvasGo.transform, "", new Vector2(0, -200), 40, FontStyle.Normal);

        perfectText.color = new Color(0.35f, 1f, 0.85f);
        greatText.color = new Color(0.9f, 1f, 0.4f);
        goodText.color = new Color(1f, 0.8f, 0.3f);
        badText.color = new Color(1f, 0.5f, 0.3f);
        missText.color = new Color(0.6f, 0.6f, 0.7f);

        var backBtn = CreateButton(canvasGo.transform, "BACK", new Vector2(0, -360), new Vector2(280, 80));

        var ctrl = canvasGo.AddComponent<ResultController>();
        ctrl.titleText = titleText;
        ctrl.scoreText = scoreText;
        ctrl.comboText = comboText;
        ctrl.perfectText = perfectText;
        ctrl.greatText = greatText;
        ctrl.goodText = goodText;
        ctrl.badText = badText;
        ctrl.missText = missText;
        ctrl.titleSceneName = "Title";
        UnityEventTools.AddPersistentListener(backBtn.onClick, ctrl.OnBackButton);

        EditorSceneManager.SaveScene(scene, ResultScenePath);
        AddSceneToBuildSettings(ResultScenePath);
        Debug.Log($"[Builder] Scene saved: {ResultScenePath}");
    }

    // ---------- Experiment シーン（手置きノーツ） ----------

    private static void BuildExperimentScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        CreateCamera(new Vector3(0, 0, -10f), Vector3.zero);
        CreateLight();
        CreateUnlockFrameRate();

        BuildSaber(useMouse: true);
        BuildJudgeGuide(judgeZ: 0f);

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NotePrefabPath);
        PlaceNote(prefab, new Vector3(2f, 0f, 0f));
        PlaceNote(prefab, new Vector3(-2f, 0f, 0f));
        PlaceNote(prefab, new Vector3(0f, 1.5f, 0f));

        EditorSceneManager.SaveScene(scene, ExperimentScenePath);
        AddSceneToBuildSettings(ExperimentScenePath);
        Debug.Log($"[Builder] Scene saved: {ExperimentScenePath}");
    }

    // ---------- UI ヘルパー ----------

    private static Text CreateLabel(Transform parent, string text, Vector2 pos, int size, FontStyle style)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(900, 200);
        rt.anchoredPosition = pos;
        var t = go.GetComponent<Text>();
        t.alignment = TextAnchor.MiddleCenter;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.text = text;
        t.color = Color.white;
        return t;
    }

    private static Button CreateButton(Transform parent, string label, Vector2 pos, Vector2 size)
    {
        var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        go.GetComponent<Image>().color = new Color(0.15f, 0.2f, 0.3f);
        var t = CreateLabel(go.transform, label, Vector2.zero, 36, FontStyle.Bold);
        t.GetComponent<RectTransform>().sizeDelta = size;
        return go.GetComponent<Button>();
    }

    private static void PlaceNote(GameObject prefab, Vector3 pos)
    {
        if (prefab == null) return;
        GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        inst.transform.position = pos;
    }

    private static void AddSceneToBuildSettings(string path)
    {
        var current = EditorBuildSettings.scenes;
        foreach (var s in current)
        {
            if (s.path == path) return;
        }
        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(current);
        list.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = list.ToArray();
    }
}
#endif
