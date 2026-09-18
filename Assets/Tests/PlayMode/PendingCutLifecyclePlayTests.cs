using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class PendingCutLifecyclePlayTests
{
    Scene previousScene, testScene;

    [SetUp]
    public void Setup()
    {
        previousScene = SceneManager.GetActiveScene();
        testScene = SceneManager.CreateScene("PendingCut_" + Guid.NewGuid().ToString("N"));
        SceneManager.SetActiveScene(testScene);
    }

    [UnityTearDown]
    public IEnumerator Cleanup()
    {
        SceneManager.SetActiveScene(previousScene);
        yield return SceneManager.UnloadSceneAsync(testScene);
    }

    [UnityTest]
    public IEnumerator DisableAndEnableDropsOldContactButAcceptsANewCut()
    {
        foreach (bool blade in new[] { false, true })
        {
            var root = new GameObject("PendingCutRig");
            var tracker = root.AddComponent<SaberTracker>(); tracker.enabled = false;
            var bridge = root.AddComponent<SaberInputBridge>(); bridge.useBladeMode = false; bridge.enabled = false;
            var judge = root.AddComponent<SaberCutJudge>(); judge.saber = tracker; judge.autonomous = false;
            judge.bladeProvider = blade ? bridge : null;
            var noteObject = new GameObject("PendingNote");
            var note = noteObject.AddComponent<CuttableNote>(); note.IsJudgeable = true;
            tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .05f);
            if (blade) bridge.OverrideBlade(Vector3.left, Vector3.right);
            Assert.AreEqual(0, judge.TryCut()); Assert.AreEqual(1, judge.PendingCount);
            judge.enabled = false;
            Assert.AreEqual(0, judge.PendingCount, "実際のOnDisableで古い進入を破棄");
            yield return null;
            tracker.ResetTo(new Vector3(2, 0, 0)); tracker.Tick(new Vector3(2, 0, 0), .05f);
            if (blade) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
            judge.enabled = true;
            Assert.AreEqual(0, judge.TryCut()); Assert.False(note.IsCut);
            tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .05f);
            if (blade) bridge.OverrideBlade(Vector3.left, Vector3.right);
            Assert.AreEqual(0, judge.TryCut());
            // Trackerだけ再開し、次のサンプルが先に届いても以前の接触を持ち越さない。
            tracker.enabled = true; tracker.enabled = false;
            tracker.Tick(new Vector3(2, 0, 0), .05f); tracker.Tick(new Vector3(2, 0, 0), .05f);
            if (blade) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
            Assert.AreEqual(0, judge.TryCut()); Assert.False(note.IsCut);
            tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .05f);
            if (blade) bridge.OverrideBlade(Vector3.left, Vector3.right);
            Assert.AreEqual(0, judge.TryCut());
            tracker.Tick(new Vector3(2, 0, 0), .05f);
            if (blade) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
            Assert.AreEqual(1, judge.TryCut()); Assert.True(note.IsCut);
            UnityEngine.Object.Destroy(root); UnityEngine.Object.Destroy(noteObject);
            yield return null;
        }
    }
}
