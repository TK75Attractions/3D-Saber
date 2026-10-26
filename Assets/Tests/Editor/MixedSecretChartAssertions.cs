using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 生成道具の切り替えを、元のJSONから独立に確かめる。
public static class MixedSecretChartAssertions
{
    public static void Check(string song, ChartData chart, string mode,
        double switchMs, int before, int after)
    {
        ChartData Read(string difficulty)
        {
            string path = Path.Combine(Application.streamingAssetsPath, "Songs", song,
                "chart_" + difficulty + ".json");
            Assert.IsTrue(File.Exists(path), path);
            return ChartLoader.Parse(File.ReadAllText(path));
        }

        var normal = Read("normal");
        var hard = Read("hard");
        Assert.AreEqual(hard.bpm, chart.bpm);
        Assert.AreEqual(hard.coordScale, chart.coordScale);
        Assert.AreEqual(hard.offsetMs, chart.offsetMs);
        Assert.AreEqual(hard.beatZeroMs, chart.beatZeroMs);
        Assert.AreEqual(before, chart.notes.Count(n => n.time < switchMs - .05));
        Assert.AreEqual(after, chart.notes.Count(n => n.time >= switchMs - .05));
        float shift = normal.offsetMs - hard.offsetMs;
        var early = normal.notes.Select(n =>
        {
            var copy = JsonUtility.FromJson<NoteData>(JsonUtility.ToJson(n));
            copy.time += shift;
            return copy;
        }).Where(n => n.time < switchMs - .05).ToList();
        var later = hard.notes.Where(n => n.time >= switchMs - .05).ToList();
        early.RemoveAll(a =>
            (a.LingerSeconds(.7f) > 0 && a.time + a.LingerSeconds(.7f) * 1000 > switchMs - .05)
            || later.Any(b => b.time - (double)a.time <= 120.05
                && (a.color == b.color || a.color == "gold" || b.color == "gold")));
        var source = early.Concat(later).OrderBy(n => n.time).ToList();
        if (mode != "x") Assert.AreEqual(source.Count, chart.notes.Count);

        bool Matches(NoteData a, NoteData b)
        {
            if (Math.Abs((double)a.time - b.time) > .03) return false;
            var restored = JsonUtility.FromJson<NoteData>(JsonUtility.ToJson(b));
            restored.time = a.time; // floatでの110ms補正の丸めだけを許す。
            if (mode == "v") restored.vanish = a.vanish;
            if (mode == "w")
            {
                restored.windX = a.windX;
                restored.windY = a.windY;
                restored.windSpin = a.windSpin;
                restored.windSway = a.windSway;
            }
            if (mode == "f")
            {
                restored.x = a.x;
                restored.y = a.y;
            }
            if (mode == "x")
            {
                if (b.color != a.color && b.color != "gold") return false;
                restored.color = a.color;
                restored.heat = a.heat;
            }
            return JsonUtility.ToJson(a) == JsonUtility.ToJson(restored);
        }

        int cursor = 0;
        foreach (var note in chart.notes)
        {
            if (mode == "x")
                while (cursor < source.Count && !Matches(source[cursor], note)) cursor++;
            Assert.Less(cursor, source.Count, "混合元の順序付き部分列: " + note.time);
            Assert.IsTrue(Matches(source[cursor++], note), "混合元にない変更: " + note.time);
        }
        var first = chart.notes.Where(n => n.time < switchMs - .05).ToList();
        var second = chart.notes.Where(n => n.time >= switchMs - .05).ToList();
        foreach (var a in first)
        {
            if (a.LingerSeconds(.7f) > 0)
                Assert.LessOrEqual(a.time + a.LingerSeconds(.7f) * 1000, switchMs - .05);
            foreach (var b in second.Where(b => a.color == b.color || a.color == "gold" || b.color == "gold"))
                Assert.Greater(b.time - (double)a.time, 120.05, "境目の同じ手が120ms以内");
        }
    }
}
