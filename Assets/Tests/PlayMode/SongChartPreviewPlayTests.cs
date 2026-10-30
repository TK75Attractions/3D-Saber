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
            if(controller!=null && Object.FindFirstObjectByType<SongSelectSkin>()?.IsReady == true)
            {
                var mount = new GameObject("OptionalPreviewTest", typeof(RectTransform));
                mount.transform.SetParent(controller.transform, false);
                controller.AttachChartPreview(mount.GetComponent<RectTransform>());
                yield break;
            }
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
    public IEnumerator LatestSelectionWaitsBrieflyAndDifficultyKeepsSyncedPreview()
    {
        yield return Open(); controller.Select(Index("ElDorado"));
        yield return new WaitForSecondsRealtime(.15f);
        controller.Select(Index("Epilogue")); controller.SetDifficulty(1);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsFalse(controller.previewSource.isPlaying); Assert.IsFalse(controller.ChartPreview.View.IsVisible);
        yield return AwaitPlaying();
        var preview=controller.ChartPreview;
        Assert.AreEqual("Epilogue",preview.SongId); Assert.AreEqual("Normal",preview.Difficulty);
        Assert.GreaterOrEqual(preview.StartedAt-preview.SelectedAt,SongSelectChartPreview.SelectionDelaySeconds);
        // 校歌は全員で歌う斉唱の頭(PDF 第53小節)から(2026-10-30 ユーザー「筑駒の切り取る部分は本家で」→「ふだん歌う校歌の部分」)。
        Assert.That(preview.Window.Start,Is.EqualTo(111.208).Within(.001));
        Assert.That(preview.Window.Start+preview.Window.Duration,Is.EqualTo(120.642).Within(.001));
        Assert.That(controller.previewSource.time,Is.EqualTo(preview.SongTime).Within(.35));
        var world=preview.View.WorldRoot;
        Assert.IsFalse(world.GetComponentsInChildren<Collider>().Any(c=>c.enabled));
        Assert.IsTrue(world.GetComponentsInChildren<CuttableNote>().All(n=>!n.IsJudgeable));
        Assert.Greater(preview.View.ExcerptNoteCount,0);
        Assert.IsTrue(world.GetComponentsInChildren<Renderer>().All(r=>r.gameObject.layer==SongChartPreviewView.PreviewLayer));
        Assert.AreEqual(1<<SongChartPreviewView.PreviewLayer,preview.View.PreviewCamera.cullingMask);
        double start=preview.Window.Start, started=preview.StartedAt, selected=preview.SelectedAt;
        var clip=controller.previewSource.clip;
        int sample=controller.previewSource.timeSamples;
        controller.SetDifficulty(2);
        Assert.IsTrue(preview.IsPlaying); Assert.IsTrue(controller.previewSource.isPlaying); Assert.IsTrue(preview.View.IsVisible);
        Assert.AreSame(clip,controller.previewSource.clip, "音源を破棄・読み直ししない");
        Assert.AreEqual(started,preview.StartedAt); Assert.AreEqual(selected,preview.SelectedAt);
        Assert.GreaterOrEqual(controller.previewSource.timeSamples,sample, "再生位置を巻き戻さない");
        Assert.AreEqual("Hard",preview.Difficulty); Assert.AreEqual(start,preview.Window.Start);
        Assert.That(preview.View.DisplayedSongTime,Is.EqualTo(preview.SongTime).Within(.35));
        Assert.That(controller.previewSource.time,Is.EqualTo(preview.SongTime).Within(.35));
        Assert.IsFalse(preview.TryChangeDifficulty("Morning","Easy",10), "別曲は継続しない");
        Assert.IsFalse(preview.TryChangeDifficulty("Epilogue","E",10), "隠しEへは継続しない");
        Assert.IsFalse(preview.TryChangeDifficulty("Epilogue","Easy",5), "範囲が変われば継続しない");
        controller.ReplayPreview();
        Assert.IsFalse(preview.IsPlaying); Assert.IsNull(controller.previewSource.clip);
        yield return AwaitPlaying();
        Assert.Greater(preview.StartedAt,started, "再試聴の操作では従来どおり最初から鳴らす");
        controller.StopPreview(); yield return null;
        Assert.IsNull(controller.previewSource.clip); Assert.IsFalse(preview.View.IsVisible);
    }

    [UnityTest]
    public IEnumerator PreviewStartsShortlyAfterTheSongIsChosen()
    {
        // 2026-10-30 ユーザー「もっとすぐに流れていい」: 曲が決まってから 0.3 秒(前は 1 秒)待って鳴らす。
        Assert.LessOrEqual(SongSelectChartPreview.SelectionDelaySeconds,.3);
        yield return Open(); controller.Select(Index("Morning"));
        var preview=controller.ChartPreview;
        yield return AwaitPlaying();
        Assert.AreEqual("Morning",preview.SongId);
        Assert.GreaterOrEqual(preview.StartedAt-preview.SelectedAt,SongSelectChartPreview.SelectionDelaySeconds-.05,"速い曲送りでは音源を開かないよう、少し待ってから鳴らす");
    }

    [UnityTest]
    public IEnumerator BarAlignedPreviewFinishesAndSceneExitReleasesWorld()
    {
        yield return Open(); Assert.AreEqual(10,controller.previewDuration);
        controller.Select(Index("揺籠")); controller.SetDifficulty(2);
        yield return AwaitPlaying();
        var preview=controller.ChartPreview;
        Assert.That(preview.Window.Start,Is.EqualTo(140.245).Within(.001));
        Assert.That(preview.Window.Start+preview.Window.Duration,Is.EqualTo(150.726672).Within(.001));
        Assert.That(preview.Window.Duration,Is.InRange(8d,12d));
        double end=preview.Window.Start+preview.Window.Duration;
        double deadline=Time.realtimeSinceStartupAsDouble+preview.Window.Duration+3;
        while(preview.IsPlaying && Time.realtimeSinceStartupAsDouble<deadline) yield return null;
        Assert.AreEqual(SongSelectChartPreview.PreviewState.Complete,preview.State);
        Assert.IsFalse(preview.IsPlaying); Assert.IsFalse(preview.View.IsVisible); Assert.IsNull(controller.previewSource.clip);
        Assert.That(preview.SongTime,Is.EqualTo(end).Within(.35));
        controller.Select(Index("Morning"));
        yield return new WaitForSecondsRealtime(.2f);
        yield return SceneManager.LoadSceneAsync("Title",LoadSceneMode.Single); yield return null;
        Assert.IsNull(Object.FindFirstObjectByType<SongSelectChartPreview>());
        Assert.IsNull(GameObject.Find("SongSelectPreviewWorld"));
    }
}
