using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 消える(Zero の「V」、2026-10-26)。残り時間で本体と合図の見え方が変わる。判定は変わらない。
public class VanishRulesTests
{
    [Test]
    public void NamesRoundTripAndUnknownIsPlain()
    {
        foreach (VanishMode mode in System.Enum.GetValues(typeof(VanishMode)))
            Assert.AreEqual(mode, VanishRules.Parse(VanishRules.Name(mode)));
        Assert.AreEqual(VanishMode.Sudden, VanishRules.Parse(" SUDDEN "));
        Assert.AreEqual(VanishMode.None, VanishRules.Parse("ghost"));
        Assert.AreEqual(VanishMode.None, VanishRules.Parse(null));
        Assert.AreEqual("", ChartLoader.Parse("{\"bpm\":98,\"notes\":[{\"time\":1000,\"x\":0,\"y\":0,\"vanish\":\"ghost\"}]}").notes[0].vanish);
        Assert.AreEqual("blink", ChartLoader.Parse("{\"bpm\":98,\"notes\":[{\"time\":1000,\"x\":0,\"y\":0,\"vanish\":\"Blink\"}]}").notes[0].vanish);
    }

    [Test]
    public void FadeHidesOnlyTheBodyNearTheGate()
    {
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Fade, .8), 1e-6);
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Fade, VanishRules.FadeFrom), 1e-6);
        Assert.AreEqual(1f, VanishRules.BodyVeil(VanishMode.Fade, VanishRules.FadeTo), 1e-6);
        Assert.AreEqual(1f, VanishRules.BodyVeil(VanishMode.Fade, 0), 1e-6);
        Assert.AreEqual(0f, VanishRules.CueVeil(VanishMode.Fade, .1), 1e-6, "矢印と着地の枠は残る");
    }

    [Test]
    public void SuddenStaysHiddenThenPopsInWithItsCues()
    {
        Assert.AreEqual(1f, VanishRules.BodyVeil(VanishMode.Sudden, .9), 1e-6);
        Assert.AreEqual(1f, VanishRules.CueVeil(VanishMode.Sudden, VanishRules.SuddenAt), 1e-6, "出るまでは合図も見えない");
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Sudden, VanishRules.SuddenTo), 1e-6);
        Assert.AreEqual(0f, VanishRules.CueVeil(VanishMode.Sudden, 0), 1e-6);
    }

    [Test]
    public void BlinkVanishesMidFlightAndComesBack()
    {
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Blink, .9), 1e-6, "遠くでは見える");
        Assert.AreEqual(1f, VanishRules.BodyVeil(VanishMode.Blink, .5), 1e-6, "途中で消える");
        Assert.AreEqual(1f, VanishRules.CueVeil(VanishMode.Blink, VanishRules.BlinkInFrom), 1e-6);
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Blink, .2), 1e-6, "直前にまた出る");
        // 途中の変わり目はなめらか(0.4 秒の前後で値が飛ばない)。
        Assert.AreEqual(VanishRules.BodyVeil(VanishMode.Blink, .4001), VanishRules.BodyVeil(VanishMode.Blink, .3999), .01);
    }

    [Test]
    public void HideTakesTheCuesTooAndMissedNotesShowUpAfterTheWindow()
    {
        Assert.AreEqual(1f, VanishRules.BodyVeil(VanishMode.Hide, .1), 1e-6);
        Assert.AreEqual(1f, VanishRules.CueVeil(VanishMode.Hide, .1), 1e-6, "合図ごと消える");
        Assert.AreEqual(1f, VanishRules.CueVeil(VanishMode.Hide, -.1), 1e-6, "判定の窓の中は隠したまま");
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Hide, -VanishRules.RevealAfterSeconds), 1e-6, "通り過ぎたら姿を戻す");
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.None, .1), 1e-6);
        Assert.AreEqual(0f, VanishRules.BodyVeil(VanishMode.Hide, double.NaN), 1e-6);
    }
}

