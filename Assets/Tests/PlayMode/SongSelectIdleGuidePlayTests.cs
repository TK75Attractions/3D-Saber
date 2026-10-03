using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class SongSelectIdleGuidePlayTests
{
    SongSelectIdleGuide guide;
    SongSelectAimPointer aim;
    SongSelectController controller;
    [UnitySetUp] public IEnumerator Open()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        float deadline = Time.realtimeSinceStartup + 15;
        while (Object.FindFirstObjectByType<SongSelectIdleGuide>() == null && Time.realtimeSinceStartup < deadline) yield return null;
        guide = Object.FindFirstObjectByType<SongSelectIdleGuide>(); Assert.NotNull(guide); guide.enabled = false;
        aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); aim.enabled = false;
        controller = Object.FindFirstObjectByType<SongSelectController>();
        yield return ScreenTransitionPlayTests.WaitForTransition();
        yield return new WaitForSecondsRealtime(.6f);
    }
    [UnityTearDown] public IEnumerator Close()
    {
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("IdleGuideCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
        yield return null;
        Assert.True(guide == null, "案内は選曲画面と一緒に解放する");
        Assert.False(Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(t => t.name == "SongSelectHumanStage"), "3D描画用のステージを生成しない");
    }
    [UnityTest] public IEnumerator IdleShowsGuideAndRaycastsStillReachDiscs()
    {
        guide.Tick(9, true); Assert.False(guide.IsVisible);
        guide.Tick(1.01f, true); Assert.True(guide.IsVisible);
        guide.Tick(.25f, true); Canvas.ForceUpdateCanvases();
        var label = guide.GetComponentInChildren<TextMeshProUGUI>(); Assert.NotNull(label);
        foreach (var graphic in guide.GetComponentsInChildren<Graphic>()) Assert.False(graphic.raycastTarget, graphic.name);
        var target = controller.startButton.GetComponent<SongSelectDiscTarget>();
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = target.ScreenRect().center }, hits);
        Assert.IsNotEmpty(hits); Assert.AreEqual(target, hits[0].gameObject.GetComponentInParent<SongSelectDiscTarget>());
        var model = guide.GetComponentInChildren<SongSelectGuideModel>(); Assert.NotNull(model);
        Assert.NotNull(model.GetComponent<CanvasRenderer>(), "2DアニメーションをUIに描画する");
        model.SetPose(0); Canvas.ForceUpdateCanvases(); Assert.True(model.IsReady);
        Assert.IsInstanceOf<Texture2D>(model.mainTexture);
        Assert.GreaterOrEqual(model.mainTexture.width, 1600, "画面上で読める解像度のイラストを使う");
        Assert.AreEqual(0, model.FrameIndex);
        var resting = model.canvasRenderer.GetMesh().uv;
        model.SetPose(2); Canvas.ForceUpdateCanvases();
        Assert.AreEqual(7, model.FrameIndex, "右腕を上げたコマまで再生する");
        CollectionAssert.AreNotEqual(resting, model.canvasRenderer.GetMesh().uv, "表示するイラストが切り替わる");
        model.SetPose(SongSelectGuideModel.LoopSeconds); Canvas.ForceUpdateCanvases();
        CollectionAssert.AreEqual(resting, model.canvasRenderer.GetMesh().uv, "一周して最初のコマへ戻る");
        var seen = new HashSet<int>();
        for (int i = 0; i <= 45; i++) { model.SetPose(i / 30f); seen.Add(model.FrameIndex); }
        Assert.AreEqual(8, seen.Count, "腕を上げる途中の全コマを使う");
        model.SetPose(float.NaN); Assert.AreEqual(0, model.FrameIndex);
        yield return null;
    }
    [UnityTest] public IEnumerator IllustrationStopsWhenHiddenAndUsesNo3DPreview()
    {
        int originalMask = Camera.main.cullingMask;
        var cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        guide.Tick(10, true);
        var model = guide.GetComponentInChildren<SongSelectGuideModel>();
        Assert.True(model.IsReady);
        Assert.False(model.mainTexture is RenderTexture);
        Assert.AreEqual(cameras.Length, Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
        Assert.IsNull(GameObject.Find("SongSelectHumanStage"));
        Assert.AreEqual(originalMask, Camera.main.cullingMask);
        model.SetPose(2); int frame = model.FrameIndex;
        guide.RegisterActivity(); Assert.False(model.gameObject.activeInHierarchy);
        model.SetPose(0); Assert.AreEqual(frame, model.FrameIndex, "非表示中はコマを進めない");
        Object.Destroy(model.gameObject); yield return null; yield return null;
        Assert.True(model == null);
    }
    [UnityTest] public IEnumerator MovingTheActualAimPathHidesGuideWithoutChangingSelection()
    {
        int selected = controller.SelectedIndex;
        guide.Tick(10, true); Assert.True(guide.IsVisible);
        aim.TickAt(new Vector2(10, 10), .01f);
        aim.TickAt(new Vector2(120, 10), .01f);
        Assert.False(guide.IsVisible); Assert.True(guide.Completed);
        Assert.AreEqual(selected, controller.SelectedIndex);
        guide.Tick(60, true); Assert.False(guide.IsVisible);
        yield return null;
    }
    [UnityTest] public IEnumerator AimingAtTargetHidesGuideBeforeShotAndDoesNotBlockSelection()
    {
        guide.Tick(10, true); Assert.True(guide.IsVisible);
        var target = controller.difficultyButtons[1].GetComponent<SongSelectDiscTarget>();
        aim.TickAt(target.ScreenRect().center, .1f);
        Assert.False(guide.IsVisible); Assert.Zero(aim.ShotCount);
        for (int i = 0; i < 9; i++) aim.TickAt(target.ScreenRect().center, .1f);
        Assert.AreEqual(1, controller.SelectedDifficultyIndex); Assert.AreEqual(1, aim.ShotCount);
        yield return null;
    }
    [UnityTest] public IEnumerator ButtonSelectionBeforeDeadlineSuppressesGuide()
    {
        controller.difficultyButtons[1].onClick.Invoke(); guide.Tick(20, true);
        Assert.True(guide.Completed); Assert.False(guide.IsVisible);
        yield return null;
    }
    [UnityTest] public IEnumerator InactiveScreenHidesGuideAndDoesNotConsumeTheTenSeconds()
    {
        guide.Tick(50, false); Assert.False(guide.IsVisible);
        guide.Tick(10, true); Assert.True(guide.IsVisible);
        guide.Tick(1, false); Assert.False(guide.IsVisible);
        guide.Tick(.3f, true); Assert.True(guide.IsVisible);
        yield return null;
    }
}
