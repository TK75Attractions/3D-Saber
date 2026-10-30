using System;
using System.IO;
using NUnit.Framework;

// 来場者の流れをあとで数えるための記録と、範囲合わせのヒント(2026-10-30 夜、改善案⑤4・⑥)。
public class VisitorFlowLogTests
{
    [Test]
    public void RangeHintsChangeEveryThreeSecondsInOrder()
    {
        Assert.AreEqual(4, TutorialOverlay.RangeHints.Length);
        Assert.AreEqual("剣の先を スマホに むけない(点になって 見えない)", TutorialOverlay.RangeHints[0]);
        Assert.AreEqual("赤と青を 重ねない", TutorialOverlay.RangeHints[3]);
        Assert.AreEqual(0, TutorialOverlay.RangeHintIndex(0f));
        Assert.AreEqual(0, TutorialOverlay.RangeHintIndex(2.99f));
        Assert.AreEqual(1, TutorialOverlay.RangeHintIndex(3f));
        Assert.AreEqual(3, TutorialOverlay.RangeHintIndex(11.99f));
        Assert.AreEqual(0, TutorialOverlay.RangeHintIndex(12f), "4つ出したら最初に戻る");
        Assert.AreEqual(0, TutorialOverlay.RangeHintIndex(-1f));
    }

    [Test]
    public void StaffReturnNamesTheSceneAndTheSong()
    {
        string song = GameSession.SelectedSongId, diff = GameSession.SelectedDifficulty;
        try
        {
            GameSession.SelectedSongId = "Phobia PARTⅠ"; GameSession.SelectedDifficulty = "Hard";
            Assert.AreEqual("how=esc scene=Result song=Phobia_PARTⅠ diff=Hard", StaffReturnLog.Detail("esc", "Result"));
            Assert.AreEqual("how=right-click scene=SongSelect", StaffReturnLog.Detail("right-click", "SongSelect"));
        }
        finally { GameSession.SelectedSongId = song; GameSession.SelectedDifficulty = diff; }
    }

    [Test]
    public void PreviousPlayerLogIsCopiedOnceWithItsTimeAndOldOnesArePruned()
    {
        string root = Path.Combine(Path.GetTempPath(), "PlayerLogArchiveTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string previous = Path.Combine(root, "Player-prev.log");
            string archive = Path.Combine(root, "archive", "player");
            Assert.IsNull(PlayerLogArchive.Archive(previous, archive, 3), "前のログが無ければ何もしない");
            File.WriteAllText(previous, "Tutorial: passed total=100s");
            File.SetLastWriteTimeUtc(previous, new DateTime(2026, 10, 30, 8, 32, 8, DateTimeKind.Utc));
            string copied = PlayerLogArchive.Archive(previous, archive, 3);
            Assert.AreEqual(Path.Combine(archive, "Player-20261030-083208.log"), copied);
            Assert.AreEqual("Tutorial: passed total=100s", File.ReadAllText(copied));
            Assert.AreEqual(copied, PlayerLogArchive.Archive(previous, archive, 3));
            Assert.AreEqual(1, Directory.GetFiles(archive).Length, "同じ起動のログは2回写さない");
            foreach (string stamp in new[] { "20261029-010000", "20261029-020000", "20261029-030000" })
                File.WriteAllText(Path.Combine(archive, "Player-" + stamp + ".log"), stamp);
            PlayerLogArchive.Prune(archive, 3);
            CollectionAssert.AreEquivalent(new[] { "Player-20261029-020000.log", "Player-20261029-030000.log", "Player-20261030-083208.log" },
                Array.ConvertAll(Directory.GetFiles(archive), Path.GetFileName));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
