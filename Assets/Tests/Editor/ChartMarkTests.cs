using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 2026-10-27 譜面の印(プレイ中の F5)と、結果画面で印ごとに打つ一言。書き出す中身と、一言の欄の動き。
// 2026-10-28 format 2(台名・1プレイの ID・保存先・OS・source・取り消し・ノーツの全項目・拍子の表の小節)、
// スマホが使う一覧と書き換え(Recent・SetComment・SetCanceled・OpenCountOnThisPc)、一言の欄の下書き・時間切れ・取り消し。
public class ChartMarkTests
{
    string folder;
    string previousPhoneUrl, previousRunId;

    [SetUp]
    public void SetUp()
    {
        previousRunId = GameSession.AchievementRunId;
        GameSession.AchievementRunId = null;
        folder = Path.Combine(Path.GetTempPath(), "ChartMarkTests_" + Guid.NewGuid().ToString("N"));
        ChartMarks.FolderOverride = folder;
        ChartMarks.BeginPlay();
        ChartMarks.ForgetUnsaved();
        InputManager.EscapeSuppressed = false;
        PhoneSaberOperatorKeys.TextEntryActive = false;
        previousPhoneUrl = ChartMarkPhoneLink.Url;
        ChartMarkPhoneLink.Url = null;
    }

