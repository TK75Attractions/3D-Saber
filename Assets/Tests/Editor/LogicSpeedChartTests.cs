using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// logic!!! の裏譜面 S。序盤Normal・中盤以降Hardに、はっちゃけ版の速さの表を適用。
// 譜面は C:\Users\shike\dev\tools\ChartLogicSpeed\gen_speed.py --write で作る。
public class LogicSpeedChartTests
{
    const double Approach = 1.0;   // GameSession.NoteApproachTimeDefault

    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "logic", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesSwitchFromNormalToHardWithASpeedTable()
    {
        var speed = Read("s");
        var hard = Read("hard");
        Assert.AreEqual(9, speed.displayLevel);
        MixedSecretChartAssertions.Check("logic", speed, "s", 62403, 63, 270);
        Assert.AreEqual(hard.editorTempoMap.Count, speed.editorTempoMap.Count);
        Assert.AreEqual(0, hard.scroll.Count);
        Assert.AreEqual(80, speed.scroll.Count, "はっちゃけ版の速さの表を維持");
        Assert.AreSame(RushESecretState.LogicSpeed, RushESecretState.ForDifficulty("S"));
    }

    [Test]
    public void ItStopsAFewTimesButNeverWhileANoteArrives()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        Assert.NotNull(map);
        int stops = chart.scroll.Count(p => p.speed == 0);
        Assert.That(stops, Is.InRange(5, 12), "止まるのは音が止まる所とこま送りだけ");
        foreach (var note in chart.notes)
        {
            double hit = note.TimeSeconds;
            Assert.GreaterOrEqual(map.SpeedAt(hit - .001), .3, hit + " 秒のノーツは動きながら届く(止まって待たない)");
        }
        // 止まっている間、空中に少なくとも1つのノーツが見えている(止まったことが分かる)。
        for (int i = 0; i < chart.scroll.Count; i++)
        {
            if (chart.scroll[i].speed != 0) continue;
            double from = chart.scroll[i].time / 1000.0;
            bool frozen = chart.notes.Any(n => n.TimeSeconds > from && map.Remaining(from, n.TimeSeconds) <= Approach);
            Assert.IsTrue(frozen, from + " 秒の止まりに、空中のノーツがある");
        }
    }

    [Test]
    public void EveryNoteStaysOnScreenLongEnoughToRead()
    {
        var chart = Read("s");
        var map = ScrollSpeedMap.Build(chart.scroll);
        foreach (var note in chart.notes)
        {
            double hit = note.TimeSeconds;
            double seen = hit - map.TimeAtPosition(map.PositionAt(hit) - Approach);
            Assert.GreaterOrEqual(seen - 1.0 / 30, .45, hit + " 秒のノーツは1コマ遅れても0.45秒以上見える");
        }
        Assert.LessOrEqual(chart.scroll.Max(p => p.speed), 3.0f, "音が戻る瞬間の叩き込みでも速さ 3 まで");
    }

    [Test]
    public void SomeSectionsBrakeNearTheGateAndOneDashesIn()
    {
        var chart = Read("s");
        Assert.IsTrue(chart.scroll.Any(p => p.ease > 1.2f), "手前でブレーキする区間がある");
        Assert.IsTrue(chart.scroll.Any(p => p.ease < .9f), "奥でためて手前で一気に来る区間がある");
        foreach (var p in chart.scroll) Assert.That(p.ease, Is.InRange(.6f, 1.7f), p.time + "ms");
    }

    // 止まり始め・動き出しで曲がり具合を変えると、空中のノーツがその瞬間に跳ぶか逆に戻る(10/26 見直し)。
    [Test]
    public void StopsKeepTheCurveSoNothingJumpsAtTheEdge()
    {
        var chart = Read("s");
        for (int i = 1; i < chart.scroll.Count; i++)
        {
            var p = chart.scroll[i];
            var prev = chart.scroll[i - 1];
            if (p.speed == 0 || prev.speed == 0)
                Assert.AreEqual(prev.ease, p.ease, 1e-6f, p.time + "ms の止まり・動き出しで曲がり具合が変わる");
        }
    }
}
