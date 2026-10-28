using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Manic Sonic の裏譜面 R(chart_r.json、dev\tools\ChartManicReverse\gen_reverse.py の出力)が設計どおりか。
// 拍160 の前は Normal、後は Hard の中身(主題A に Hard の外側のノーツを4個足した)。サビの間(門が止まる拍〜割れる拍+2拍)に
// 届くノーツは Hard の格子の位置にそろえ(上書きの表を除く)、鏡の区間に門の内側(中央の列 x = ±0.6)を通るノーツだけが鏡
// (x と矢印の横を反転)。外側の列(x = ±1.9)は門の横を素通りして反転しない(2026-10-28 v8「中央だけ反転・外側は素通り」)。
// 逆さのノーツは画面の位置へ戻すと Hard か 3-3 の表と一致する。
public class ManicSonicReverseChartTests
{
    const string Song = "ManicSonic";
    const double SwitchMs = 53333.3333;

    // サビ(届く拍 [門が止まる拍, 割れる拍 + 2))。ここに届くノーツは Hard の格子の位置にそろえる。
    static readonly (double lo, double hi)[] Choruses = { (13, 32), (64, 128), (240, 320) };
    static readonly float[] GridX = { -1.9f, -.6f, .6f, 1.9f };
    static readonly float[] GridY = { -.6f, .3f };
    // 位置の上書き(Hard の同じ時刻・同じ手の位置から変えた所)。x・y は鏡にする前の位置(Hard の座標と同じ書き方)。
    // 高揚1 の 22・24・29・30 は外側へ(外は反転しないことを初回で見せる)、80・121 は中央の下段へ、317 の X は中央の上段へ。
    static readonly (double beat, string color, float x, float y)[] Overrides =
    {
        (22, "red", 1.9f, -.6f), (24, "blue", -1.9f, -.6f), (29, "red", 1.9f, .3f), (30, "blue", -1.9f, .3f),
        (80, "red", .6f, -.6f), (121, "red", .6f, -.6f), (317, "red", .6f, .3f), (317, "blue", -.6f, .3f),
    };
    // 主題A に足した Hard のノーツ(外側・反転なし。Hard の時刻と座標のまま)。
    static readonly (double beat, string color)[] Added = { (91, "red"), (95, "blue"), (113, "blue"), (114, "red") };
    // 高揚3 の間引き(整数の拍だけ)から戻した Hard のノーツ(外側・反転なし)。
    static readonly (double beat, string color)[] Restored =
        { (262.75, "blue"), (265.5, "red"), (269.5, "blue"), (308.5, "red"), (310.5, "blue") };
    // 鏡の区間(届く拍 [Hs, He))ごとの数・中央(鏡)・外側(素通り)。
    static readonly (string name, double lo, double hi, int total, int center, int outer)[] Windows =
    {
        ("高揚1", 18, 32, 9, 5, 4), ("主題A-1", 80, 96, 13, 5, 8), ("主題A-2", 112, 128, 13, 7, 6),
        ("高揚3-1", 256, 272, 15, 7, 8), ("高揚3-2", 304, 320, 12, 7, 5),
    };

    static string ChartPath => Path.Combine(Application.streamingAssetsPath, "Songs", Song, "chart_r.json");

