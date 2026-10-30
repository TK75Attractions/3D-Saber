using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// タイトルの受付の鍵(2026-10-30 ユーザー「マウスの真ん中のやつのクリックで、タイトルノーツのカギを開け閉めできるようにして」
// 「①の1 2 5をやる」)。運営モードではタイトルに入ると閉じ、開始ノーツを隠して鍵の印を出す。
// 「タイトルノーツでかくして」(大きくして)で、開始ノーツは 1.6 倍・的は 1.4 倍(2026-10-31)。
public class TitleReceptionPlayTests
{
    Mouse mouse;
    InputSettings originalInputSettings, testInputSettings;

    [UnitySetUp] public IEnumerator Setup()
    {
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = false;
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        testInputSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
        testInputSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
        mouse = InputSystem.AddDevice<Mouse>();
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(5, 5) });
        OperatorMode.OverrideForTest(true);
        yield return LoadTitle();
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        OperatorMode.OverrideForTest(false);
        TitleReception.ResetForTest();
        GameSession.TutorialPending = false;
        yield return ScreenTransitionPlayTests.WaitForTransition();
        if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        var empty = SceneManager.CreateScene("TitleReceptionCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    static IEnumerator LoadTitle()
    {
        yield return SceneManager.LoadSceneAsync("Title");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<TitleSceneSkin>() == null; i++) yield return null;
        yield return null; yield return null;
    }

    IEnumerator MiddleClick()
    {
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(5, 5) }.WithButton(MouseButton.Middle));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = new Vector2(5, 5) });
        yield return null; yield return null;
    }

    [UnityTest]
    public IEnumerator InOperatorModeTheStartNoteStaysHiddenUntilTheMiddleClick()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        Assert.NotNull(skin);
        Assert.IsTrue(TitleReception.Locked, "本番(運営モード)はタイトルに入ると閉じる");
        Assert.IsFalse(skin.StartNoteShown, "鍵の間は開始ノーツを隠す");
        Assert.IsNull(Object.FindFirstObjectByType<TitleStartNote>(), "隠したノーツは剣の判定の一覧にも入らない");
        Assert.IsTrue(skin.ReceptionLockShown, "代わりに鍵の印を出す");
        var waiting = GameObject.Find("ReceptionLockText").GetComponent<TMPro.TextMeshProUGUI>();
        Assert.AreEqual(TitleReception.WaitingText, waiting.text);
        foreach (var graphic in GameObject.Find("ReceptionLock").GetComponentsInChildren<UnityEngine.UI.Graphic>())
            Assert.IsFalse(graphic.raycastTarget, "鍵の印はクリックを受けない: " + graphic.name);
        ScreenTransitionPlayTests.Capture("title-reception-locked"); // -transitionCapture <フォルダ> のときだけ撮る
        skin.TryStartPresentation(); // Enter・Space・的のクリックと同じ入口
        yield return null; yield return null;
        Assert.IsNull(Object.FindFirstObjectByType<TitleTutorialPrompt>(), "鍵の間は Enter・クリックでも始まらない");
        Assert.IsTrue(skin.AtStart);

        yield return MiddleClick();
        Assert.IsFalse(TitleReception.Locked, "マウスの中ボタンで開く");
        Assert.IsTrue(skin.StartNoteShown, "開けるとノーツが出る");
        Assert.IsFalse(skin.ReceptionLockShown, "開けると鍵の印は消える");
        yield return MiddleClick();
        Assert.IsTrue(TitleReception.Locked, "もう一度押すと閉じる");
        Assert.IsFalse(skin.StartNoteShown);
        Assert.IsTrue(skin.ReceptionLockShown);
        yield return MiddleClick();

        Assert.IsTrue(skin.TryStartPresentation());
        float deadline = Time.realtimeSinceStartup + 4f;
        TitleTutorialPrompt prompt = null;
        while (Time.realtimeSinceStartup < deadline && (prompt = Object.FindFirstObjectByType<TitleTutorialPrompt>()) == null) yield return null;
        Assert.NotNull(prompt, "開けたあとは今までどおり問いかけへ");
        yield return MiddleClick();
        Assert.IsFalse(TitleReception.Locked, "切ったあとは中ボタンで鍵を変えない");
    }

    [UnityTest]
    public IEnumerator ComingBackToTheTitleLocksAgain()
    {
        var skin = Object.FindFirstObjectByType<TitleSceneSkin>();
        skin.ToggleReception("test");
        Assert.IsFalse(TitleReception.Locked);
        yield return LoadTitle();
        Assert.IsTrue(TitleReception.Locked, "タイトルに戻ると閉じる(1回の受付=1人)");
        Assert.IsFalse(Object.FindFirstObjectByType<TitleSceneSkin>().StartNoteShown);
    }

    [UnityTest]
    public IEnumerator OutsideOperatorModeTheTitleStartsOpen()
    {
        OperatorMode.OverrideForTest(false);
        yield return LoadTitle();
        Assert.IsFalse(TitleReception.Locked, "ふだん(開発・テスト)は開けて入る");
        Assert.IsTrue(Object.FindFirstObjectByType<TitleSceneSkin>().StartNoteShown);
        Assert.IsFalse(Object.FindFirstObjectByType<TitleSceneSkin>().ReceptionLockShown);
    }

    // 大きくした開始ノーツが、浮き沈み・首振りの間ずっと見える枠(四隅の切り欠き)の内側に収まる。
    [UnityTest]
    public IEnumerator TheBiggerStartNoteStaysInsideItsFrame()
    {
        Object.FindFirstObjectByType<TitleSceneSkin>().ToggleReception("test");
        var note = Object.FindFirstObjectByType<TitleStartNote>();
        Assert.NotNull(note);
        Assert.AreEqual(Vector3.one * TitleStartNote.Size, note.transform.localScale);
        var target = (RectTransform)GameObject.Find("StartTarget").transform;
        Assert.AreEqual(Vector3.one * TitleSceneSkin.StartTargetScale, target.localScale);
        // 本番の 16:9(キャンバスがちょうど 1920×1080)で比べる。CLI のテストの画面は 4:3 のことがあり、そのときは
        // キャンバスが縦に伸びて(Expand)ノーツだけ大きく映るので、実際の画面の座標は使わない。
        // 四隅の切り欠きは的の中の ±111×±100(TitleSceneSkin.BuildStartTarget)。
        Vector2 half = new Vector2(111f, 100f) * target.localScale.x;
        Vector2 fmin = target.anchoredPosition - half, fmax = target.anchoredPosition + half;
        var camera = Camera.main;
        Matrix4x4 project = Matrix4x4.Perspective(camera.fieldOfView, 16f / 9f, camera.nearClipPlane, camera.farClipPlane)
            * camera.worldToCameraMatrix;
        // 外接箱(renderer.bounds)は首振りで角が手前へ出て大きく見えるので、メッシュの頂点そのものを写して測る。
        Vector3[] vertices = note.GetComponent<MeshFilter>().sharedMesh.vertices;
        float end = Time.realtimeSinceStartup + 3f; // 浮き沈み(0.55Hz)と首振り(0.35Hz)の1周より長く見る
        int frames = 0;
        Vector2 lo = new Vector2(1e9f, 1e9f), hi = new Vector2(-1e9f, -1e9f);
        while (Time.realtimeSinceStartup < end)
        {
            foreach (var vertex in vertices)
            {
                Vector3 ndc = project.MultiplyPoint(note.transform.TransformPoint(vertex));
                var local = new Vector2(ndc.x * 960f, ndc.y * 540f);
                lo = Vector2.Min(lo, local); hi = Vector2.Max(hi, local);
            }
            frames++;
            yield return null;
        }
        ScreenTransitionPlayTests.Capture("title-reception-open");
        Assert.Greater(frames, 10);
        string where = $"ノーツ x {lo.x:0}..{hi.x:0} y {lo.y:0}..{hi.y:0} / 枠 x {fmin.x:0}..{fmax.x:0} y {fmin.y:0}..{fmax.y:0}";
        Assert.IsTrue(lo.x > fmin.x && lo.y > fmin.y && hi.x < fmax.x && hi.y < fmax.y, "枠の外へ出る: " + where);
        // 上下の余白がおおよそそろう(枠の中心と映りの中心が 20 以内)。
        Assert.That((lo.y + hi.y) * .5f, Is.EqualTo((fmin.y + fmax.y) * .5f).Within(20f), where);
        Assert.Greater((hi.x - lo.x) / (fmax.x - fmin.x), .5f, "枠だけ大きくて中が小さい、にはしない: " + where);
    }
}
