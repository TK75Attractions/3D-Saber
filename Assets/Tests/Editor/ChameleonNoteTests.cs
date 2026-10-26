using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// C/U12。表示だけの変化と、既存の暗さ・透明度・再利用との合成を確かめる。
public class ChameleonNoteTests
{
    GameObject root;
    NoteSpawner spawner;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("ChameleonNoteTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.approachTime = 1f;
        spawner.ConfigureFloorGuide(-3f);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    CuttableNote Spawn(string color = "red", float reveal = .4f, bool arrow = true,
        string vanish = "", float veil = 0f)
    {
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData {
            time = 2000, x = color == "blue" ? -1.05f : 1.05f, y = -.45f,
            color = color, type = "long", direction = "left", count = 2, lengthMs = 1200,
            reveal = reveal, revealArrow = arrow, vanish = vanish, veil = veil
        });
        spawner.SetChart(chart);
        spawner.Tick(1.45);
        return spawner.LiveNotes.Single();
    }

    static Color Read(Material material) => material.HasProperty("_BaseColor")
        ? material.GetColor("_BaseColor") : material.color;

    static void Rgb(Color expected, Color actual)
    {
        Assert.AreEqual(expected.r, actual.r, 1e-5);
        Assert.AreEqual(expected.g, actual.g, 1e-5);
        Assert.AreEqual(expected.b, actual.b, 1e-5);
    }

    Mesh FloorMesh => spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh;

    [TestCase("red")]
    [TestCase("blue")]
    [TestCase("gold")]
    public void BodyFrameFloorCountAndArrowsRevealWithoutChangingTheNote(string color)
    {
        var note = Spawn(color);
        var visuals = note.Visuals;
        Color original = visuals.baseColor;
        Color chalk = ChameleonRules.Chalk;
        var body = note.BodyRenderer.sharedMaterial;
        var frame = note.TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        var rails = visuals.FindChildByName("EdgeRails").GetComponent<MeshRenderer>().sharedMaterial;
        Color originalRails = Color.Lerp(original, Color.white, .45f);
        float bodyAlpha = Read(body).a;
        Rgb(chalk, Read(body));
        Rgb(chalk, Read(rails));
        Rgb(chalk, Read(frame));
        Rgb(chalk, note.countLabel.color);
        Assert.IsFalse(note.countLabel.enableVertexGradient);
        Assert.AreEqual("2", note.countLabel.text);
        Assert.IsFalse(note.Arrow.gameObject.activeSelf);
        Assert.AreEqual(28, FloorMesh.vertexCount, "判定線8頂点＋中立のロング開始印20頂点");
        Rgb(Color.Lerp(chalk, Color.white, .22f), FloorMesh.colors[12]);

        spawner.Tick(1.63);
        Assert.That(visuals.RevealAmount, Is.InRange(.49f, .51f));
        Rgb(Color.Lerp(chalk, original, .5f), Read(body));
        Rgb(Color.Lerp(Color.Lerp(chalk, originalRails, .5f), Color.white, .35f), Read(rails));
        Rgb(visuals.DisplayColor, Read(frame));
        Rgb(visuals.DisplayColor, note.countLabel.color);
        Assert.IsTrue(note.Arrow.gameObject.activeSelf);
        Assert.AreEqual(8 + FlickArrowShape.Points.Length * 2, FloorMesh.vertexCount);

        spawner.Tick(1.67);
        Rgb(original, Read(body));
        Rgb(originalRails, Read(rails));
        Rgb(original, Read(frame));
        Rgb(original, note.countLabel.color);
        Color arrowFloor = Color.Lerp(Color.Lerp(original, Color.white, .22f), Color.white, .75f);
        Rgb(arrowFloor, FloorMesh.colors[8 + FlickArrowShape.OutlinePoints.Length]);
        Assert.AreEqual(bodyAlpha, Read(body).a, 1e-6);
        Assert.AreEqual(0f, note.Veil);
        Assert.AreEqual(0f, visuals.Veil);
        Assert.AreEqual(SaberHandHelper.FromColor(color), note.RequiredHand);
        Assert.AreEqual(color == "gold", note.IsGold);
        Assert.AreEqual(CutDirection.Left, note.RequiredDirection);
        Assert.AreEqual(2, note.RequiredCutCount);
        Assert.AreEqual(1.2f, note.OverrideLingerSeconds, 1e-6);
        Assert.AreEqual(2d, note.HitTime);
        Assert.AreEqual(note.LandingPosition, (Vector2)note.transform.position);
        spawner.Tick(2d);
        Assert.AreEqual(spawner.judgeZ, note.transform.position.z, 1e-6);
    }

