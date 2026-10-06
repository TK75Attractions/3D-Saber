using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using System.IO;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class ElDoradoShortAudioPlayTests
{
    [UnityTest]
    public IEnumerator ActualGameUsesTheShortClipAndItsNewPreviewWindow()
    {
        string song = GameSession.SelectedSongId, difficulty = GameSession.SelectedDifficulty;
        bool calibration = GameSession.IsCalibrationMode;
        try
        {
            GameSession.SelectedSongId = "ElDorado";
            GameSession.SelectedDifficulty = "normal";
            GameSession.IsCalibrationMode = false;
            yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            Assert.NotNull(manager);
            double deadline = Time.realtimeSinceStartupAsDouble + 30;
            while (manager.songPlayer.Clip == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.NotNull(manager.songPlayer.Clip);
            // 任意の外部QA出力先があるときだけ、実際のデコーダーのPCMを照合用に保存する。
            string qa = System.Environment.GetEnvironmentVariable("ELDORADO_SHORT_QA_DIR");
            if (!string.IsNullOrEmpty(qa))
            {
                Directory.CreateDirectory(qa);
                var clip = manager.songPlayer.Clip;
                File.WriteAllText(Path.Combine(qa, "format.txt"), clip.frequency + "," + clip.channels);
                foreach (int seconds in new[] { 2, 20, 60, 100, 120, 138 })
                {
                    var samples = new float[clip.frequency * clip.channels];
                    Assert.True(clip.GetData(samples, seconds * clip.frequency));
                    using (var writer = new BinaryWriter(File.Create(Path.Combine(qa, seconds + ".f32"))))
                        foreach (float sample in samples) writer.Write(sample);
                }
            }
            // MPEGデコーダーは末尾の割当分を含み144.77秒と報告する。PCM位置は6区間で元音源と照合する。
            Assert.That(manager.songPlayer.Duration, Is.InRange(144.0, 145.0), "旧223秒版や途中までの音源を読んでいない");
            var stage = StagePerformanceTimeline.Load("ElDorado");
            var window = SongPreviewWindow.Resolve(stage, manager.songPlayer.Duration);
            Assert.That(window.Start, Is.EqualTo(100.203).Within(.001));
            Assert.Less(window.Start + window.Duration, manager.songPlayer.Duration);
            foreach (var d in new[] { "easy", "normal", "hard" })
            {
                var chart = ChartLoader.LoadFromStreamingAssets("ElDorado", d);
                Assert.True(chart.notes.Any(n => SongPreviewWindow.Intersects(chart, n, window, 0)), d);
                foreach (var n in chart.notes)
                {
                    double length = n.count > 1 ? (n.lengthMs > 0 ? n.lengthMs / 1000.0 : (n.count - 1) * .7) : 0;
                    Assert.Less(n.time / 1000.0 + length, manager.songPlayer.Duration, d);
                }
            }
            foreach (var s in stage.sections) Assert.Less(s.endSeconds, manager.songPlayer.Duration);
            manager.songPlayer.Stop();
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        }
        finally
        {
            GameSession.SelectedSongId = song;
            GameSession.SelectedDifficulty = difficulty;
            GameSession.IsCalibrationMode = calibration;
        }
    }
}
