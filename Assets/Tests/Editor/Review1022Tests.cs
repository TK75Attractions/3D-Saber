using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Action = AudioOutputRecovery.Action;
using Object = UnityEngine.Object;

// 2026-10-22 の仕様・演出の点検で直したところ(番号は点検のページの番号)。
public class Review1022Tests
{
    // ---- 1: 音の出力先が変わったとき ----
    // 同日ユーザー「戻るのやめて」: 画面は戻さず、その場で音を立て直す。曲の途中は読み直して 3・2・1 で続きから。
    [Test]
    public void GameKeepsTheScreenAndResumesTheSongAfterTheOutputChanges()
    {
        Assert.AreEqual(Action.ResumeSong, AudioOutputRecovery.PlanGame(false, false, false, false, false, true), "曲の途中・カウントイン・一時停止");
        Assert.AreEqual(Action.InPlace, AudioOutputRecovery.PlanGame(false, false, false, false, false, false), "曲を始める前は予約の直前に読み直す");
        Assert.AreEqual(Action.InPlace, AudioOutputRecovery.PlanGame(false, false, true, true, false, true), "終わりの演出はそのまま続ける");
        Assert.AreEqual(Action.InPlace, AudioOutputRecovery.PlanGame(false, true, false, false, false, true), "練習はその場で曲を鳴らし直す");
        Assert.AreEqual(Action.CalibrationInPlace, AudioOutputRecovery.PlanGame(true, false, false, false, false, true));
        Assert.AreEqual(Action.None, AudioOutputRecovery.PlanGame(false, false, false, false, true, true), "もう画面を出るところ");
        Assert.AreEqual(Action.None, AudioOutputRecovery.PlanGame(false, false, false, true, false, true), "空の譜面で選曲へ戻るところ");
    }

    // 作った音を鳴らす前の確かめ(ProceduralSfx.Revive)。中身のある音・ここで作っていない音・null はそのまま返す。
    [Test]
    public void ReviveKeepsLiveClipsAndLeavesForeignOnesAlone()
    {
        var made = ProceduralSfx.Clip("ReviveProbe", new float[] { 0f, .5f, -.5f });
        var foreign = AudioClip.Create("ForeignProbe", 3, 1, 44100, false);
        try
        {
            var held = made;
            Assert.AreSame(made, ProceduralSfx.Revive(ref held));
            Assert.AreSame(made, held);
            var other = foreign;
            Assert.AreSame(foreign, ProceduralSfx.Revive(ref other));
            AudioClip none = null;
            Assert.IsNull(ProceduralSfx.Revive(ref none));
        }
        finally { Object.DestroyImmediate(made); Object.DestroyImmediate(foreign); }
    }

    // ---- 3: 片方の色だけ届かないとき ----
    [Test]
    public void ResumeWaitsForBothColorsOrFiveSecondsOfOne()
    {
        var watch = new PlayTrackingWatch();
        Feed(watch, 0, 0, 10); Feed(watch, 1, 0, 10);
        Assert.AreEqual(PlayTrackingWatch.Request.Pause, watch.Step(13.5, true));
        Feed(watch, 0, 20, 21.2);
        Assert.True(watch.Receiving(21.2), "赤が届き始めた(確認中)");
        Assert.False(watch.Stable(21.2), "両手で遊んでいた人は、片方だけ1秒では数え始めない");
        Feed(watch, 0, 21.2, 25.1);
        Assert.True(watch.Stable(25.1), "片方しか戻らないときは、5秒続けば数え始める");

        var both = new PlayTrackingWatch();
        Feed(both, 0, 0, 10); Feed(both, 1, 0, 10);
        both.Step(13.5, true);
        Feed(both, 0, 20, 21.1); Feed(both, 1, 20, 21.1);
        Assert.True(both.Stable(21.1), "両方戻れば今までどおり1秒");
        Assert.False(new PlayTrackingWatch().Receiving(5), "一度も届いていなければ確認中にしない");
    }

