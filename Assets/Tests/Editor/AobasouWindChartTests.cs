using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 青葉騒の裏譜面 W。序盤Normal・拍108以後Hardへ風を付け直す。
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
    public void NotesSwitchFromNormalToHardWithWindAdded()
    {
        var wind = Read("w");
        Assert.AreEqual(9, wind.displayLevel);
        MixedSecretChartAssertions.Check("Aobasou", wind, "w", 46285.7143, 58, 258);
        Assert.IsTrue(Read("hard").notes.All(n => !n.HasWind));
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