public class VanishNoteTests
{
    GameObject root;
    NoteSpawner spawner;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("VanishNoteTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "VanishPrefab";
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.approachTime = 1;
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    CuttableNote Spawn(string vanish, string direction = "none")
    {
        var chart = new ChartData { bpm = 98 };
        chart.notes.Add(new NoteData { time = 2000, x = .5f, y = 0, color = "red", vanish = vanish,
            type = direction == "none" ? "tap" : "direction", direction = direction });
        spawner.SetChart(chart);
        Assert.IsTrue(spawner.HasVanishNotes);
        spawner.Tick(1.05);
        Assert.AreEqual(1, spawner.AliveCount);
        return spawner.LiveNotes[0];
    }

    [Test]
    public void ASuddenNoteSpawnsHiddenAndItsFrameComesBackWithIt()
    {
        var note = Spawn("sudden");
        Assert.AreEqual(VanishMode.Sudden, note.Vanish);
        Assert.AreEqual(1f, note.Veil, 1e-6);
        Assert.AreEqual(1f, note.Visuals.Veil, 1e-6, "出た瞬間は見えない");
        Assert.NotNull(note.TimingCue);
        spawner.Tick(1.3);
        Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf, "出るまでは着地の枠も隠す");
        spawner.Tick(1.65);
        Assert.AreEqual(0f, note.Veil, 1e-6);
        Assert.AreEqual(0f, note.Visuals.Veil, 1e-6, "直前に出る");
        Assert.IsTrue(note.TimingCue.GhostRoot.activeSelf, "着地の枠も戻る");
    }

    [Test]
    public void AFadeNoteLosesItsBodyButKeepsItsArrow()
    {
        var note = Spawn("fade", "left");
        Assert.NotNull(note.Arrow);
        spawner.Tick(1.8);
        Assert.AreEqual(1f, note.Visuals.Veil, 1e-6, "本体は消える");
        Assert.AreEqual(0f, note.Veil, 1e-6);
        Assert.IsTrue(note.Arrow.gameObject.activeSelf, "矢印は残る");
    }

    [Test]
    public void AHiddenNoteTakesItsArrowAlongAndShattersVisiblyWhenCut()
    {
        var note = Spawn("hide", "left");
        spawner.Tick(1.8);
        Assert.AreEqual(1f, note.Veil, 1e-6);
        Assert.IsFalse(note.Arrow.gameObject.activeSelf, "矢印も消える");
        Assert.IsTrue(note.CutAtSongTime(note.transform.position, new Vector3(-5, 0, 0), CutDirection.Left, SaberHand.Right, 2.0),
            "消えていても切れる");
        Assert.IsTrue(note.IsCut);
        Assert.AreEqual(0f, note.Veil, 1e-6, "切ったら姿を戻して割る(破片は見える)");
        Assert.AreEqual(0f, note.Visuals.Veil, 1e-6);
    }

    [Test]
    public void PlainNotesAreUntouched()
    {
        var chart = new ChartData { bpm = 98 };
        chart.notes.Add(new NoteData { time = 2000, x = .5f, y = 0, color = "red" });
        spawner.SetChart(chart);
        Assert.IsFalse(spawner.HasVanishNotes);
        spawner.Tick(1.8);
        var note = spawner.LiveNotes[0];
        Assert.AreEqual(VanishMode.None, note.Vanish);
        Assert.AreEqual(0f, note.Veil);
        Assert.AreEqual(0f, note.Visuals.Veil);
    }
}

// Zero の裏譜面 V。Normal/Hardの音源上の時刻をそろえ、区間ごとの見え方を付ける。
// 譜面は C:\Users\shike\dev\tools\ChartZeroVanish\gen_vanish.py --write で作る。
public class ZeroVanishChartTests
{
    static ChartData Read(string difficulty)
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", "製作中", "chart_" + difficulty + ".json");
        Assert.IsTrue(File.Exists(path), "必須の譜面がありません: " + path);
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    [Test]
    public void NotesSwitchFromNormalToHardWithVisibilityModes()
    {
        var vanish = Read("v");
        Assert.AreEqual(10, vanish.displayLevel);
        MixedSecretChartAssertions.Check("製作中", vanish, "v", 48889.59183673469, 50, 244);
        foreach (var note in vanish.notes)
        {
            double t = note.TimeSeconds;
            string expected = note.IsLong ? "" : t < 37 ? "fade" : t < 76 ? "sudden"
                : t < 134.7 ? "blink" : t < 164.2 ? (note.color == "gold" ? "fade" : "hide") : "";
            Assert.AreEqual(expected, note.vanish, note.time + "ms");
        }
        foreach (var mode in new[] { "fade", "sudden", "blink", "hide" })
            Assert.IsTrue(vanish.notes.Any(n => n.vanish == mode), mode + " の区間がある");
        Assert.IsTrue(vanish.notes.Where(n => n.IsLong).All(n => n.vanish == ""), "ロングは消さない(とどまる間も切る)");
        Assert.IsTrue(vanish.notes.Where(n => n.color == "gold").All(n => n.vanish != "hide"), "金は目印に残す");
        Assert.AreEqual("", vanish.notes.Last().vanish, "最後は全部見える");
        Assert.AreSame(RushESecretState.ZeroVanish, RushESecretState.ForDifficulty("V"));
    }
}
