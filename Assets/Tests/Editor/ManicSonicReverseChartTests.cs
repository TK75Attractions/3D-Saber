using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Manic Sonic の裏譜面 R(chart_r.json、dev\tools\ChartManicReverse\gen_reverse.py の出力)が設計どおりか。
// 拍160 の前は Normal、後は Hard の中身。鏡のノーツは写しを戻すと元と一致し、逆さのノーツは画面の位置へ戻すと Hard か 3-3 の表と一致する。
public class ManicSonicReverseChartTests
{
    const string Song = "ManicSonic";
    const double SwitchMs = 53333.3333;

    static ChartData Load()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", Song, "chart_r.json");
        Assert.IsTrue(File.Exists(path), "先に gen_reverse.py --write を実行する。Hard への代替読込では通さない");
        return ChartLoader.LoadFromStreamingAssets(Song, "R");
    }

    static ChartData Read(string difficulty)
        => ChartLoader.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "Songs", Song, "chart_" + difficulty + ".json")));

    // 逆さの間(1回目の回転の終わり〜2回目の回転の頭)に届くノーツ。
    static List<NoteData> Inverted(ChartData chart)
    {
        var turns = chart.reverse.turns;
        return chart.notes.Where(n => n.time >= turns[0].endTime - .5f && n.time < turns[1].time).ToList();
    }

    static bool SameHand(NoteData a, NoteData b) => a.color == b.color && a.color != "gold";

    [Test]
    public void CountsColorsAndLevelFollowTheSpec()
    {
        var chart = Load();
        Assert.AreEqual(330, chart.notes.Count);
        Assert.AreEqual(93, chart.notes.Count(n => n.time < SwitchMs - .05), "拍160 の前は Normal 由来");
        Assert.AreEqual(237, chart.notes.Count(n => n.time >= SwitchMs - .05), "後ろは Hard 由来");
        Assert.AreEqual(53, chart.notes.Count(n => n.mirror), "鏡: 高揚1 9・主題A 11+11・高揚3 12+10");
        Assert.AreEqual(38, Inverted(chart).Count, "逆さ: 新しい13個 + 拍395 からの Hard 25");
        Assert.AreEqual(43, chart.notes.Count(n => n.swell));
        Assert.AreEqual(37, chart.notes.Where(n => n.swell).Select(n => Math.Round(n.time)).Distinct().Count(), "逆シンバルは届く時刻で37か所");
        Assert.AreEqual(10, chart.notes.Count(n => n.color == "gold"));
        Assert.AreEqual(8, chart.notes.Count(n => n.IsDirection));
        Assert.AreEqual(4, chart.notes.Count(n => n.IsLong));
        Assert.AreEqual(10, chart.displayLevel);
        Assert.IsTrue(chart.reverse.enabled);
        Assert.IsNull(chart.heat, "熱交換ではない(HeatMode は作られない)");
        Assert.IsFalse(chart.notes.Any(n => n.hidden || n.HasWind || n.HasOvertake || n.fall || n.slide
            || n.vanish != "" || n.hitScale != 0f || n.veil != 0f), "ほかの裏譜面の仕掛けを混ぜない");
        Assert.IsFalse(chart.quiz.HasContent || chart.memory.HasContent || chart.bull.HasContent || chart.aim.HasContent);
        var hard = Read("hard");
        Assert.AreEqual(hard.bpm, chart.bpm);
        Assert.AreEqual(hard.offsetMs, chart.offsetMs);
        Assert.AreEqual(hard.editorTempoMap.Count, chart.editorTempoMap.Count, "テンポ表は Hard と同じ");
    }

    [Test]
    public void TheReverseBlockIsTheDesignedTimeline()
    {
        var chart = Load();
        var reverse = chart.reverse;
        Assert.AreEqual(.45f, reverse.gap, 1e-6);
        Assert.AreEqual(5, reverse.pieces.Count);
        CollectionAssert.AreEqual(new[] { 4, 2, 2, 6, 6 }, reverse.pieces.Select(p => p.count).ToArray(), "見せ場は導入・BPM160・BPM60");
        CollectionAssert.AreEqual(new[] { 0f, 0f, 0f, 1f, 1f }, reverse.pieces.Select(p => p.spin).ToArray(), "BPM160 と BPM60 は渦");
        Assert.AreEqual(3, reverse.gates.Count, "3つのサビ");
        var stages = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        double[][] expected =
        {
            new double[] { 15, 30, 16, 30 },
            new double[] { 64, 126, 78, 94, 110, 126 },
            new double[] { 240, 318, 254, 270, 302, 318 },
        };
        for (int g = 0; g < 3; g++)
        {
            var gate = reverse.gates[g];
            Assert.AreEqual(stages.TimeAt(expected[g][0]), gate.time, .05, "門が止まる拍 " + g);
            Assert.AreEqual(stages.TimeAt(expected[g][1]), gate.endTime, .05, "割れる拍 " + g);
            Assert.AreEqual((expected[g].Length - 2) / 2, gate.on.Count);
            for (int s = 0; s < gate.on.Count; s++)
            {
                Assert.AreEqual(stages.TimeAt(expected[g][2 + s * 2]), gate.on[s].time, .05, "鏡にする拍");
                Assert.AreEqual(stages.TimeAt(expected[g][3 + s * 2]), gate.on[s].endTime, .05, "戻す拍");
            }
        }
        Assert.AreEqual(2, reverse.turns.Count);
        Assert.AreEqual(127500f, reverse.turns[0].time, .01);
        Assert.AreEqual(131500f, reverse.turns[0].endTime, .01);
        Assert.AreEqual(180f, reverse.turns[0].degrees);
        Assert.AreEqual(stages.TimeAt(414), reverse.turns[1].time, .05, "2回目の回転の時刻はテンポ表から(約 200 BPM)");
        Assert.AreEqual(stages.TimeAt(420), reverse.turns[1].endTime, .05);
        Assert.AreEqual(360f, reverse.turns[1].degrees, "一回転して元に戻る");
        CollectionAssert.AreEqual(new[] { 3f, -3f, 3f }, reverse.creaks.Select(c => c.degrees).ToArray());
        CollectionAssert.AreEqual(new[] { 370.0, 372.0, 374.0 },
            reverse.creaks.Select(c => Math.Round(stages.BeatAt(c.time), 3)).ToArray(), "ブレイクの拍 370・372・374 にきしむ");
    }

    [Test]
    public void OrdinaryNotesComeFromTheNormalHardMixInOrder()
    {
        var chart = Load();
        int ordinary = MixedSecretChartAssertions.CheckOrdinarySubsequence(Song, chart, SwitchMs,
            n => !n.mirror && n.beat < 379 - .001);
        Assert.AreEqual(238, ordinary, "鏡でも逆さでもないノーツは、混合元の順序付き部分列(逆シンバルの印だけ戻して比べる)");
    }

    [Test]
    public void MirrorNotesAreTheReflectionOfTheMixAndCrossTheGateInsideAMirrorSpan()
    {
        var chart = Load();
        var normal = Read("normal");
        var hard = Read("hard");
        var source = normal.notes.Where(n => n.time < SwitchMs - .05).Concat(hard.notes.Where(n => n.time >= SwitchMs - .05)).ToList();
        int goldExceptions = 0;
        foreach (var note in chart.notes.Where(n => n.mirror))
        {
            Assert.IsFalse(note.IsLong, "鏡の区間にロングを置かない " + note.time);
            var match = source.Where(s => Math.Abs(s.time - note.time) < .05 && Mathf.Abs(s.x + note.x) < 1e-4f
                && Mathf.Abs(s.y - note.y) < 1e-4f && s.direction == ReverseRules.MirrorDirection(note.direction)).ToList();
            Assert.IsNotEmpty(match, "写しを戻すと元と一致する(x → −x、矢印の横を反転) " + note.time);
            if (match.All(s => s.color != note.color))
            {
                Assert.AreEqual("gold", note.color, "色を変えてよいのは金にした所だけ " + note.time);
                goldExceptions++;
            }
            Assert.IsTrue(ReverseRules.MirrorOn(chart.reverse, note.time - ReverseRules.CrossSeconds * 1000), "門を通る時刻が鏡の区間 " + note.time);
        }
        Assert.AreEqual(2, goldExceptions, "高揚1 の最初の鏡の2個だけ金にした");
        var first = chart.notes.Where(n => n.mirror).OrderBy(n => n.time).Take(2).ToList();
        Assert.IsTrue(first.All(n => n.color == "gold"), "初回は金2個から");
        foreach (var note in chart.notes)
            if (ReverseRules.MirrorOn(chart.reverse, note.time - ReverseRules.CrossSeconds * 1000))
                Assert.IsTrue(note.mirror, "鏡の区間に門を通るノーツは全部が鏡 " + note.time);

        // 切り替え(鏡にする・戻す・割れる)の前後 0.15 秒には、門を通るノーツを置かない。
        var switches = chart.reverse.gates.SelectMany(g => g.on.SelectMany(s => new[] { s.time, s.endTime }).Concat(new[] { g.endTime })).ToList();
        foreach (var note in chart.notes)
            foreach (float at in switches)
                Assert.GreaterOrEqual(Math.Abs(note.time - ReverseRules.CrossSeconds * 1000 - at), ReverseRules.SwitchClearSeconds * 1000 - 1e-3,
                    "切り替えから 0.15 秒以上離れる " + note.time);

        // 高揚3 の最後は、両腕が交差する X(拍317 の両手同時。鏡で赤が左・青が右)。
        var x = chart.notes.Where(n => Math.Abs(n.beat - 317) < .01f).ToList();
        Assert.AreEqual(2, x.Count);
        Assert.IsTrue(x.All(n => n.mirror));
        Assert.Less(x.Single(n => n.color == "red").x, 0f, "右手が左へ");
        Assert.Greater(x.Single(n => n.color == "blue").x, 0f, "左手が右へ");
        Assert.AreEqual(x[0].time, chart.notes.Where(n => n.mirror).Max(n => n.time), 1e-3, "X が鏡の最後");
    }

    // 3-3 の表(逆さの1拍1個。画面で見えてほしい位置と向き)。
    static readonly (float beat, string color, float x, float y, string direction)[] Table =
    {
        (385, "blue", -1.2f, -.6f, "none"), (386, "red", 1.2f, -.6f, "none"),
        (387, "blue", -1.9f, -.15f, "none"), (388, "red", 1.9f, -.15f, "none"),
        (389, "blue", -.6f, .3f, "up"), (390, "red", .6f, .3f, "up"),
        (391, "gold", 0f, -.15f, "none"),
        (392, "blue", -1.9f, .3f, "none"), (392, "red", 1.9f, .3f, "none"),
        (393, "blue", -1.2f, -.6f, "none"), (393, "red", 1.2f, -.6f, "none"),
        (394, "gold", -.6f, .3f, "none"), (394, "gold", .6f, .3f, "none"),
    };

    [Test]
    public void InvertedNotesAreScreenPositionsTurnedHalfway()
    {
        var chart = Load();
        var hard = Read("hard");
        var inverted = Inverted(chart);
        Assert.IsFalse(inverted.Any(n => n.IsLong || n.mirror), "逆さの間にロング・鏡を置かない");
        int fromTable = 0, fromHard = 0;
        foreach (var note in inverted)
        {
            // 譜面に焼いた W = (−S.x, −0.3 − S.y)、矢印は逆向き。180° の写しで画面での位置 S に戻す。
            Vector2 screen = ReverseRules.InputRotate(new Vector2(note.x, note.y), 180f);
            string direction = ReverseRules.TurnDirection(note.direction);
            if (note.beat < 395 - .001f)
            {
                var row = Table.Where(t => Mathf.Abs(t.beat - note.beat) < .01f && t.color == note.color
                    && Mathf.Abs(t.x - screen.x) < 1e-4f && Mathf.Abs(t.y - screen.y) < 1e-4f && t.direction == direction).ToList();
                Assert.AreEqual(1, row.Count, "3-3 の表の位置 " + note.beat + " " + note.color);
                fromTable++;
            }
            else
            {
                var match = hard.notes.Where(h => Math.Abs(h.time - note.time) < .05 && h.color == note.color
                    && Mathf.Abs(h.x - screen.x) < 1e-4f && Mathf.Abs(h.y - screen.y) < 1e-4f && h.direction == direction).ToList();
                Assert.AreEqual(1, match.Count, "Hard の締めを逆さの写しで " + note.time);
                fromHard++;
            }
        }
        Assert.AreEqual(Table.Length, fromTable);
        Assert.AreEqual(25, fromHard);
    }

    [Test]
    public void TheHingeIsAloneAndNothingFliesWhileTheWorldTurns()
    {
        var chart = Load();
        var turns = chart.reverse.turns;
        var hinge = chart.notes.Where(n => Math.Abs(n.time - turns[0].time) < 1f).ToList();
        Assert.AreEqual(1, hinge.Count, "127.5 秒はちょうつがいの金だけ");
        Assert.AreEqual("gold", hinge[0].color);
        Assert.AreEqual(ReverseRules.Pivot.x, hinge[0].x, 1e-4, "回転の軸の上(画面で動かない)");
        Assert.AreEqual(ReverseRules.Pivot.y, hinge[0].y, 1e-4);
        Assert.IsFalse(hinge[0].IsDirection || hinge[0].IsLong);
        var after = chart.notes.Where(n => n.time > turns[0].time + 1f).ToList();
        Assert.IsFalse(after.Any(n => n.time < 133500f - .5f), "ちょうつがいのあと 133.5 秒までに届くノーツは無い");
        // 実効の接近時間はいちばん長くて 2.0 秒。回っている間(〜131.5 秒)に出現するノーツは、ちょうつがいのほかに無い。
        Assert.IsTrue(after.All(n => n.TimeSeconds - ReverseRules.MaximumApproach >= turns[0].endTime / 1000.0 - 1e-6),
            "回り終わるまでノーツを出さない");
        // 最後のノーツの判定(遅め 0.18 秒 + カメラの遅れ 0.12 秒 + Miss の猶予と待ち)が閉じてから一回転が始まる。
        double last = chart.notes.Max(n => n.TimeSeconds + n.LingerSeconds(.7f));
        Assert.Less(last + JudgmentTierHelper.LateBadSeconds + .12 + .06 + .15, turns[1].time / 1000.0);
    }

    [Test]
    public void BodyLimitsAreKept()
    {
        var chart = Load();
        Assert.IsTrue(chart.notes.All(n => n.y >= -.6f - 1e-4f && n.y <= .3f + 1e-4f), "高さは −0.6〜0.3(逆さの写しも同じ範囲)");
        Assert.IsTrue(chart.notes.All(n => Mathf.Abs(n.x) <= 1.9f + 1e-4f));
        var ordered = chart.notes.OrderBy(n => n.time).ToList();
        for (int i = 0; i < ordered.Count; i++)
            for (int j = i + 1; j < ordered.Count && ordered[j].time - ordered[i].time < 1000; j++)
            {
                var a = ordered[i];
                var b = ordered[j];
                if (!SameHand(a, b) || b.time - a.time <= 10) continue;
                double gap = (b.time - a.time) / 1000.0 - a.LingerSeconds(.7f);
                Assert.GreaterOrEqual(gap, .15 - 1e-3, "同じ手の間は 0.15 秒以上 " + a.time);
                if (a.mirror && b.mirror)
                    Assert.GreaterOrEqual(gap, (a.time < SwitchMs ? 1 / 3.0 : .5) - 1e-3,
                        "交差する鏡の区間は Normal 由来 0.333 秒・Hard 由来 0.5 秒以上 " + a.time);
            }
    }

    [Test]
    public void SoundsLandOnTheirNotesAndBeats()
    {
        var chart = Load();
        var events = ReverseSfx.BuildEvents(chart, 0);
        var swells = events.Where(e => e.Cue == ReverseCue.Swell).ToList();
        Assert.AreEqual(37, swells.Count, "同時の組は1回");
        var hits = chart.notes.Where(n => n.swell).Select(n => n.TimeSeconds).ToList();
        foreach (var swell in swells)
            Assert.IsTrue(hits.Any(h => Math.Abs(swell.At + swell.Length - h) < 1e-6), "届く時刻でいちばん大きい " + swell.At);
        Assert.AreEqual(20, swells.Count(e => Math.Abs(e.Length - 2 / 3.0) < 1e-3), "180 BPM は2拍");
        Assert.AreEqual(6, swells.Count(e => Math.Abs(e.Length - .75) < 1e-3), "160 BPM は2拍");
        Assert.AreEqual(11, swells.Count(e => Math.Abs(e.Length - 1) < 1e-3), "60 BPM は1拍(1秒)");
        Assert.AreEqual(15, events.Count(e => e.Cue == ReverseCue.Cue), "鏡にする5回 × 3拍");
        Assert.AreEqual(5, events.Count(e => e.Cue == ReverseCue.FlipIn));
        Assert.AreEqual(2, events.Count(e => e.Cue == ReverseCue.FlipOut), "主題A と高揚3 の、拍の土台の句へ戻すとき");
        Assert.AreEqual(3, events.Count(e => e.Cue == ReverseCue.Shatter));
        Assert.AreEqual(3, events.Count(e => e.Cue == ReverseCue.Creak));
        var rumble = events.Single(e => e.Cue == ReverseCue.Rumble);
        Assert.AreEqual(127.5, rumble.At, 1e-6);
        Assert.AreEqual(4, rumble.Length, 1e-6);
        Assert.AreEqual(131.5, events.Single(e => e.Cue == ReverseCue.Landing).At, 1e-6, "回り終わりの小さなドン");
        Assert.AreEqual(151.4519, events.Single(e => e.Cue == ReverseCue.FinaleHit).At, 1e-3, "曲の最後の拍で一打");
        var tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        foreach (var cue in events.Where(e => e.Cue == ReverseCue.Cue))
        {
            double beat = tempo.BeatAt(cue.At * 1000);
            Assert.AreEqual(Math.Round(beat), beat, 1e-3, "予告音は拍の上 " + cue.At);
        }
    }
}
