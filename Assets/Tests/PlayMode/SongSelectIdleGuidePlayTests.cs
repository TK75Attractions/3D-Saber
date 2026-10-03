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
        Assert.True(guide == null, "案内とメッシュは選曲画面と一緒に解放する");
        Assert.False(Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(t => t.name == "SongSelectHumanStage"), "専用カメラと人体も選曲画面と一緒に解放する");
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
        model.SetPose(0); Canvas.ForceUpdateCanvases(); Assert.True(model.IsReady);
        Assert.IsInstanceOf<RenderTexture>(model.mainTexture);
        var body = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None).Single(r => r.name == "GuideHumanBody");
        Assert.Greater(body.sharedMesh.vertexCount, 6000, "骨格に追従する実際の人体メッシュを使う");
        var hand = body.bones.Single(b => b.name == "hand_r");
        var lowered = hand.position;
        var baked = new Mesh(); body.BakeMesh(baked); var restingMesh = baked.vertices;
        foreach (string finger in new[] { "thumb", "index", "middle", "ring", "pinky" })
            Assert.True(body.bones.Any(b => b.name == finger + "_03_r"), "右手の各指にも骨がある");
        var grip = hand.GetComponentsInChildren<Transform>().Single(t => t.name == "RightHandGrip");
        var localGrip = grip.localPosition;
        model.SetPose(2); Canvas.ForceUpdateCanvases();
        Assert.Greater(hand.position.y - lowered.y, .3f, "右腕を上げてかざす");
        body.BakeMesh(baked); CollectionAssert.AreNotEqual(restingMesh, baked.vertices, "骨だけでなく表面のメッシュも変形する");
        Assert.AreEqual(localGrip, grip.localPosition, "柄は動作中も右手から離れない");
        model.SetPose(5.2f); Assert.Less(Vector3.Distance(lowered, hand.position), .003f, "一周して自然に最初の姿勢へ戻る");
        Object.Destroy(baked);
        yield return null;
    }
    [UnityTest] public IEnumerator PreviewStopsWhenHiddenAndReleasesItsTexture()
    {
        int originalMask = Camera.main.cullingMask;
        Assert.IsNull(GameObject.Find("SongSelectHumanStage"), "表示前は人体を生成しない");
        guide.Tick(10, true);
        var model = guide.GetComponentInChildren<SongSelectGuideModel>();
        Assert.True(model.IsReady);
        var texture = model.mainTexture as RenderTexture;
        var stage = GameObject.Find("SongSelectHumanStage"); Assert.NotNull(stage);
        texture.Release(); model.SetPose(2);
        Assert.True(texture.IsCreated(), "描画用テクスチャが失われたら再確保する");
        Assert.AreSame(stage, GameObject.Find("SongSelectHumanStage"), "再確保時に人体を重複生成しない");
        var camera = stage.GetComponentInChildren<Camera>(); Assert.NotNull(camera); Assert.False(camera.enabled);
        Assert.AreEqual(originalMask, Camera.main.cullingMask);
        guide.RegisterActivity(); Assert.False(stage.activeSelf);
        Object.Destroy(model.gameObject); yield return null; yield return null;
        Assert.True(texture == null); Assert.True(stage == null);
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
