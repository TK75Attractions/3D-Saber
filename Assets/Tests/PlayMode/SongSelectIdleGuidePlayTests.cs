using System.Collections;
using System.Collections.Generic;
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
        Assert.True(guide == null, "案内とメッシュは選曲画面と一緒に解放する");
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
        Assert.NotNull(model.GetComponent<CanvasRenderer>(), "人形を実際に描画するRendererが必要");
        model.SetPose(0); Canvas.ForceUpdateCanvases();
        var mesh = model.canvasRenderer.GetMesh(); Assert.NotNull(mesh);
        Assert.Greater(mesh.vertexCount, 100, "人形の立体メッシュを描画する");
        var lowered = mesh.vertices;
        model.SetPose(2); Canvas.ForceUpdateCanvases();
        CollectionAssert.AreNotEqual(lowered, model.canvasRenderer.GetMesh().vertices, "右腕を上げてかざす動作で姿勢が変わる");
        yield return null;
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