    [Test]
    public void ColorWarningNeedsThatColorsNotesAndThreeSeconds()
    {
        var watch = new PlayTrackingWatch();
        Feed(watch, 0, 0, 10); Feed(watch, 1, 0, 6);
        Assert.False(watch.ShouldWarn(1, 8.9, true, false, 0), "3秒たっていない");
        Assert.True(watch.ShouldWarn(1, 9.1, true, false, 0));
        Assert.False(watch.ShouldWarn(1, 9.1, false, false, 0), "青ノーツが来ていなければ出さない");
        Assert.False(watch.ShouldWarn(0, 9.1, true, false, 0), "赤は届いている");
        var none = new PlayTrackingWatch();
        Assert.False(none.ShouldWarn(1, 100, true, false, 0), "エディター(マウスで試す)では、一度も届いていない色を知らせない");
        Assert.False(none.ShouldWarn(1, 2.9, true, true, 0));
        Assert.True(none.ShouldWarn(1, 3.1, true, true, 0), "会場の Player では、曲が鳴り始めて3秒で知らせる");
        Assert.False(none.ShouldWarn(1, 10, true, true, double.NaN), "曲がまだ鳴っていない");
    }

    [Test]
    public void WarningTextNamesTheMissingColor()
    {
        Assert.AreEqual("スタッフへ：青の剣の位置が届いていません", PlayTrackingStatus.WarningText(SaberHand.Left, false));
        Assert.AreEqual("スタッフへ：赤の剣の位置をまだ受信していません", PlayTrackingStatus.WarningText(SaberHand.Right, true));
        StringAssert.Contains("赤と青", PlayTrackingStatus.WarningText(true, true, true));
        Assert.IsNull(PlayTrackingStatus.WarningText(false, false, false));
    }

    // ---- 8: MISS のノーツは「来ている」に数えない ----
    [Test]
    public void MissedNotesDoNotCountAsComing()
    {
        var made = new List<GameObject>();
        try
        {
            CuttableNote Note(SaberHand hand)
            {
                var go = new GameObject("Note"); made.Add(go);
                var note = go.AddComponent<CuttableNote>(); note.RequiredHand = hand; return note;
            }
            var red = Note(SaberHand.Right); var gold = Note(SaberHand.Any);
            var live = new List<CuttableNote> { red, gold };
            Assert.True(PlayTrackingWatch.LiveNeeds(live, SaberHand.Any));
            Assert.True(PlayTrackingWatch.LiveNeeds(live, SaberHand.Right));
            Assert.False(PlayTrackingWatch.LiveNeeds(live, SaberHand.Left), "金はどちらの手の警告の理由にもしない");
            red.MarkMiss(); gold.MarkMiss();
            Assert.False(PlayTrackingWatch.LiveNeeds(live, SaberHand.Any), "MISS のあと後ろへ流れて残るノーツは数えない");
        }
        finally { foreach (var go in made) Object.DestroyImmediate(go); }
    }

    [Test]
    public void UpcomingNotesCountWithinOneSecondByHand()
    {
        var notes = new List<NoteData>
        {
            new NoteData { time = 1000, color = "red" },
            new NoteData { time = 1500, color = "gold" },
            new NoteData { time = 3000, color = "blue" },
        };
        Func<NoteData, double> at = n => n.time / 1000.0;
        Assert.True(PlayTrackingWatch.UpcomingNeeds(notes, 0, at, 1, 0, SaberHand.Right));
        Assert.False(PlayTrackingWatch.UpcomingNeeds(notes, 0, at, 1, 0, SaberHand.Left), "青は2秒後に流れ始める");
        Assert.True(PlayTrackingWatch.UpcomingNeeds(notes, 0, at, 1, 1.1, SaberHand.Left));
        Assert.False(PlayTrackingWatch.UpcomingNeeds(notes, 1, at, 1, 0, SaberHand.Right), "金は赤の理由にしない");
        Assert.True(PlayTrackingWatch.UpcomingNeeds(notes, 1, at, 1, 0, SaberHand.Any));
        Assert.False(PlayTrackingWatch.UpcomingNeeds(notes, 3, at, 1, 0, SaberHand.Any), "もう出し終わった");
    }