    [TearDown]
    public void TearDown()
    {
        if (ChartMarkCommentPanel.Current != null) ChartMarkCommentPanel.Current.Close();
        if (ChartMarkCommentPanel.Current != null) ChartMarkCommentPanel.Current.Close();
        ChartMarks.BeginPlay();
        ChartMarks.ForgetUnsaved();
        ChartMarks.FolderOverride = null;
        InputManager.EscapeSuppressed = false;
        PhoneSaberOperatorKeys.TextEntryActive = false;
        ChartMarkPhoneLink.Url = previousPhoneUrl;
        GameSession.AchievementRunId = previousRunId;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static ChartData Chart()
    {
        var chart = new ChartData { bpm = 120, beatZeroMs = 0 };
        for (int i = 0; i < 20; i++)
            chart.notes.Add(new NoteData { time = 500 * i, beat = i, x = i % 2 == 0 ? -1 : 1, color = i % 2 == 0 ? "blue" : "red" });
        return chart;
    }

    static ChartMarkEntry Mark(ChartData chart, double songTime, DateTime now, string station = "A", string run = "1a2b3c4d5e6f")
        => ChartMarks.Add(chart, songTime, 0, null, "Morning", "Morning", "M", "", now, station: station, runId: run);

    static ChartMarkFile Saved(string path) => JsonUtility.FromJson<ChartMarkFile>(File.ReadAllText(path));

    [Test]
    public void AMarkRecordsWhereItIsAndTheNotesAroundIt()
    {
        var chart = Chart();
        var recent = new[] { new ChartMarkJudgment { chartMs = 3500, hand = "Right", result = "Miss" } };
        var entry = ChartMarks.Add(chart, 4.25, .25, recent, "Morning", "Morning", "M", "projector=1",
            new DateTime(2026, 10, 27, 22, 30, 5), station: "A", runId: "1a2b3c4d5e6f");
        Assert.NotNull(entry);
        Assert.AreEqual(1, entry.number);
        Assert.AreEqual(4000, entry.chartMs, 1e-6, "曲の時計からずらしを引いた譜面の時刻(ノーツの time と同じ物差し)");
        Assert.AreEqual(8, entry.beat, 1e-6);
        Assert.AreEqual(3, entry.bar, "拍子の表が無い譜面は4拍で1小節。8拍目は3小節目の頭");
        // 気づいてから押すので前を長く(前4秒・後2秒)。0〜6000ms の13個。
        CollectionAssert.AreEqual(Enumerable.Range(0, 13).ToArray(), entry.notes.Select(n => n.index).ToArray());
        Assert.AreEqual("Miss", entry.recent.Single().result);
        Assert.AreEqual("20261027-223005_A_Morning_M_1a2b3c4d-1", entry.id, "名前は日時・台・曲・難易度・1プレイの ID の先頭8文字");
        Assert.AreEqual("20261027-223005_A_Morning_M_1a2b3c4d_1.jpg", entry.screenshot, "画面は縮めた JPEG");
        Assert.AreEqual("open", entry.status);
        Assert.AreEqual("key", entry.source, "F5 の印");
        Assert.AreEqual("Songs/Morning/chart_m.json", ChartMarks.Current.chartFile);
        Assert.AreEqual(64, ChartMarks.Current.chartSha256.Length, "譜面の指紋で、あとから譜面が変わったかが分かる");
        Assert.AreEqual(GameVersion.Number, ChartMarks.Current.gameVersion);

        var second = ChartMarks.Add(chart, 9.25, .25, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 22, 30, 9),
            ChartMarks.SourcePhone, false);
        Assert.AreEqual(2, second.number);
        Assert.AreEqual("phone", second.source, "スマホの印");
        Assert.AreEqual("", second.screenshot, "画面を撮らない印は空");
        Assert.AreEqual(1, Directory.GetFiles(folder, "*.json").Length, "1プレイは1ファイル");
        var saved = Saved(ChartMarks.CurrentPath);
        Assert.AreEqual(2, saved.format);
        Assert.AreEqual("A", saved.station);
        Assert.AreEqual("1a2b3c4d5e6f", saved.runId);
        Assert.AreEqual(Path.GetFullPath(folder), saved.folder, "保存先のフォルダー(台Aから持ってきても、どこにあったか分かる)");
        Assert.IsNotEmpty(saved.os);
        Assert.AreEqual(2, saved.marks.Count);
        Assert.AreEqual("open", saved.marks[0].status);
        Assert.AreEqual("phone", saved.marks[1].source);
        Assert.AreEqual("M", saved.difficulty);
        Assert.AreEqual(13, saved.marks[0].notes.Count);
        Assert.NotNull(saved.marks[0].notes[0].data, "ノーツの全項目");

        ChartMarks.BeginPlay();
        ChartMarks.Add(chart, 1, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 27, 22, 40, 0), station: "");
        Assert.AreEqual(2, Directory.GetFiles(folder, "*.json").Length, "次のプレイは別のファイル");
        Assert.AreEqual("PC", ChartMarks.Current.station, "台名が無い PC は PC");
        Assert.AreEqual(32, ChartMarks.Current.runId.Length, "1プレイの ID が無いときは GUID");
        StringAssert.StartsWith("20261027-224000_PC_Morning_M_", Path.GetFileName(ChartMarks.CurrentPath));
        Assert.IsNull(ChartMarks.Add(chart, double.NaN, 0, null, "Morning", "Morning", "M", "", DateTime.Now));
    }

    [Test]
    public void NotesKeepEveryFieldAndLongsThatReachIntoTheWindow()
    {
        // 3/4 拍子。印は10秒(20拍目)、窓は 6000〜12000ms。
        var chart = new ChartData { bpm = 120 };
        chart.timeSignatures.Add(new ChartTimeSignature { beat = 0, numerator = 3, denominator = 4 });
        chart.notes.Add(new NoteData { time = 2000, type = "long", count = 2, lengthMs = 1000 });           // 3000ms で終わる
        chart.notes.Add(new NoteData { time = 3000, type = "long", count = 4, lengthMs = 3500 });           // 6500ms まで残る
        chart.notes.Add(new NoteData { time = 5000, type = "long", count = 3 });                            // 1回0.7秒 × 2
        chart.notes.Add(new NoteData { time = 5900 });
        chart.notes.Add(new NoteData { time = 9000, x = .6f, mirror = true, windX = .5f, vanish = "fade", hitScale = .8f, veil = .4f });
        var slide = new NoteData { time = 11000, type = "long", count = 2, lengthMs = 800, slide = true };
        slide.slidePath.Add(new SlidePathPoint { ms = 400, x = 1, y = 0 });
        chart.notes.Add(slide);
        chart.notes.Add(new NoteData { time = 12500 });

        var entry = Mark(chart, 10, new DateTime(2026, 10, 28, 9, 0, 0));
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 5 }, entry.notes.Select(n => n.index).ToArray(),
            "窓より前に始まっても窓にかかるロングは入れる。終わったロング・窓の外は入れない");
        CollectionAssert.AreEqual(new[] { 3500f, 1400f, 0f, 800f }, entry.notes.Select(n => n.lingerMs).ToArray());
        var mirrored = entry.notes[2].data;
        Assert.IsTrue(mirrored.mirror);
        Assert.AreEqual(.5f, mirrored.windX);
        Assert.AreEqual("fade", mirrored.vanish);
        Assert.AreEqual(.8f, mirrored.hitScale);
        Assert.AreEqual(.4f, mirrored.veil);
        Assert.AreEqual(1, entry.notes[3].data.slidePath.Count);
        chart.notes[4].mirror = false;
        Assert.IsTrue(entry.notes[2].data.mirror, "印を付けたときのノーツを写して持つ");
        var saved = Saved(ChartMarks.CurrentPath);
        Assert.IsTrue(saved.marks[0].notes[2].data.mirror, "ファイルにも NoteData の全項目");
        Assert.AreEqual(1, saved.marks[0].notes[3].data.slidePath.Count);

        Assert.AreEqual(20, entry.beat, 1e-6);
        Assert.AreEqual(7, entry.bar, "拍子の表(3/4)で数えた小節。4拍で数えると6小節目になる");
        Assert.AreEqual(4, ChartMarks.BarOf(chart, 9));
        Assert.AreEqual(0, ChartMarks.BarOf(chart, -1), "カウントイン中は0小節目");
        var countIn = ChartMarks.Add(chart, 0, .5, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 28, 9, 0, 1));
        Assert.AreEqual(0, countIn.bar);
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
        Assert.AreEqual("20261028-103012_A_ManicSonic_R_1a2b3c4d",
            ChartMarks.FileStem(new DateTime(2026, 10, 28, 10, 30, 12), "A", "ManicSonic", "R", "1a2b3c4d5e6f7a8b"));
        Assert.AreEqual("PC", ChartMarks.StationName(""));
        Assert.AreEqual("PC", ChartMarks.StationName("台 A!"), "ファイル名に使えない台名は PC");
    }

    [Test]
    public void RecentListsThisPlayFirstAndEditsReachEarlierFiles()
    {
        var chart = Chart();
        var first = Mark(chart, 2, new DateTime(2026, 10, 27, 22, 0, 0), "A", "aaaaaaaa1111");
        var second = Mark(chart, 5, new DateTime(2026, 10, 27, 22, 0, 3), "A", "aaaaaaaa1111");
        string firstPath = ChartMarks.CurrentPath;
        ChartMarks.BeginPlay();
        var now = Mark(chart, 3, new DateTime(2026, 10, 27, 22, 10, 0), "B", "bbbbbbbb2222");

        var list = ChartMarks.Recent(10);
        CollectionAssert.AreEqual(new[] { now.id, second.id, first.id }, list.Select(m => m.id).ToArray(), "新しい順。この回が先");
        Assert.IsTrue(list[0].current);
        Assert.IsFalse(list[1].current);
        Assert.AreEqual("B", list[0].station);
        Assert.AreEqual("A", list[1].station);
        Assert.AreEqual("Morning", list[1].song);
        Assert.AreEqual("M", list[1].difficulty);
        Assert.AreEqual(2, list[1].number);
        Assert.AreEqual(5, list[1].songSeconds, 1e-6);
        Assert.AreEqual(second.bar, list[1].bar);
        Assert.AreEqual("key", list[1].source);
        Assert.AreEqual(second.markedAt, list[1].savedAt);
        Assert.AreEqual(2, ChartMarks.Recent(2).Count, "max まで");
        Assert.AreEqual(3, ChartMarks.OpenCountOnThisPc());

        Assert.IsTrue(ChartMarks.SetComment(first.id, " 前の回のノーツいらない\n", out string error), error);
        Assert.AreEqual("前の回のノーツいらない", Saved(firstPath).marks[0].comment, "前の回のファイルにも書ける");
        Assert.AreEqual("前の回のノーツいらない", ChartMarks.Recent(10)[2].comment);
        Assert.IsTrue(ChartMarks.SetComment(now.id, "この回", out error), error);
        Assert.AreEqual("この回", Saved(ChartMarks.CurrentPath).marks[0].comment, "この回はメモリとファイルの両方");

        Assert.IsTrue(ChartMarks.SetCanceled(now.id, true, out error), error);
        Assert.AreEqual("canceled", ChartMarks.Recent(10)[0].status, "取り消しも一覧に出す(灰色にする)");
        Assert.AreEqual("canceled", Saved(ChartMarks.CurrentPath).marks[0].status);
        Assert.AreEqual(2, ChartMarks.OpenCountOnThisPc(), "取り消した印は数えない");
        Assert.AreEqual(0, ChartMarks.OpenCountThisPlay());
        Assert.IsTrue(ChartMarks.SetCanceled(second.id, true, out error), error);
        Assert.AreEqual("canceled", Saved(firstPath).marks[1].status);
        Assert.IsTrue(ChartMarks.SetCanceled(second.id, false, out error), error);
        Assert.AreEqual("open", Saved(firstPath).marks[1].status, "取り消しを戻す");

        // marks.py が直した印は <名前>.status.json に書く。一覧に出さず、数えない。状態のファイルを印のファイルとして読まない。
        string status = Path.Combine(folder, Path.GetFileNameWithoutExtension(firstPath) + ChartMarks.StatusFileSuffix);
        File.WriteAllText(status, "{\"format\": 1, \"marks\": [{\"id\": \"" + second.id
            + "\", \"status\": \"fixed\", \"fixedAt\": \"2026-10-28T10:00:00+09:00\", \"fixedCommit\": \"abc\", \"fixedNote\": \"消した\"}]}");
        CollectionAssert.AreEqual(new[] { now.id, first.id }, ChartMarks.Recent(10).Select(m => m.id).ToArray());
        Assert.AreEqual(1, ChartMarks.OpenCountOnThisPc());
        File.WriteAllText(status, "{ broken");
        Assert.AreEqual(3, ChartMarks.Recent(10).Count, "壊れた状態のファイルは「直した印なし」");

        Assert.IsFalse(ChartMarks.SetComment("nope", "x", out error));
        Assert.IsNotEmpty(error);
        Assert.IsFalse(ChartMarks.SetComment("../" + Path.GetFileNameWithoutExtension(firstPath) + "-1", "x", out error),
            "フォルダーの外を指す ID は断る");
        Assert.IsFalse(ChartMarks.SetCanceled(Path.GetFileNameWithoutExtension(firstPath) + "-9", true, out error), "無い番号");
    }

    [Test]
    public void CommentPanelSavesEachMarksWordsAndReleasesEscape()
    {
        var chart = Chart();
        Mark(chart, 4, new DateTime(2026, 10, 27, 23, 0, 0));
        Mark(chart, 8, new DateTime(2026, 10, 27, 23, 0, 4));
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        Assert.NotNull(panel);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen);
        Assert.IsTrue(InputManager.EscapeSuppressed, "日本語入力の取り消しの Esc でタイトルへ戻らない");
        Assert.IsTrue(PhoneSaberOperatorKeys.TextEntryActive, "日本語入力の F6〜F10 で運営の操作が動かない");
        Assert.IsFalse(InputManager.EscapeGetDown);
        Assert.AreEqual(2, panel.Count);
        Assert.AreSame(panel, ChartMarkCommentPanel.Open(ChartMarks.Current), "二重に開かない");
        Assert.AreEqual(ChartMarkCommentPanel.KeysGuide, panel.FooterText);

        panel.Type("1小節目のノーツいらない");
        panel.DeleteLast();
        panel.Type("い");
        panel.Submit();
        Assert.AreEqual(1, panel.Row);
        panel.Type("おそく");
        panel.Submit();   // 最後の行の Enter で閉じる
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen);
        Assert.IsFalse(InputManager.EscapeSuppressed, "閉じたら Esc を戻す");
        Assert.IsFalse(PhoneSaberOperatorKeys.TextEntryActive, "閉じたら運営の F キーを戻す");

        var saved = Saved(ChartMarks.CurrentPath);
        Assert.AreEqual("1小節目のノーツいらない", saved.marks[0].comment);
        Assert.AreEqual("おそく", saved.marks[1].comment);
    }

    [Test]
    public void ThePanelKeepsWhatWasTypedAndSavesDrafts()
    {
        var chart = Chart();
        Mark(chart, 4, new DateTime(2026, 10, 28, 9, 30, 0));
        Mark(chart, 8, new DateTime(2026, 10, 28, 9, 30, 4));
        float t = Time.unscaledTime;

        // 何も打たずに放っておくと30秒で閉じる。
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        panel.Tick(t + 10f);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen);
        StringAssert.Contains("秒で閉じます", panel.CountsText);
        panel.Tick(t + ChartMarkCommentPanel.IdleCloseSeconds + 1f);
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen, "何も打たなければ時間切れで閉じる");

        // 打ったあとは時間切れで閉じない。1秒あけて下書きを保存する。
        panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        panel.Type("下書き");
        Assert.IsTrue(panel.Typed);
        panel.Tick(t + .5f);
        Assert.AreEqual("", Saved(ChartMarks.CurrentPath).marks[0].comment, "打っている途中はまだ書かない");
        panel.Tick(t + 1.5f);
        Assert.AreEqual("下書き", Saved(ChartMarks.CurrentPath).marks[0].comment, "1秒あけて下書きを保存");
        panel.Tick(t + 1000f);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen, "打ったあとは時間切れで閉じない");
        StringAssert.DoesNotContain("秒で閉じます", panel.CountsText);

        // スマホで打った一言は、この欄で打っていない行に出る(打った行は上書きしない)。
        Assert.IsTrue(ChartMarks.SetComment(ChartMarks.Current.marks[1].id, "スマホから", out string error), error);
        Assert.IsTrue(ChartMarks.SetComment(ChartMarks.Current.marks[0].id, "上書きしない", out error), error);
        panel.Tick(t + 1001f);
        Assert.AreEqual("スマホから", panel.TextAt(1));
        Assert.AreEqual("下書き", panel.TextAt(0));

        // 閉じるときは変換中の文字も一言に含めて保存する。
        panel.Compose("へんかん");
        Assert.AreEqual("へんかん", panel.Composition);
        panel.Close();
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen);
        var saved = Saved(ChartMarks.CurrentPath);
        Assert.AreEqual("下書きへんかん", saved.marks[0].comment);
        Assert.AreEqual("スマホから", saved.marks[1].comment);

        // 変換中も時間切れで閉じない。
        panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        Assert.AreEqual("下書きへんかん", panel.TextAt(0), "開き直すと保存した一言が出る");
        panel.Compose("か");
        panel.Tick(t + 5000f);
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen);
    }

    [Test]
    public void DeleteCancelsAMarkAndTheFooterShowsWhatIsLeft()
    {
        var chart = Chart();
        Mark(chart, 4, new DateTime(2026, 10, 28, 9, 40, 0));
        Mark(chart, 8, new DateTime(2026, 10, 28, 9, 40, 4));
        ChartMarkPhoneLink.Url = "http://192.168.137.1:5020/m?t=abc";
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        StringAssert.StartsWith("この PC に直していない印が 2件(この回 2件)", panel.CountsText);
        StringAssert.Contains(Path.GetFileName(folder), panel.PlaceText, "保存先(フォルダー名の末尾)");
        Assert.AreEqual("スマホで印: <noparse>http://192.168.137.1:5020/m?t=abc</noparse>", panel.PhoneText);

        panel.Type("消さない");
        panel.ToggleCanceled();
        Assert.IsTrue(panel.IsCanceled(0));
        var saved = Saved(ChartMarks.CurrentPath);
        Assert.AreEqual("canceled", saved.marks[0].status, "取り消しも保存する(marks.py の一覧に出さない)");
        Assert.AreEqual("消さない", saved.marks[0].comment, "打った一言も同じ書き込みで残す");
        panel.Tick(Time.unscaledTime);
        StringAssert.StartsWith("この PC に直していない印が 1件(この回 1件)", panel.CountsText);
        panel.ToggleCanceled();
        Assert.IsFalse(panel.IsCanceled(0), "もう一度 Delete で戻す");
        Assert.AreEqual("open", Saved(ChartMarks.CurrentPath).marks[0].status);

        ChartMarkPhoneLink.Url = null;
        panel.Tick(Time.unscaledTime);
        Assert.AreEqual("", panel.PhoneText, "スマホのアドレスが無ければ出さない");
    }

    [Test]
    public void MarksStopAtTheLimitAndAFailedSaveIsReported()
    {
        var chart = Chart();
        for (int i = 0; i < ChartMarks.MaxMarksPerPlay; i++)
            Assert.NotNull(Mark(chart, 1 + i * .1, new DateTime(2026, 10, 27, 23, 10, 0)));
        Assert.IsTrue(ChartMarks.Full);
        Assert.IsNull(ChartMarks.Add(chart, 9, 0, null, "Morning", "Morning", "M", "", DateTime.Now), "1プレイの印は上限まで");
        Assert.IsEmpty(ChartMarks.LastSaveError);
        Assert.IsFalse(File.Exists(ChartMarks.CurrentPath + ".tmp"), "一時ファイルを残さない");

        // 置き場所がファイルで塞がれている → 保存は失敗し、理由が残る。それまでのファイルは壊さない。
        ChartMarks.BeginPlay();
        string blocked = Path.Combine(folder, "blocked");
        File.WriteAllText(blocked, "not a folder");
        ChartMarks.FolderOverride = blocked;
        var entry = Mark(chart, 2, new DateTime(2026, 10, 27, 23, 20, 0));
        Assert.NotNull(entry);
        Assert.IsNotEmpty(ChartMarks.LastSaveError, "保存に失敗したことが分かる");
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        panel.Type("消して");
        panel.Close();
        Assert.IsTrue(ChartMarkCommentPanel.IsOpen, "保存できなければ、すぐには閉じずに知らせる");
        Assert.IsTrue(panel.SaveFailed);
        panel.Tick(Time.unscaledTime);
        StringAssert.Contains("保存できませんでした", panel.FooterText);
        panel.Close();
        Assert.IsFalse(ChartMarkCommentPanel.IsOpen, "もう一度閉じると、保存できないまま閉じる(来場者を待たせ続けない)");
        Assert.IsFalse(InputManager.EscapeSuppressed);

        // 閉じても一言は捨てない。次の曲が始まっても持っておき、書けるようになったら書き直す。
        string path = ChartMarks.CurrentPath;
        Assert.IsTrue(ChartMarks.CurrentUnsaved);
        ChartMarks.BeginPlay();
        Assert.AreEqual(1, ChartMarks.UnsavedCount, "次の曲へ持ち越す");
        Assert.AreEqual(1, ChartMarks.RetryUnsaved(), "まだ書けない間は持ったまま");
        File.Delete(blocked);
        Assert.AreEqual(0, ChartMarks.RetryUnsaved());
        Assert.AreEqual("消して", Saved(path).marks[0].comment, "書けるようになったら、閉じる前に打った一言も書く");
        ChartMarks.FolderOverride = folder;
    }

    [Test]
    public void APlayThatCouldNotBeWrittenIsListedFromMemoryNotFromTheOldFile()
    {
        var chart = Chart();
        var mark = Mark(chart, 1, new DateTime(2026, 10, 28, 14, 10, 0));
        Assert.IsTrue(ChartMarks.SetComment(mark.id, "暗い", out string error), error);
        string path = ChartMarks.CurrentPath;
        // 書き込めなくなる(読み取り専用)→ 一言の変更は書けないままメモリに残り、次の曲へ持ち越す。
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.IsFalse(ChartMarks.SetComment(mark.id, "暗い、後半だけ", out error));
            ChartMarks.BeginPlay();
            Assert.AreEqual(1, ChartMarks.UnsavedCount);
            var rows = ChartMarks.Recent(10).Where(row => row.id == mark.id).ToList();
            Assert.AreEqual(1, rows.Count, "フォルダーの古い中身を重ねて出さない");
            Assert.AreEqual("暗い、後半だけ", rows[0].comment, "一覧はメモリの新しい方");
            Assert.AreEqual(1, ChartMarks.OpenCountOnThisPc());
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.AreEqual(0, ChartMarks.RetryUnsaved());
        Assert.AreEqual("暗い、後半だけ", Saved(path).marks[0].comment);
    }

    [Test]
    public void ThePanelFollowsCommentsFromThePhoneOnRowsItIsNotTyping()
    {
        var chart = Chart();
        var first = Mark(chart, 1, new DateTime(2026, 10, 28, 14, 0, 0));
        Mark(chart, 2, new DateTime(2026, 10, 28, 14, 0, 1));
        var panel = ChartMarkCommentPanel.Open(ChartMarks.Current);
        panel.Type("暗い");
        panel.Submit();
        Assert.AreEqual(1, panel.Row);

        // スマホが同じ印に書き足す → 欄はその一言に追従し、あとで欄から足しても消さない。
        Assert.IsTrue(ChartMarks.SetComment(first.id, "暗い、後半だけ", out string error), error);
        panel.Tick(Time.unscaledTime);
        Assert.AreEqual("暗い、後半だけ", panel.TextAt(0));
        panel.Move(-1);
        panel.Type("!");
        panel.Submit();
        Assert.AreEqual("暗い、後半だけ!", Saved(ChartMarks.CurrentPath).marks[0].comment);
    }

    [Test]
    public void OnlyMarksFromTheKeyOpenThePanelByThemselves()
    {
        var chart = Chart();
        Assert.IsFalse(ChartMarks.HasKeyMarks);
        Assert.NotNull(ChartMarks.Add(chart, 1, 0, null, "Morning", "Morning", "M", "", new DateTime(2026, 10, 28, 14, 5, 0),
            ChartMarks.SourcePhone, station: "A", runId: "1a2b3c4d5e6f"));
        Assert.IsFalse(ChartMarks.HasKeyMarks, "スマホだけの回は、結果画面で欄を自動では開かない");
        Assert.NotNull(Mark(chart, 2, new DateTime(2026, 10, 28, 14, 5, 1)));
        Assert.IsTrue(ChartMarks.HasKeyMarks);
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