    [Test]
    public void DarknessAndVeilUseOriginalColorsAndKeepTheirAlpha()
    {
        var note = Spawn(veil: .25f);
        var body = note.BodyRenderer.sharedMaterial;
        float alpha = Read(body).a;
        spawner.BrightnessFor = _ => .2f;
        spawner.Tick(1.45);
        Rgb(DarkRules.Rgb(ChameleonRules.Chalk, .2f), Read(body));
        Rgb(DarkRules.Rgb(ChameleonRules.Chalk, .2f), note.countLabel.color);
        Rgb(DarkRules.Rgb(ChameleonRules.Chalk, .2f),
            Read(note.TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>().sharedMaterial));
        Rgb(DarkRules.Rgb(Color.Lerp(ChameleonRules.Chalk, Color.white, .22f), .2f),
            FloorMesh.colors[12]);
        Assert.AreEqual(alpha, Read(body).a, 1e-6);
        spawner.Tick(1.67);
        Rgb(DarkRules.Rgb(note.Visuals.baseColor, .2f), Read(body));
        Rgb(DarkRules.Rgb(note.Visuals.baseColor, .2f), note.countLabel.color);
        spawner.BrightnessFor = _ => 1f;
        spawner.Tick(1.67);
        Rgb(note.Visuals.baseColor, Read(body));
        Assert.AreEqual(alpha, Read(body).a, 1e-6);
        // 同じ曲時計に戻しても、暗くした赤を元色として覚えない。
        spawner.Tick(1.45);
        Rgb(ChameleonRules.Chalk, Read(body));
        Assert.IsFalse(note.Arrow.gameObject.activeSelf);
    }

    [TestCase("hide")]
    [TestCase("fade")]
    public void RevealDoesNotUndoVanish(string vanish)
    {
        var note = Spawn(vanish: vanish);
        spawner.Tick(1.8);
        Assert.AreEqual(1f, note.Visuals.Veil);
        Assert.IsFalse(note.BodyRenderer.enabled);
        if (vanish == "hide")
        {
            Assert.AreEqual(1f, note.Veil);
            Assert.IsFalse(note.Arrow.gameObject.activeSelf);
            Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf);
            Assert.IsFalse(note.countLabel.gameObject.activeSelf);
            Assert.AreEqual(0, spawner.FloorGuide.MarkerCount);
        }
        else
        {
            Assert.AreEqual(0f, note.Veil);
            Assert.IsTrue(note.Arrow.gameObject.activeSelf);
            Assert.IsTrue(note.TimingCue.GhostRoot.activeSelf);
        }
    }

    [Test]
    public void MemoryVeilStillHidesAllCues()
    {
        var note = Spawn(veil: 1f);
        spawner.Tick(1.67);
        Assert.IsFalse(note.BodyRenderer.enabled);
        Assert.IsFalse(note.Arrow.gameObject.activeSelf);
        Assert.IsFalse(note.countLabel.gameObject.activeSelf);
        Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf);
        Assert.AreEqual(0, spawner.FloorGuide.MarkerCount);
    }

    [Test]
    public void ReuseRestoresPlainMaterialsCountGradientAndArrow()
    {
        var note = Spawn(reveal: 0);
        var materials = note.GetComponentsInChildren<MeshRenderer>(true)
            .Select(r => r.sharedMaterial).Where(m => m != null).Distinct().ToArray();
        var original = materials.Select(Read).ToArray();
        var gradient = note.countLabel.colorGradient;
        bool useGradient = note.countLabel.enableVertexGradient;
        Color count = note.countLabel.color;

        note.ConfigureReveal(new NoteData { reveal = .4f, revealArrow = true }, .55);
        note.SetDarkBrightness(.2f);
        Assert.IsFalse(note.Arrow.gameObject.activeSelf);
        note.ResetForSpawn();
        note.Visuals.ResetForReuse();
        note.ConfigureReveal(new NoteData(), .55);
        note.Arrow.Bind(note, note.Visuals.baseColor);
        note.TimingCue.Initialize(note, spawner.judgeZ);
        note.TickReveal(.55);
        note.TimingCue.Tick(.55, 1, spawner.earlyJudgeWindow, spawner.LateWindowFor(note));
        for (int i = 0; i < materials.Length; i++) Rgb(original[i], Read(materials[i]));
        Rgb(count, note.countLabel.color);
        Assert.AreEqual(useGradient, note.countLabel.enableVertexGradient);
        Assert.AreEqual(gradient.bottomLeft, note.countLabel.colorGradient.bottomLeft);
        Assert.AreEqual(0f, note.RevealSeconds);
        Assert.AreEqual(1f, note.Visuals.RevealAmount);
        Assert.IsTrue(note.Arrow.gameObject.activeSelf);
    }

    [Test]
    public void PlainNotesAndNonChorusArrowsKeepTheirUsualDisplay()
    {
        var plain = Spawn(reveal: 0);
        Rgb(plain.Visuals.baseColor, Read(plain.BodyRenderer.sharedMaterial));
        Rgb(Color.white, plain.countLabel.color);
        Assert.IsTrue(plain.countLabel.enableVertexGradient);
        Assert.AreEqual(LongNoteCountStyle.BottomColor, plain.countLabel.colorGradient.bottomLeft);
        Assert.IsTrue(plain.Arrow.gameObject.activeSelf);
        var nonChorus = Spawn(arrow: false);
        Rgb(ChameleonRules.Chalk, Read(nonChorus.BodyRenderer.sharedMaterial));
        Assert.IsTrue(nonChorus.Arrow.gameObject.activeSelf);
    }
}