    // ---- 7: 止まっている間の表示 ----
    [Test]
    public void PauseViewDimsShowsTheRingAndTheColorWarning()
    {
        var go = new GameObject("TrackingViewTest1022");
        try
        {
            var view = PlayTrackingStatus.Create(go.transform, 262);
            view.Show("位置受信  赤 ●  青 ○", null, false, PlayTrackingStatus.WarningText(SaberHand.Left, false));
            StringAssert.Contains("青の剣", view.WarningShown);
            Assert.False(view.PanelShown, "片方の警告は上の中央の枠を出さない");
            Assert.AreEqual(0f, view.DimLevel);
            view.Show("位置受信  赤 ○  青 ○", PlayTrackingStatus.CheckingText, false, null, -1, true);
            Assert.AreEqual(PlayTrackingStatus.DimAlpha, view.DimLevel, 1e-4f, "止まっている間は少し暗く");
            Assert.False(view.RingShown);
            Assert.AreEqual("", view.WarningShown);
            view.Show("位置受信  赤 ●  青 ○", PlayTrackingStatus.ResumeText(1.5), false, null, .5f, true);
            Assert.True(view.RingShown, "3・2・1 は輪で進み具合");
            view.Show("位置受信  赤 ●  青 ●", PlayTrackingStatus.ResumedText, false);
            Assert.False(view.RingShown);
            Assert.AreEqual(0f, view.DimLevel, "再開したら暗さを戻す");
            Assert.AreEqual(PlayTrackingStatus.ResumedText, view.HeadlineShown);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // ---- 9: 校歌のサビの終わりの音 ----
    [Test]
    public void ChorusEndSoundPlaysOncePerRealEnd()
    {
        var epilogue = new List<(double, double)> { (62.631, 90.091), (111.208, 145.401), (133.859, 145.401) };
        CollectionAssert.AreEqual(new[] { 90.091, 145.401 }, StageFeverShow.EndSoundTimes(epilogue), "同じ時刻に終わる2区間は1回");
        CollectionAssert.AreEqual(new[] { 30.0 }, StageFeverShow.EndSoundTimes(new List<(double, double)> { (10, 30), (20, 25) }),
            "ほかの強い区間が続いている間の終わりでは鳴らさない");
        Assert.IsEmpty(StageFeverShow.EndSoundTimes(null));
        foreach (string dir in Directory.GetDirectories(Path.Combine(Application.streamingAssetsPath, "Songs")))
        {
            var timeline = StagePerformanceTimeline.Load(Path.GetFileName(dir));
            if (timeline.sections == null) continue;
            var strong = timeline.sections.Where(s => s != null && s.intensity >= StagePerformanceTimeline.StrongIntensity)
                .Select(s => ((double)s.startSeconds, (double)s.endSeconds)).OrderBy(s => s.Item1).ToList();
            var times = StageFeverShow.EndSoundTimes(strong);
            for (int i = 1; i < times.Length; i++) Assert.Greater(times[i] - times[i - 1], .001, Path.GetFileName(dir));
            if (Path.GetFileName(dir) == "Epilogue") Assert.AreEqual(2, times.Length, "校歌は2回(90.091 と 145.401)");
        }
    }

    // ---- 10: コンボの数字と「COMBO」 ----
    [Test]
    public void ComboLabelStaysBelowTheDigits()
    {
        Assert.AreEqual(GameHUDSkin.ComboLabelRestY, GameHUDSkin.ComboLabelTop(-200f), "数字が小さいうちは今の位置");
        Assert.AreEqual(-340f - GameHUDSkin.ComboLabelGap, GameHUDSkin.ComboLabelTop(-340f), "数字が下へ育ったら、その下へ");
        Assert.AreEqual(GameHUDSkin.ComboLabelRestY, GameHUDSkin.ComboLabelTop(float.NaN));
    }

    // ---- 5: logic の Normal の 30〜53秒 ----
    [Test]
    public void LogicNormalFollowsTheAnticipatedSoundsOfHard()
    {
        var normal = ChartLoader.LoadFromStreamingAssets("logic", "Normal");
        var easy = ChartLoader.LoadFromStreamingAssets("logic", "Easy");
        var hard = ChartLoader.LoadFromStreamingAssets("logic", "Hard");
        bool Has(ChartData chart, double ms) => chart.notes.Any(n => Math.Abs(n.time - ms) < 1);
        // 表拍に置いていた7個は、94ms 前(16分音符ひとつ前に食った音、Hard の時刻)へ。
        foreach (double ms in new[] { 31434.0, 32934, 34434, 35934, 40434, 43434, 52434 })
        {
            Assert.True(Has(normal, ms), ms + "ms");
            Assert.True(Has(hard, ms), ms + "ms は Hard の時刻");
            Assert.False(Has(normal, ms + 94), (ms + 94) + "ms には音が無い");
        }
        Assert.True(Has(easy, 34434)); Assert.False(Has(easy, 34528), "Easy も Normal と同じ時刻へ");
    }

    // ---- 11: ユーザーが直すと決めた2か所 ----
    [Test]
    public void ElDoradoEasyStartsWhenTheMusicStarts()
    {
        // 音源は 2.10 秒まで無音。Easy の最初のノーツも Normal・Hard と同じ 2.399 秒から。
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard" })
        {
            var chart = ChartLoader.LoadFromStreamingAssets("ElDorado", difficulty);
            Assert.GreaterOrEqual(chart.notes.Min(n => n.time + chart.offsetMs), 2100f, difficulty + " は音が鳴る前にノーツを置かない");
        }
    }

    [Test]
    public void AobasouLowerChartsHaveNoNotesWhereThereIsNoSound()
    {
        var normal = ChartLoader.LoadFromStreamingAssets("Aobasou", "Normal");
        var easy = ChartLoader.LoadFromStreamingAssets("Aobasou", "Easy");
        foreach (double ms in new[] { 83571.441, 87428.5844, 117428.5889, 118285.7318, 119571.4463, 135000.02, 124714.3042 })
        {
            Assert.False(normal.notes.Any(n => Math.Abs(n.time - ms) < 1), "Normal " + ms + "ms(音が無い)");
            Assert.False(easy.notes.Any(n => Math.Abs(n.time - ms) < 1), "Easy " + ms + "ms(音が無い)");
        }
        Assert.False(normal.notes.Any(n => Math.Abs(n.time - 124607.1429) < 1));
        var replacement = normal.notes.Single(n => Math.Abs(n.time - 124285.7143) < 1);
        Assert.AreEqual("red", replacement.color);
        Assert.AreEqual("up", replacement.direction);
    }

    // ---- くり返し1回目: 全曲の音の取り方(Codex と合意した4か所) ----
    [Test]
    public void LogicLowerChartsUseTheHardSoundsNearby()
    {
        var easy = ChartLoader.LoadFromStreamingAssets("logic", "Easy");
        var normal = ChartLoader.LoadFromStreamingAssets("logic", "Normal");
        var hard = ChartLoader.LoadFromStreamingAssets("logic", "Hard");
        bool Has(ChartData chart, double ms) => chart.notes.Any(n => Math.Abs(n.time - ms) < 1);
        // 108.403 秒は、のちに Hard ごと音の頭の 108.347 秒へ動かした(ず13)。
        foreach (var (chart, from, to) in new[] { (easy, 38278.0, 38090.5), (easy, 108903.0, 108347.0), (easy, 146403.0, 146153.0), (normal, 146403.0, 146153.0) })
        {
            Assert.True(Has(chart, to) && Has(hard, to), to + "ms は同じ色の Hard の音");
            Assert.False(Has(chart, from), from + "ms には音の立ち上がりが無い");
        }
    }

    // ---- くり返し6回目: アンダルシア Normal の3個を Hard の音の頭へ ----
    // 4.16・4.86秒は拍の線(曲の頭で音より27ms早い)から +24.9ms(下位譜面の検査の25ms以内、Hard まで約2ms)、16.99秒は Hard と同じ時刻。
    [Test]
    public void AndalusiaNormalOpeningFollowsTheHardSounds()
    {
        var normal = ChartLoader.LoadFromStreamingAssets("Andalusia", "Normal");
        var hard = ChartLoader.LoadFromStreamingAssets("Andalusia", "Hard");
        foreach (var (index, hardIndex, limit) in new[] { (3, 5, 2.5), (4, 6, 2.5), (31, 43, .1) })
            Assert.AreEqual(hard.notes[hardIndex].time, normal.notes[index].time, limit, "Normal #" + index + " は Hard #" + hardIndex + " の音");
        foreach (double old in new[] { 4161.8497, 4855.4913, 16985.2344 })
            Assert.False(normal.notes.Any(n => Math.Abs(n.time - old) < 1), old + "ms(音より早い)には置かない");
    }

    // ---- ず1〜13(ユーザーの指示): 下の譜面と Hard(RUSH E は E も)が同じ時刻で音の頭からずれていた13か所を、全部の譜面で音の頭へ ----
    [TestCase("2_23_AM", 6026.2, 6058.0, "normal,hard")]
    [TestCase("2_23_AM", 88026.2, 88058.0, "easy,normal,hard")]
    [TestCase("2_23_AM", 89359.53, 89385.0, "normal,hard")]
    [TestCase("2_23_AM", 90692.87, 90732.0, "easy,hard")]
    [TestCase("BenkyouRemaster", 12774.0, 12736.0, "hard")]
    [TestCase("BenkyouRemaster", 12774.0, 12736.0, "easy,normal", false)]
    [TestCase("RushE", 15968.49, 16001.0, "normal,hard,e")]
    [TestCase("RushE", 67843.49, 67877.0, "easy,normal,hard,e")]
    [TestCase("RushE", 68218.49, 68244.0, "normal,hard,e")]
    [TestCase("RushE", 94155.99, 94131.0, "easy,normal,e")]
    [TestCase("escapade", 57493.68, 57516.0, "easy,normal,hard")]
    [TestCase("logic", 53903.0, 53928.0, "easy,normal,hard")]
    [TestCase("logic", 105403.0, 105431.0, "hard")]
    [TestCase("logic", 105403.0, 105431.0, "normal", false)]
    [TestCase("logic", 108403.0, 108347.0, "easy,hard")]
    public void SharedNotesMovedToTheSoundInEveryChart(string song, double from, double to, string difficulties, bool present = true)
    {
        foreach (string difficulty in difficulties.Split(','))
        {
            var chart = ChartLoader.LoadFromStreamingAssets(song, difficulty);
            Assert.AreEqual(present, chart.notes.Any(n => Math.Abs(n.time - to) < .2),
                song + " " + difficulty + " " + to + "ms");
            Assert.False(chart.notes.Any(n => Math.Abs(n.time - from) < 1), song + " " + difficulty + " " + from + "ms には残さない");
        }
    }

    // ---- くり返し9回目: 数字キーでも譜面の無い難易度は選ばない ----
    [Test]
    public void NumberKeysSkipDifficultiesWithoutNotes()
    {
        const string song = "__TestHardOnlySong__";
        string dir = Path.Combine(Application.streamingAssetsPath, "Songs", song);
        var go = new GameObject("SongSelectHardOnly");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "chart_hard.json"), "{ \"bpm\": 120.0, \"coordScale\": 1.0, \"offsetMs\": 0, \"notes\": [" +
                string.Join(",", Enumerable.Range(0, 8).Select(i => "{ \"time\": " + (1000 + i * 500) + ", \"type\": \"tap\", \"color\": \"" + (i % 2 == 0 ? "red" : "blue") + "\", \"x\": " + (i % 2 == 0 ? 1 : -1) + ", \"y\": 0 }")) + "] }");
            var ctl = go.AddComponent<SongSelectController>();
            ctl.Populate();
            int index = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == song);
            ctl.Select(index);
            Assert.AreEqual(2, ctl.SelectedDifficultyIndex, "曲を選ぶと譜面のある Hard になる");
            Assert.AreEqual(0, ctl.DifficultyLevelAt(0));
            ctl.ChooseDifficulty(0);
            Assert.AreEqual(2, ctl.SelectedDifficultyIndex, "数字キーの 1 でも譜面の無い Easy は選ばない(クリック・矢印と同じ)");
            ctl.ChooseDifficulty(1);
            Assert.AreEqual(2, ctl.SelectedDifficultyIndex);
        }
        finally
        {
            Object.DestroyImmediate(go);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(dir + ".meta")) File.Delete(dir + ".meta");
        }
    }

    static void Feed(PlayTrackingWatch watch, int color, double from, double to)
    {
        int n = (int)Math.Floor((to - from) * 30 + 1e-6);
        for (int i = 0; i <= n; i++) watch.ObserveUsable(color, from + i / 30.0);
    }
}