    static ChartData Load()
    {
        Assert.IsTrue(File.Exists(ChartPath), "先に gen_reverse.py --write を実行する。Hard への代替読込では通さない");
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

    static bool Near(float a, float b) => Mathf.Abs(a - b) < 1e-4f;
    static bool InChorus(NoteData n) => Choruses.Any(c => n.beat >= c.lo - 1e-3 && n.beat < c.hi - 1e-3);
    static bool OnGrid(NoteData n) => GridX.Any(x => Near(n.x, x)) && GridY.Any(y => Near(n.y, y));
    static bool Center(NoteData n) => Near(Mathf.Abs(n.x), .6f);
    static bool Outer(NoteData n) => Near(Mathf.Abs(n.x), 1.9f);
    // 手の側(青は左・赤は右)。金はどちらでもよい。
    static bool OwnSide(NoteData n) => n.color == "blue" ? n.x < 0f : n.color == "red" ? n.x > 0f : true;
    // 門の内側(中央の 2×2 マス)を通る位置か。門の大きさは ReverseRules のもの(spec_v8: 半幅 1.25・半高 1.10)。
    static bool InsideGate(NoteData n)
        => Mathf.Abs(n.x) < ReverseRules.GateHalfWidth && Mathf.Abs(n.y - ReverseRules.BandCenter.y) < ReverseRules.GateHalfHeight;
    // 門を通る時刻(届く − 0.5 秒)が鏡の区間の中か。
    static bool InSpan(ChartData chart, NoteData n) => ReverseRules.MirrorOn(chart.reverse, n.time - ReverseRules.CrossSeconds * 1000);
    // 門が立っている間(止まる〜割れる)に門を通るか。
    static bool Standing(ChartData chart, NoteData n)
    {
        double cross = n.time - ReverseRules.CrossSeconds * 1000;
        return chart.reverse.gates.Any(g => cross >= g.time - .05 && cross <= g.endTime + .05);
    }
    static bool IsAdded(NoteData n) => n.time < SwitchMs && Added.Any(a => Math.Abs(n.beat - a.beat) < .01 && n.color == a.color);

    [Test]
    public void CountsColorsAndLevelFollowTheSpec()
    {
        var chart = Load();
        Assert.AreEqual(339, chart.notes.Count);
        Assert.AreEqual(97, chart.notes.Count(n => n.time < SwitchMs - .05), "拍160 の前は Normal 由来 93 + 主題A に足した Hard 4");
        Assert.AreEqual(242, chart.notes.Count(n => n.time >= SwitchMs - .05), "後ろは Hard 由来(高揚3 の間引きから5個を戻した)");
        Assert.AreEqual(31, chart.notes.Count(n => n.mirror), "鏡は中央の列だけ: 高揚1 5・主題A 5+7・高揚3 7+7");
        Assert.AreEqual(38, Inverted(chart).Count, "逆さ: 新しい13個 + 拍395 からの Hard 25");
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
        Assert.AreEqual(3, reverse.gates.Count, "3つのサビ");
        var stages = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        double[][] expected =
        {
            new double[] { 13, 30, 16, 30 },
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
        // 拍160 より前のサビ(拍13〜32・64〜128)は Hard の格子へ動かした(位置と時刻は ChorusNotesSitOnTheHardGridAndKeepTheMixTiming で見る)。
        // 拍160 からのサビは Hard の位置のままなので、鏡でないノーツ(外側の列と戻した5個を含む)はここで元のまま比べる。
        int ordinary = MixedSecretChartAssertions.CheckOrdinarySubsequence(Song, chart, SwitchMs,
            n => !n.mirror && n.beat < 379 - .001 && !(n.time < SwitchMs - .05 && InChorus(n)));
        Assert.AreEqual(225, ordinary, "鏡でも逆さでもなく、格子へ動かしていないノーツは、混合元の順序付き部分列(元のまま)");
    }

    // サビの間に届くノーツ(門が立っている間に門を通るノーツは全部この中)は Hard の格子の上。位置は上書きの表のほかは
    // Hard の同じ時刻・同じ手のノーツと同じ(鏡は x を反対にして比べる)。時刻・色・矢印・数は混合元の順序付き部分列で、
    // 足したノーツと戻したノーツは Hard のノーツのまま。
    [Test]
    public void ChorusNotesSitOnTheHardGridAndKeepTheMixTiming()
    {
        var chart = Load();
        var normal = Read("normal");
        var hard = Read("hard");
        var standing = chart.notes.Where(n => Standing(chart, n)).ToList();
        Assert.AreEqual(141, standing.Count, "門が立っている間に門を通るノーツ");
        foreach (var note in standing)
        {
            Assert.IsTrue(InChorus(note), "門が立っている間に門を通るノーツはサビの間に届く " + note.time);
            Assert.IsTrue(OnGrid(note), "門が立っている間に門を通るノーツは格子(x ±0.6・±1.9、y −0.6・0.3)の上 " + note.time);
        }

        var chorus = chart.notes.Where(n => n.beat < 379 - .001 && InChorus(n)).ToList();
        Assert.AreEqual(144, chorus.Count, "サビの間に届くノーツ(足した4個を含む)");
        int overridden = 0, sameAsHard = 0;
        foreach (var note in chorus)
        {
            Assert.IsTrue(OnGrid(note), "サビの間は格子の上 " + note.time);
            float before = note.mirror ? -note.x : note.x;    // 鏡にする前の位置
            string hand = note.color != "gold" ? note.color : before < 0f ? "blue" : "red";
            var over = Overrides.Where(o => Math.Abs(o.beat - note.beat) < .01 && o.color == hand).ToList();
            if (over.Count == 1)
            {
                Assert.AreEqual(over[0].x, before, 1e-4, "上書きの表の位置 " + note.time);
                Assert.AreEqual(over[0].y, note.y, 1e-4, "上書きの表の位置 " + note.time);
                overridden++;
                continue;
            }
            var match = hard.notes.Where(h => Math.Abs(h.time - note.time) < .05 && h.color == hand).ToList();
            Assert.AreEqual(1, match.Count, "Hard の同じ時刻・同じ手のノーツ " + note.time);
            Assert.AreEqual(match[0].x, before, 1e-4, "位置は Hard と同じ(鏡は x を反対にして比べる) " + note.time);
            Assert.AreEqual(match[0].y, note.y, 1e-4, "位置は Hard と同じ " + note.time);
            sameAsHard++;
        }
        Assert.AreEqual(Overrides.Length, overridden, "上書きの表は1個ずつ使う");
        Assert.AreEqual(chorus.Count - Overrides.Length, sameAsHard);

        // 時刻・色・矢印・数は、混合元(拍160 の前は Normal、後は Hard)の順序付き部分列。足したノーツは除いて比べる。
        // 色を変えてよいのは高揚1 の最初の鏡の2個(金)だけ。鏡の矢印は横を反転して比べる(縦だけなので同じ向き)。
        var source = normal.notes.Where(n => n.time < SwitchMs - .05).Concat(hard.notes.Where(n => n.time >= SwitchMs - .05))
            .OrderBy(n => n.time).ToList();
        int cursor = 0, recolored = 0;
        foreach (var note in chorus.Where(n => !IsAdded(n)))
        {
            Func<NoteData, bool> same = s => Math.Abs(s.time - note.time) < .05 && s.count == note.count
                && s.IsLong == note.IsLong && s.IsDirection == note.IsDirection
                && (note.mirror ? ReverseRules.MirrorDirection(s.direction) : s.direction) == note.direction
                && (s.color == note.color || (note.mirror && note.color == "gold" && s.color != "gold" && note.beat < 32));
            while (cursor < source.Count && !same(source[cursor])) cursor++;
            Assert.Less(cursor, source.Count, "混合元にない、または順序の違うノーツ " + note.time);
            if (source[cursor].color != note.color) recolored++;
            cursor++;
        }
        Assert.AreEqual(2, recolored, "色を変えたのは高揚1 の最初の鏡の2個(金)だけ");

        // 足したノーツは Hard のノーツそのもの(外側・反転なし)で、Normal に無い所に足す。
        foreach (var (beat, color) in Added)
        {
            var mine = chart.notes.Where(n => Math.Abs(n.beat - beat) < .01 && n.color == color).ToList();
            var theirs = hard.notes.Where(h => Math.Abs(h.beat - beat) < .01 && h.color == color).ToList();
            Assert.AreEqual(1, mine.Count, "足したノーツ " + beat);
            Assert.AreEqual(1, theirs.Count);
            Assert.AreEqual(theirs[0].time, mine[0].time, .01, "足したノーツは Hard と同じ時刻 " + beat);
            Assert.AreEqual(theirs[0].x, mine[0].x, 1e-4, "足したノーツは Hard と同じ位置 " + beat);
            Assert.AreEqual(theirs[0].y, mine[0].y, 1e-4);
            Assert.AreEqual(theirs[0].direction, mine[0].direction);
            Assert.IsFalse(mine[0].mirror || mine[0].IsLong, "足したノーツは外側のふつうのノーツ " + beat);
            Assert.IsTrue(Outer(mine[0]) && OwnSide(mine[0]) && InSpan(chart, mine[0]), "足したノーツは鏡の区間の外側の列 " + beat);
            Assert.IsFalse(normal.notes.Any(n => Math.Abs(n.time - mine[0].time) < .05 && n.color == color), "Normal に無い所に足す " + beat);
        }
        // 高揚3 の間引きから戻したノーツも Hard の時刻・位置のまま(外側・反転なし)。
        foreach (var (beat, color) in Restored)
        {
            var mine = chart.notes.Single(n => Math.Abs(n.beat - beat) < .01 && n.color == color);
            var theirs = hard.notes.Single(h => Math.Abs(h.beat - beat) < .01 && h.color == color);
            Assert.AreEqual(theirs.time, mine.time, .01, "戻したノーツ " + beat);
            Assert.AreEqual(theirs.x, mine.x, 1e-4);
            Assert.AreEqual(theirs.y, mine.y, 1e-4);
            Assert.IsFalse(mine.mirror);
            Assert.IsTrue(Outer(mine) && InSpan(chart, mine), "戻したノーツは鏡の区間の外側の列 " + beat);
        }
    }

    // 鏡の区間に門を通るノーツのうち、門の内側(中央の列 ±0.6)だけが鏡。譜面の x は本当の着地点で、手と反対の側(腕が交差する)。
    // 外側の列(±1.9)は門の横を素通りして、手の側のまま。
    [Test]
    public void OnlyTheCenterColumnsInsideTheGateAreMirrored()
    {
        var chart = Load();
        var raw = JsonUtility.FromJson<ChartData>(File.ReadAllText(ChartPath));
        Assert.AreEqual(31, raw.notes.Count(n => n.mirror), "譜面に書いた mirror");
        CollectionAssert.AreEquivalent(raw.notes.Where(n => n.mirror).Select(n => n.time).ToList(),
            chart.notes.Where(n => n.mirror).Select(n => n.time).ToList(), "読み込み(門の内側・鏡の間の Normalize)で外れる mirror は無い");
        foreach (var note in chart.notes)
        {
            bool span = InSpan(chart, note);
            if (note.mirror)
            {
                Assert.IsTrue(span, "鏡は門を通る時刻が鏡の区間の中 " + note.time);
                Assert.IsTrue(Center(note) && InsideGate(note), "鏡は門の内側の中央の列(±0.6)だけ " + note.time);
                Assert.IsTrue(note.color == "gold" || !OwnSide(note), "鏡の着地点は手と反対の側(赤は左・青は右) " + note.time);
            }
            if (!span) continue;
            Assert.IsTrue(Center(note) || Outer(note), "鏡の区間は中央か外側の列 " + note.time);
            Assert.AreEqual(Center(note), note.mirror, "鏡の区間では中央の列だけが鏡 " + note.time);
            Assert.AreEqual(InsideGate(note), note.mirror, "門の内側を通るノーツだけが鏡、外側の列は門の横を素通り " + note.time);
            if (!note.mirror) Assert.IsTrue(OwnSide(note), "外側の列は手の側(青は左・赤は右) " + note.time);
            Assert.IsFalse(note.IsLong, "鏡の区間にロングを置かない " + note.time);
            Assert.IsTrue(note.direction == "none" || note.direction == "up" || note.direction == "down", "鏡の区間の矢印は縦だけ " + note.time);
        }
        foreach (var w in Windows)
        {
            var inside = chart.notes.Where(n => n.beat >= w.lo - 1e-3 && n.beat < w.hi - 1e-3).ToList();
            Assert.IsTrue(inside.All(n => InSpan(chart, n)), w.name + " のノーツは門を通る時刻が鏡の間");
            Assert.AreEqual(w.total, inside.Count, w.name);
            Assert.AreEqual(w.center, inside.Count(n => n.mirror), w.name + " の中央(鏡)");
            Assert.AreEqual(w.outer, inside.Count(n => !n.mirror), w.name + " の外側(素通り)");
        }
        Assert.AreEqual(Windows.Sum(w => w.total), chart.notes.Count(n => InSpan(chart, n)), "鏡の区間のほかに、門を通る時刻が鏡の間のノーツは無い");

        // 初回は中央の上段の金2個から。主題A-1 の金の組は外側(左右に1個ずつ)で反転しない。
        var first = chart.notes.Where(n => n.mirror).OrderBy(n => n.time).Take(2).ToList();
        Assert.IsTrue(first.All(n => n.color == "gold" && Near(n.y, .3f)), "初回は金2個から");
        Assert.AreEqual(2, chart.notes.Count(n => n.mirror && n.color == "gold"), "鏡の金は高揚1 の2個だけ");
        var gold83 = chart.notes.Where(n => Math.Abs(n.beat - 83) < .01f).ToList();
        Assert.AreEqual(2, gold83.Count);
        Assert.IsTrue(gold83.All(n => n.color == "gold" && !n.mirror && Outer(n)), "主題A-1 の金の組は外側(±1.9)で反転しない");
        Assert.AreEqual(0f, gold83.Sum(n => n.x), 1e-4, "左右に1個ずつ");

        // 切り替え(鏡にする・戻す・割れる)の前後 0.15 秒には、門を通るノーツを置かない(中央・外側とも)。
        var switches = chart.reverse.gates.SelectMany(g => g.on.SelectMany(s => new[] { s.time, s.endTime }).Concat(new[] { g.endTime })).ToList();
        foreach (var note in chart.notes)
            foreach (float at in switches)
                Assert.GreaterOrEqual(Math.Abs(note.time - ReverseRules.CrossSeconds * 1000 - at), ReverseRules.SwitchClearSeconds * 1000 - 1e-3,
                    "切り替えから 0.15 秒以上離れる " + note.time);

        // 高揚3 の最後は、両腕が交差する X(拍317 の両手同時。中央の上段の鏡で、赤が左・青が右)。
        var x = chart.notes.Where(n => Math.Abs(n.beat - 317) < .01f).ToList();
        Assert.AreEqual(2, x.Count);
        Assert.IsTrue(x.All(n => n.mirror && Center(n) && Near(n.y, .3f)), "X は中央の上段の鏡の2個");
        Assert.Less(x.Single(n => n.color == "red").x, 0f, "右手が左へ");
        Assert.Greater(x.Single(n => n.color == "blue").x, 0f, "左手が右へ");
        Assert.AreEqual(x[0].time, chart.notes.Where(n => n.mirror).Max(n => n.time), 1e-3, "X が鏡の最後");
    }

    // 腕が交差する(赤が左・青が右に着く)のは中央の鏡のノーツだけ。同じ手の連続交差は3打まで、両腕の X は拍317 の1組だけ。
    [Test]
    public void ArmsCrossOnlyOnCenterMirrorNotes()
    {
        var chart = Load();
        var turns = chart.reverse.turns;
        Func<NoteData, bool> crossed = n =>
        {
            bool upside = n.time >= turns[0].endTime - .5f && n.time < turns[1].time;
            float hx = upside ? -n.x : n.x;   // 逆さの間は入力も回るので、画面の位置で見る
            return (n.color == "red" && hx < -1e-4f) || (n.color == "blue" && hx > 1e-4f);
        };
        var crossing = chart.notes.Where(crossed).ToList();
        Assert.AreEqual(29, crossing.Count, "交差は鏡の色つきのノーツ(鏡 31 − 金 2)");
        Assert.IsTrue(crossing.All(n => n.mirror && Center(n)), "交差するのは中央の鏡のノーツだけ");
        foreach (var color in new[] { "red", "blue" })
        {
            int run = 0, longest = 0;
            foreach (var n in chart.notes.Where(m => m.color == color).OrderBy(m => m.time))
            {
                run = crossed(n) ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
            Assert.LessOrEqual(longest, 3, "同じ手の連続交差は3打まで " + color);
        }
        var pairs = crossing.GroupBy(n => Mathf.RoundToInt(n.time)).Where(g => g.Select(n => n.color).Distinct().Count() == 2).ToList();
        Assert.AreEqual(1, pairs.Count, "両腕の X は1組");
        Assert.IsTrue(pairs[0].All(n => Math.Abs(n.beat - 317) < .01f), "X は拍317");
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
        // 鏡の区間(門を通る時刻が鏡の間)の中と前後は、中央・外側を問わず、前半(高揚1・主題A)1/3 秒・後半(高揚3)0.5 秒以上。
        Func<NoteData, double> limit = n => !InSpan(chart, n) ? .15 : n.time < SwitchMs ? 1 / 3.0 : .5;
        var ordered = chart.notes.OrderBy(n => n.time).ToList();
        for (int i = 0; i < ordered.Count; i++)
            for (int j = i + 1; j < ordered.Count && ordered[j].time - ordered[i].time < 1000; j++)
            {
                var a = ordered[i];
                var b = ordered[j];
                if (!SameHand(a, b) || b.time - a.time <= 10) continue;
                double gap = (b.time - a.time) / 1000.0 - a.LingerSeconds(.7f);
                Assert.GreaterOrEqual(gap, .15 - 1e-3, "同じ手の間は 0.15 秒以上 " + a.time);
                Assert.GreaterOrEqual(gap, Math.Max(limit(a), limit(b)) - 1e-3,
                    "鏡の区間の中と前後は前半 0.333 秒・後半 0.5 秒以上(外側の列も) " + a.time + " → " + b.time);
            }
    }

    [Test]
    public void SoundsLandOnTheirNotesAndBeats()
    {
        var chart = Load();
        var events = ReverseSfx.BuildEvents(chart, 0);
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
