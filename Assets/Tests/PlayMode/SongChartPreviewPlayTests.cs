using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class SongChartPreviewPlayTests
{
    private SongSelectController controller;
    private IEnumerator Open()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect",LoadSceneMode.Single);
        float deadline=Time.realtimeSinceStartup+15;
        while(Time.realtimeSinceStartup<deadline)
        {
            controller=Object.FindFirstObjectByType<SongSelectController>();
            if(controller!=null && controller.ChartPreview?.View!=null) yield break;
            yield return null;
        }
        Assert.Fail("選曲プレビューの表示領域が生成されません。");
    }
    private int Index(string id) => Enumerable.Range(0,controller.SongCount).Single(i=>controller.SongIdAt(i)==id);
    private IEnumerator AwaitPlaying()
    {
        float deadline=Time.realtimeSinceStartup+10;
        while(Time.realtimeSinceStartup<deadline && (!controller.ChartPreview.IsPlaying || !controller.ChartPreview.View.IsVisible)) yield return null;
        Assert.IsTrue(controller.ChartPreview.IsPlaying);
        Assert.IsTrue(controller.ChartPreview.View.IsVisible);
    }

    [UnityTest]
    public IEnumerator LatestSelectionWaitsOneSecondAndDifficultyRestartsSyncedPreview()
    {
        yield return Open(); controller.Select(Index("ElDorado"));
        yield return new WaitForSecondsRealtime(.2f);
        controller.Select(Index("Epilogue")); controller.SetDifficulty(1);
        yield return new WaitForSecondsRealtime(.75f);
        Assert.IsFalse(controller.previewSource.isPlaying); Assert.IsFalse(controller.ChartPreview.View.IsVisible);
        yield return AwaitPlaying();
        var preview=controller.ChartPreview;
        Assert.AreEqual("Epilogue",preview.SongId); Assert.AreEqual("Normal",preview.Difficulty);
        Assert.GreaterOrEqual(preview.StartedAt-preview.SelectedAt,1);
        Assert.That(preview.Window.Start,Is.EqualTo(133.859).Within(.001));
        Assert.That(controller.previewSource.time,Is.EqualTo(preview.SongTime).Within(.35));
        var world=preview.View.WorldRoot;
        Assert.IsFalse(world.GetComponentsInChildren<Collider>().Any(c=>c.enabled));
        Assert.IsTrue(world.GetComponentsInChildren<CuttableNote>().All(n=>!n.IsJudgeable));
        Assert.Greater(preview.View.ExcerptNoteCount,0);
        Assert.IsTrue(world.GetComponentsInChildren<Renderer>().All(r=>r.gameObject.layer==SongChartPreviewView.PreviewLayer));
        Assert.AreEqual(1<<SongChartPreviewView.PreviewLayer,preview.View.PreviewCamera.cullingMask);
        double start=preview.Window.Start;
        controller.SetDifficulty(2);
        Assert.IsFalse(preview.IsPlaying); Assert.IsFalse(controller.previewSource.isPlaying); Assert.IsFalse(preview.View.IsVisible);
        yield return AwaitPlaying();
        Assert.AreEqual("Hard",preview.Difficulty); Assert.AreEqual(start,preview.Window.Start);
        Assert.That(controller.previewSource.time,Is.EqualTo(preview.SongTime).Within(.35));
        controller.StopPreview(); yield return null;
        Assert.IsNull(controller.previewSource.clip); Assert.IsFalse(preview.View.IsVisible);
    }

    [UnityTest]
    public IEnumerator TenSecondPreviewFinishesAndSceneExitReleasesWorld()
    {
        yield return Open(); Assert.AreEqual(10,controller.previewDuration);
        controller.Select(Index("揺籠")); controller.SetDifficulty(2);
        yield return AwaitPlaying();
        var preview=controller.ChartPreview;
        Assert.AreEqual(10,preview.Window.Duration);
        float deadline=Time.realtimeSinceStartup+12;
        while(preview.IsPlaying && Time.realtimeSinceStartup<deadline) yield return null;
        Assert.IsFalse(preview.IsPlaying); Assert.IsFalse(preview.View.IsVisible); Assert.IsNull(controller.previewSource.clip);
        Assert.That(preview.SongTime-preview.Window.Start,Is.GreaterThanOrEqualTo(10));
        controller.Select(Index("Morning"));
        yield return new WaitForSecondsRealtime(.2f);
        yield return SceneManager.LoadSceneAsync("Title",LoadSceneMode.Single); yield return null;
        Assert.IsNull(Object.FindFirstObjectByType<SongSelectChartPreview>());
        Assert.IsNull(GameObject.Find("SongSelectPreviewWorld"));
    }
}
