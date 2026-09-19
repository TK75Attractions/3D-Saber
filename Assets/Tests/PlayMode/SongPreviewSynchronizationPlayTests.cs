using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public class SongPreviewSynchronizationPlayTests
{
    [UnityTest]
    public IEnumerator DelayedPreviewStartUsesActualAudioPosition()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var controller = Object.FindFirstObjectByType<SongSelectController>();
        Assert.NotNull(controller);
        double deadline = Time.realtimeSinceStartupAsDouble + 15;
        while ((controller.SongCount == 0 || controller.ChartPreview?.View == null)
            && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        controller.Select(Enumerable.Range(0, controller.SongCount).Single(i => controller.SongIdAt(i) == "Epilogue"));
        deadline = Time.realtimeSinceStartupAsDouble + 15;
        while (!controller.ChartPreview.IsPlaying && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        var preview = controller.ChartPreview;
        Assert.True(preview.IsPlaying);
        // 短い開始予約をまたぐ描画負荷でも、ずれが曲の途中まで残らない。
        System.Threading.Thread.Sleep(500);
        while (controller.previewSource.timeSamples / (double)controller.previewSource.clip.frequency < preview.Window.Start + .1
            && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        AudioSettings.GetDSPBufferSize(out int frames, out _);
        for (int frame = 0; frame < 20; frame++)
        {
            double time = preview.SongTime;
            double actual = controller.previewSource.timeSamples / (double)controller.previewSource.clip.frequency;
            Assert.That(time, Is.EqualTo(actual).Within(frames / (double)AudioSettings.outputSampleRate + .004));
            yield return null;
        }
        controller.StopPreview();
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
    }

    [UnityTest]
    public IEnumerator EverySongPreviewTracksItsDecodedAudio()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var controller = Object.FindFirstObjectByType<SongSelectController>();
        double deadline = Time.realtimeSinceStartupAsDouble + 15;
        while ((controller.SongCount == 0 || controller.ChartPreview?.View == null)
            && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        string[] songs = { "2_23_AM", "Andalusia", "ElDorado", "Epilogue", "Morning", "揺籠" };
        AudioSettings.GetDSPBufferSize(out int frames, out _);
        foreach (string id in songs)
        {
            controller.Select(Enumerable.Range(0, controller.SongCount).Single(i => controller.SongIdAt(i) == id));
            deadline = Time.realtimeSinceStartupAsDouble + 15;
            while ((!controller.ChartPreview.IsPlaying || !controller.ChartPreview.View.IsVisible)
                && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.True(controller.ChartPreview.IsPlaying, id);
            Assert.True(controller.ChartPreview.View.IsVisible, id);
            for (int frame = 0; frame < 12; frame++)
            {
                double time = controller.ChartPreview.SongTime;
                double actual = controller.previewSource.timeSamples / (double)controller.previewSource.clip.frequency;
                Assert.That(time, Is.EqualTo(actual).Within(frames / (double)AudioSettings.outputSampleRate + .004), id);
                yield return null;
            }
            controller.StopPreview();
        }
        yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
    }

    [UnityTest]
    public IEnumerator PreviewArrivalIncludesChartAndPlayerOffsetsExactlyOnce()
    {
        const string key = "judgmentOffsetMs";
        bool had = PlayerPrefs.HasKey(key);
        int saved = PlayerPrefs.GetInt(key);
        var mount = new GameObject("PreviewSynchronizationTest", typeof(RectTransform));
        SongChartPreviewView view = null;
        try
        {
            GameSession.JudgmentOffsetMs = 100;
            view = new SongChartPreviewView(mount.GetComponent<RectTransform>());
            var chart = new ChartData { bpm = 120, offsetMs = 125 };
            chart.notes.Add(new NoteData { time = 1000, color = "blue", x = 0, y = 0 });
            view.Prepare(chart, new SongPreviewWindow(0, 3), new StagePerformanceTimeline(), "Normal");
            view.Tick(1.125);
            var note = view.WorldRoot.GetComponentsInChildren<CuttableNote>().Single();
            Assert.Greater(note.transform.localPosition.z, .1f, "音源上の制作時刻では、個人補正100msのぶん手前に残る");
            view.Tick(1.225);
            Assert.That(note.transform.localPosition.z, Is.EqualTo(0).Within(.001));
            Assert.AreEqual(1000, chart.notes[0].time, "表示の補正を譜面へ焼き込まない");
            Assert.AreEqual(125, chart.offsetMs);

            // 抜粋直前のノーツも、正の補正で抜粋内へ入るなら表示する。
            view.Prepare(chart, new SongPreviewWindow(1.2, 2), new StagePerformanceTimeline(), "Normal");
            Assert.AreEqual(1, view.ExcerptNoteCount);
            view.Tick(1.225);
            Assert.That(view.WorldRoot.GetComponentsInChildren<CuttableNote>().Last().transform.localPosition.z, Is.EqualTo(0).Within(.001));
        }
        finally
        {
            view?.Dispose();
            Object.Destroy(mount);
            if (had) PlayerPrefs.SetInt(key, saved); else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
        yield return null;
    }
}
