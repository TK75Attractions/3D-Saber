using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Morning(Sky Sanctuary)の明るい青灰色の空で青ノーツが沈む件(2026-10-21)。
// 本物の Game シーンで、青(左手)ノーツだけに暗い縁が付き、赤・金には付かないことを、通常表示とプロジェクターモードで確かめる。
// 環境変数 SABER_UI_CATALOG_CAPTURE_DIR があれば、縁あり/なしの画面を撮る(UICatalogPlayTests.Capture)。
public class MorningBlueNoteOutlinePlayTests
{
    // 遠い上段(空の前)から近い下段(床の前)まで。青は 5 つ(タップ・矢印・ロング)。
    const string Notes = "{\"bpm\":118,\"coordScale\":1,\"notes\":[" +
        "{\"time\":350,\"x\":-1.6,\"y\":-0.8,\"count\":1,\"color\":\"blue\"}," +
        "{\"time\":550,\"x\":1.6,\"y\":-0.8,\"count\":1,\"color\":\"red\"}," +
        "{\"time\":800,\"x\":-0.6,\"y\":0.8,\"count\":1,\"type\":\"direction\",\"direction\":\"down\",\"color\":\"blue\"}," +
        "{\"time\":1000,\"x\":0.9,\"y\":0.0,\"count\":2,\"lengthMs\":800,\"type\":\"long\",\"color\":\"blue\"}," +
        "{\"time\":1200,\"x\":0.0,\"y\":-0.4,\"count\":1,\"color\":\"gold\"}," +
        "{\"time\":1400,\"x\":-2.0,\"y\":0.8,\"count\":1,\"color\":\"blue\"}," +
        "{\"time\":1600,\"x\":2.0,\"y\":0.8,\"count\":1,\"color\":\"red\"}," +
        "{\"time\":1800,\"x\":0.6,\"y\":1.45,\"count\":1,\"color\":\"blue\"}," +
        "{\"time\":1950,\"x\":-1.0,\"y\":1.45,\"count\":1,\"color\":\"red\"}]}";

    [UnityTest]
    [Timeout(120000)]
    public IEnumerator BlueNotesOnTheBrightSkyGetADarkOutline()
    {
        string oldSong = GameSession.SelectedSongId, oldDifficulty = GameSession.SelectedDifficulty;
        bool oldCalibration = GameSession.IsCalibrationMode, oldTutorial = GameSession.TutorialPending;
        bool oldProjector = DisplaySettings.ProjectorMode;
        try
        {
            foreach (bool projector in new[] { false, true })
            {
                DisplaySettings.SetProjectorModeForTest(projector);
                GameSession.SelectedSongId = "Morning"; GameSession.SelectedDifficulty = "normal";
                GameSession.IsCalibrationMode = false; GameSession.TutorialPending = false;
                yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
                var manager = Object.FindFirstObjectByType<GamePlayManager>(); Assert.NotNull(manager);
                var spawner = manager.noteSpawner; Assert.NotNull(spawner);
                float deadline = Time.realtimeSinceStartup + 25;
                while (spawner.TotalNoteCount == 0 && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.Greater(spawner.TotalNoteCount, 0);
                Assert.AreEqual(StageTheme.SkySanctuary, Object.FindFirstObjectByType<FloorRenderer>().ActiveTheme);
                Assert.IsTrue(spawner.outlineLeftNotes, "Morning は青ノーツに縁を付ける");

                // 曲を止め、決まった並びのノーツを置く(ScenicWorldsPreview と同じ止め方)。
                manager.StopAllCoroutines(); manager.enabled = false; manager.songPlayer.Stop();
                foreach (var countdown in Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None)) countdown.gameObject.SetActive(false);
                foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
                spawner.approachTime = 2; spawner.SetExtraOffsetSeconds(0);
                spawner.SetChart(ChartLoader.Parse(Notes)); spawner.Tick(0);
                yield return null; yield return null;

                var notes = Object.FindObjectsByType<CuttableNote>(FindObjectsSortMode.None)
                    .Where(n => n.gameObject.activeInHierarchy).ToArray();
                Assert.AreEqual(5, notes.Count(n => n.RequiredHand == SaberHand.Left), "青は 5 つ");
                Assert.AreEqual(4, notes.Count(n => n.RequiredHand != SaberHand.Left), "赤 3 つと金 1 つ");
                foreach (var note in notes)
                    Assert.AreEqual(note.RequiredHand == SaberHand.Left, note.GetComponent<NoteVisuals>().ContrastOutlineShown,
                        $"{note.RequiredHand} {note.RequiredDirection} {note.RequiredCutCount}");

                string mode = projector ? "projector" : "normal";
                UICatalogPlayTests.Capture("morning-outline-" + mode);
                foreach (var note in notes) note.GetComponent<NoteVisuals>().SetContrastOutline(false);
                UICatalogPlayTests.Capture("morning-no-outline-" + mode);
            }
        }
        finally
        {
            DisplaySettings.SetProjectorModeForTest(oldProjector);
            ProjectorMode.Apply(Camera.main);
            GameSession.SelectedSongId = oldSong; GameSession.SelectedDifficulty = oldDifficulty;
            GameSession.IsCalibrationMode = oldCalibration; GameSession.TutorialPending = oldTutorial;
        }
    }
}
