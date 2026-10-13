using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 改作の核として指定された、作者の同時ロングを保存データと本編ローダーで守る。
public class ZeroHardRemixTests
{
    [TestCase(34250f)]
    [TestCase(148125f)]
    public void AuthoredPairedLongsRetainEveryNoteField(float startMs)
    {
        string saved = Path.Combine(Application.dataPath, "../Tools/ChartDesign/Seisakuchu/ZeroHardRemix/authored-hard.json");
        var original = ChartLoader.Parse(File.ReadAllText(saved));
        var current = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var before = original.notes.Where(n => n.time == startMs && n.count > 1).OrderBy(n => n.x).ToArray();
        var after = current.notes.Where(n => n.time == startMs && n.count > 1).OrderBy(n => n.x).ToArray();
        Assert.AreEqual(2, before.Length);
        Assert.AreEqual(2, after.Length);
        for (int i = 0; i < before.Length; i++)
            Assert.AreEqual(JsonUtility.ToJson(before[i]), JsonUtility.ToJson(after[i]), "同時ロングの時刻・長さ・回数・座標・色を維持する");
        Assert.AreEqual(original.bpm, current.bpm);
        Assert.AreEqual(original.offsetMs, current.offsetMs);
        Assert.AreEqual(original.beatZeroMs, current.beatZeroMs);
        Assert.AreEqual(original.displayLevel, current.displayLevel);
    }

    [Test]
    public void SignatureLongsShareFiftyCutsAndTheLongerDeadline()
    {
        string saved = Path.Combine(Application.dataPath, "../Tools/ChartDesign/Seisakuchu/ZeroHardRemix/authored-hard.json");
        var original = ChartLoader.Parse(File.ReadAllText(saved));
        var current = ChartLoader.LoadFromStreamingAssets("製作中", "hard");
        var before = original.notes.Where(n => n.time == 88750f && n.count > 1).OrderBy(n => n.x).ToArray();
        var after = current.notes.Where(n => n.time == 88750f && n.count > 1).OrderBy(n => n.x).ToArray();
        Assert.AreEqual(2, before.Length);
        Assert.AreEqual(2, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            before[i].count = 50;
            before[i].lengthMs = 21677f;
            before[i].y = -.3f;
            Assert.AreEqual(JsonUtility.ToJson(before[i]), JsonUtility.ToJson(after[i]));
        }
        var extras = current.notes.Where(n => n.time > 88750f && n.time < 88750f + 21677f).ToArray();
        Assert.AreEqual(8, extras.Length);
        Assert.IsTrue(extras.All(n => n.count == 1 && n.type == "tap" && n.direction == "none"));
        Assert.AreEqual(2, extras.Select(n => new Vector2(n.x, n.y)).Distinct().Count());
        Assert.IsTrue(extras.All(n => Mathf.Abs(n.x) >= 2f), "超ロング中の単発は外側の固定レーンへ置く");
        Assert.IsTrue(extras.All(n => n.y == 1.5f), "配置可能域内の上段へ置く");
    }
}
