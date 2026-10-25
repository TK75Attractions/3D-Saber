using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 青葉騒の裏譜面 W(風に流されて来る、2026-10-25)。ノーツは Hard そのまま、風の値だけが足してある。
// 譜面は C:\Users\shike\dev\tools\ChartAobasouWind\gen_wind.py --write で作る。
public class AobasouWindChartTests
{
    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "Aobasou", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesAreTheHardChartWithWindAdded()
    {
        var wind = Read("w");
        var hard = Read("hard");
        Assert.AreEqual(9, wind.displayLevel);
        Assert.AreEqual(hard.bpm, wind.bpm);
        Assert.AreEqual(hard.notes.Count, wind.notes.Count);
        for (int i = 0; i < hard.notes.Count; i++)
        {
            NoteData a = hard.notes[i], b = wind.notes[i];
            string label = b.beat + "拍";
            Assert.AreEqual(a.time, b.time, label);
            Assert.AreEqual(a.x, b.x, label);
            Assert.AreEqual(a.y, b.y, label);
            Assert.AreEqual(a.color, b.color, label);
            Assert.AreEqual(a.type, b.type, label);
            Assert.AreEqual(a.direction, b.direction, label);
            Assert.AreEqual(a.count, b.count, label);
            Assert.IsFalse(a.HasWind, "Hard には風が無い");
        }
    }

    [Test]
    public void MostNotesRideTheWindAndTheOutroLandsStill()
    {
        var wind = Read("w");
        int moving = wind.notes.Count(n => n.HasWind);
        Assert.Greater(moving, wind.notes.Count * 8 / 10, "ほとんどのノーツが風に乗る");
        // 最後の 304 拍からは、なぎ(風なし)で静かに着く。
        foreach (var n in wind.notes.Where(n => n.beat >= 304))
            Assert.IsFalse(n.HasWind, n.beat + "拍");
        // つむじ風の区間だけが回る。
        Assert.IsTrue(wind.notes.Any(n => n.windSpin > 0) && wind.notes.Any(n => n.windSpin < 0));
    }

    // 風の値は判定面で見たときの画面上のずれ(奥では WindRules.DepthScale 倍に動かす)。
    // 判定面で見た画面は横 ±7.2、縦は約 -3.8〜4.2。HUD と床に重ならない内側に収める。
    [Test]
    public void EveryNoteAppearsInsideTheVisibleArea()
    {
        foreach (var n in Read("w").notes)
        {
            string label = n.beat + "拍";
            Assert.LessOrEqual(Mathf.Abs(n.x + n.windX) + n.windSway, 5.0f, label);
            Assert.That(n.y + n.windY, Is.InRange(-2.6f, 2.6f), label);
            Assert.That(n.windSway, Is.InRange(0f, 1f), label);
        }
    }
}