public class ChameleonChartTests
{
    [Test]
    public void LoaderClampsRevealAndOldNotesRemainPlain()
    {
        var chart = ChartLoader.Parse("{\"notes\":["
            + "{\"time\":0,\"revealArrow\":true,\"direction\":\"left\"},"
            + "{\"time\":1,\"reveal\":-1},"
            + "{\"time\":2,\"reveal\":0.01,\"revealArrow\":true,\"direction\":\"left\"},"
            + "{\"time\":3,\"reveal\":99,\"revealArrow\":true}]}");
        CollectionAssert.AreEqual(new[] { 0f, 0f, .4f, 1.5f }, chart.notes.Select(n => n.reveal));
        CollectionAssert.AreEqual(new[] { false, false, true, false }, chart.notes.Select(n => n.revealArrow));
        Assert.AreEqual(0, ChameleonRules.Normalize(float.NaN));
        Assert.AreEqual(0, ChameleonRules.Normalize(float.PositiveInfinity));
        Assert.AreEqual(0, ChameleonRules.Normalize(float.NegativeInfinity));
        Assert.AreEqual(1, ChameleonRules.Amount(.4f, double.NaN));
        Assert.AreEqual(0, ChameleonRules.Amount(.4f, .4001));
        Assert.AreEqual(.5, ChameleonRules.Amount(.4f, .37), 1e-5);
        Assert.AreEqual(1, ChameleonRules.Amount(.4f, .34), 1e-5);
        Assert.GreaterOrEqual(.4 - ChameleonRules.TransitionSeconds - 1d / 30, .3);
    }

    [Test]
    public void CPreservesTheNormalHardMixAndIsLastInTheSecretTable()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", "BenkyouRemaster");
        string path = Path.Combine(folder, "chart_c.json");
        Assert.IsTrue(File.Exists(path), "先にgen_chameleon.py --writeで生成する");
        var chart = ChartLoader.Parse(File.ReadAllText(path));
        MixedSecretChartAssertions.Check("BenkyouRemaster", chart, "c", 50424, 53, 208);
        Assert.AreEqual(9, chart.displayLevel, "位置から手が読めるため、後半HardのLv9を維持");
        var stage = JsonUtility.FromJson<StagePerformanceTimeline>(File.ReadAllText(Path.Combine(folder, "stage.json")));
        Assert.AreEqual(4, stage.sections.Length);
        foreach (var note in chart.notes)
        {
            Assert.AreEqual(.4f, note.reveal);
            double t = note.TimeSeconds + chart.offsetMs / 1000d;
            bool chorus = stage.sections.Any(s => s.startSeconds <= t && t < s.endSeconds);
            Assert.AreEqual(note.IsDirection && chorus, note.revealArrow, note.time + "ms");
            Assert.GreaterOrEqual(note.reveal - ChameleonRules.TransitionSeconds - 1d / 30, .3);
            if (note.color == "red") Assert.Greater(note.x, 0f);
            if (note.color == "blue") Assert.Less(note.x, 0f);
        }
        Assert.AreEqual(2, chart.notes.Count(n => n.revealArrow));
        var secret = RushESecretState.BenkyouChameleon;
        Assert.AreSame(secret, RushESecretState.For("BenkyouRemaster"));
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" c "));
        Assert.AreSame(secret, RushESecretState.Charts.Last());
        Assert.AreSame(RushESecretState.YurikagoOvertake,
            RushESecretState.Charts[RushESecretState.Charts.Length - 2]);
        Assert.AreEqual("C", secret.Difficulty);
        Assert.AreEqual(ChameleonRules.Chalk, secret.Accent);
        Assert.AreEqual(secret.Accent, GameHUDSkin.DifficultyColor("C"));
        Assert.AreEqual(secret.Accent, GameStartCountdown.AccentForDifficulty("c"));
        var state = new RushESecretState();
        state.Select("BenkyouRemaster");
        for (int i = 0; i < 10; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.AreSame(secret, state.Current);
    }
}
