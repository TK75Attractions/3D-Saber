using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 2026-10-27 譜面の印(プレイ中の F5)と、結果画面で印ごとに打つ一言。書き出す中身と、一言の欄の動き。
public class ChartMarkTests
{
    string folder;

    [SetUp]
    public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "ChartMarkTests_" + Guid.NewGuid().ToString("N"));
        ChartMarks.FolderOverride = folder;
        ChartMarks.BeginPlay();
        InputManager.EscapeSuppressed = false;
    }

    [TearDown]
    public void TearDown()
    {
        if (ChartMarkCommentPanel.Current != null) ChartMarkCommentPanel.Current.Close();
        ChartMarks.BeginPlay();
        ChartMarks.FolderOverride = null;
        InputManager.EscapeSuppressed = false;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static ChartData Chart()
    {
        var chart = new ChartData { bpm = 120, beatZeroMs = 0 };
        for (int i = 0; i < 20; i++)
            chart.notes.Add(new NoteData { time = 500 * i, beat = i, x = i % 2 == 0 ? -1 : 1, color = i % 2 == 0 ? "blue" : "red" });
        return chart;
    }

    [Test]
    public void AMarkRecordsWhereItIsAndTheNotesAroundIt()
    {
        var chart = Chart();
        var recent = new[] { new ChartMarkJudgment { chartMs = 3500, hand = "Right", result = "Miss" } };
        var entry = ChartMarks.Add(chart, 4.25, .25, recent, "Morning", "Morning", "M", "projector=1",
            new DateTime(2026, 10, 27, 22, 30, 5));
        Assert.NotNull(entry);
        Assert.AreEqual(1, entry.number);
        Assert.AreEqual(4000, entry.chartMs, 1e-6, "曲の時計からずらしを引いた譜面の時刻(ノーツの time と同じ物差し)");
        Assert.AreEqual(8, entry.beat, 1e-6);
        Assert.AreEqual(3, entry.bar, "4拍で1小節。8拍目は3小節目の頭");
        // 気づいてから押すので前を長く(前4秒・後2秒)。0〜6000ms の13個。
        CollectionAssert.AreEqual(Enumerable.Range(0, 13).ToArray(), entry.notes.Select(n => n.index).ToArray());
        Assert.AreEqual("Miss", entry.recent.Single().result);
        Assert.AreEqual("20261027-223005_Morning_M-1", entry.id);
        Assert.AreEqual("20261027-223005_Morning_M_1.jpg", entry.screenshot, "画面は縮めた JPEG");
        Assert.AreEqual("Songs/Morning/chart_m.json", ChartMarks.Current.chartFile);
        Assert.AreEqual(64, ChartMarks.Current.chartSha256.Length, "譜面の指紋で、あとから譜面が変わったかが分かる");
        Assert.AreEqual(GameVersion.Number, ChartMarks.Current.gameVersion);

        var second = ChartMarks.Add(chart, 9.25, .25, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 22, 30, 9));
        Assert.AreEqual(2, second.number);
        Assert.AreEqual(1, Directory.GetFiles(folder, "*.json").Length, "1プレイは1ファイル");
        var saved = JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(ChartMarks.CurrentPath));
        Assert.AreEqual(2, saved.marks.Count);
        Assert.AreEqual("open", saved.marks[0].status);
        Assert.AreEqual("M", saved.difficulty);
        Assert.AreEqual(13, saved.marks[0].notes.Count);

        ChartMarks.BeginPlay();
        ChartMarks.Add(chart, 1, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 22, 40, 0));
        Assert.AreEqual(2, Directory.GetFiles(folder, "*.json").Length, "次のプレイは別のファイル");
        Assert.IsNull(ChartMarks.Add(chart, double.NaN, 0, null, "Morning", "Morning", "M", "", DateTime.Now));
    }

    [Test]
    public void ChartFileFollowsTheLoaderAndTextIsTidied()
    {
        Assert.AreEqual("Songs/Morning/chart_m.json", ChartMarks.ChartFileFor("Morning", "M"));
        Assert.AreEqual("Songs/Morning/chart_normal.json", ChartMarks.ChartFileFor("Morning", "Normal"));
        Assert.AreEqual("Songs/Morning/chart.json", ChartMarks.ChartFileFor("Morning", "Nope"), "ChartLoader と同じく chart.json へ");
        Assert.AreEqual("0:04.6", ChartMarks.Clock(4.62));
        Assert.AreEqual("1:05.0", ChartMarks.Clock(65));
        Assert.AreEqual(1, ChartMarks.BarOf(0));
        Assert.AreEqual(0, ChartMarks.BarOf(-.5), "カウントイン中は0小節目");
        Assert.AreEqual("あいう", ChartMarks.Clean(" あいう\n"));
        Assert.AreEqual(ChartMarks.CommentLimit, ChartMarks.Clean(new string('あ', 500)).Length);
    }

    [Test]
    public void CommentPanelSavesEachMarksWordsAndReleasesEscape()
    {
        var chart = Chart();
        ChartMarks.Add(chart, 4, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 23, 0, 0));
        ChartMarks.Add(chart, 8, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 23, 0, 4));
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        Assert.NotNull(panel);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen);
        Assert.IsTrue(InputManager.EscapeSuppressed, "日本語入力の取り消しの Esc でタイトルへ戻らない");
        Assert.IsFalse(InputManager.EscapeGetDown);
        Assert.AreEqual(2, panel.Count);
        Assert.AreSame(panel, ChartMarkCommentPanel.Open(ChartMarks.Current), "二重に開かない");

        panel.Type("1小節目のノーツいらない");
        panel.DeleteLast();
        panel.Type("い");
        panel.Submit();
        Assert.AreEqual(1, panel.Row);
        panel.Type("おそく");
        panel.Submit();   // 最後の行の Enter で閉じる
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen);
        Assert.IsFalse(InputManager.EscapeSuppressed, "閉じたら Esc を戻す");

        var saved = JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(ChartMarks.CurrentPath));
        Assert.AreEqual("1小節目のノーツいらない", saved.marks[0].comment);
        Assert.AreEqual("おそく", saved.marks[1].comment);
    }

    [Test]
    public void MarksStopAtTheLimitAndAFailedSaveIsReported()
    {
        var chart = Chart();
        for (int i = 0; i < ChartMarks.MaxMarksPerPlay; i++)
            Assert.NotNull(ChartMarks.Add(chart, 1 + i * .1, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 23, 10, 0)));
        Assert.IsTrue(ChartMarks.Full);
        Assert.IsNull(ChartMarks.Add(chart, 9, 0, null, "Morning", "Morning", "M", "", DateTime.Now), "1プレイの印は上限まで");
        Assert.IsEmpty(ChartMarks.LastSaveError);
        Assert.IsFalse(File.Exists(ChartMarks.CurrentPath + ".tmp"), "一時ファイルを残さない");

        // 置き場所がファイルで塞がれている → 保存は失敗し、理由が残る。それまでのファイルは壊さない。
        ChartMarks.BeginPlay();
        string blocked = Path.Combine(folder, "blocked");
        File.WriteAllText(blocked, "not a folder");
        ChartMarks.FolderOverride = blocked;
        var entry = ChartMarks.Add(chart, 2, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 23, 20, 0));
        Assert.NotNull(entry);
        Assert.IsNotEmpty(ChartMarks.LastSaveError, "保存に失敗したことが分かる");
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        panel.Type("消して");
        panel.Close();
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen, "保存できなければ、すぐには閉じずに知らせる");
        Assert.IsTrue(panel.SaveFailed);
        panel.Close();
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen, "もう一度閉じると、保存せずに閉じる");
        Assert.IsFalse(InputManager.EscapeSuppressed);
        ChartMarks.FolderOverride = folder;
    }

    [Test]
    public void CommentPanelOpensOnlyWhenThereAreMarks()
    {
        Assert.IsNull(ChartMarkCommentPanel.Open(null));
        Assert.IsNull(ChartMarkCommentPanel.Open(new ChartMarkFile()));
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen);
        Assert.AreEqual(UnityEngine.InputSystem.Key.F5, ChartMarkRecorder.MarkKey, "運営のキー(F1〜F12)の空き。来場者のキーと重ならない");
    }
}
