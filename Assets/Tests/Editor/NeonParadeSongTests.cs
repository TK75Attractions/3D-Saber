using System.IO;
using NUnit.Framework;
using UnityEngine;

// 自作EDM「NeonParade」(dev/tools/SongForge で作曲・譜面生成)の組み込みを実データで検証する。
// 譜面の中身(ノーツ数・配置)はエディターで編集されうるので縛らず、音源との時間の整合だけを見る。
public class NeonParadeSongTests
{
    const string SongId = "NeonParade";
    // audio.ogg のデコード長(5375790 サンプル / 44100Hz)
    const double AudioSeconds = 121.9;

    [Test]
    public void SongIsListedPlayableAndTitled()
    {
        CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), SongId);
        Assert.IsTrue(SongSelectController.HasPlayableChart(SongId, SongSelectController.StandardDifficulties));
        Assert.AreEqual("NEON PARADE", ResultSkin.SongIdToDisplayTitle(SongId));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(Application.streamingAssetsPath, "Songs", SongId), "audio.ogg").Length);
    }

    [TestCase("easy")]
    [TestCase("normal")]
    [TestCase("hard")]
    public void ChartUsesTheAudioGridAndFitsInsideTheAudio(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", SongId, "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), path);
        var chart = ChartLoader.LoadFromStreamingAssets(SongId, difficulty);
        // 音源は BPM130 固定・0秒 = 1小節目の1拍目で作ってある(カウントの START と同時)
        Assert.AreEqual(130f, chart.bpm, 0.001f);
        Assert.AreEqual(0f, chart.beatZeroMs, 0.001f);
        Assert.AreEqual(0f, chart.offsetMs, 0.001f);
        Assert.That(chart.displayLevel, Is.InRange(1, 10));
        Assert.Greater(chart.notes.Count, 0);
        foreach (var n in chart.notes)
            Assert.Less((n.time + chart.offsetMs + n.lengthMs) / 1000.0, AudioSeconds, "音源の終わりより前に収まる");
    }
}
