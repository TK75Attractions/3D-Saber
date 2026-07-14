using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class UISkinTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
    }

    private Canvas MakeCanvas()
    {
        var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        created.Add(go);
        return go.GetComponent<Canvas>();
    }

    [Test]
    public void Backdrop_Ensure_AddsBackdropAsFirstSibling()
    {
        var canvas = MakeCanvas();
        // 既存 UI を1個入れて、backdrop が前に挿入されることを確認
        var ui = new GameObject("ExistingUI", typeof(RectTransform));
        ui.transform.SetParent(canvas.transform, false);

        var backdrop = CyberBackdrop.Ensure(canvas);
        Assert.IsNotNull(backdrop);
        Assert.AreEqual(0, backdrop.transform.GetSiblingIndex(),
            "backdrop は Canvas の最初の子であるべき");
    }

    [Test]
    public void Backdrop_Ensure_IsIdempotent()
    {
        var canvas = MakeCanvas();
        var a = CyberBackdrop.Ensure(canvas);
        var b = CyberBackdrop.Ensure(canvas);
        Assert.AreSame(a, b);
        // 子の中に CyberBackdrop が1つだけ
        var found = canvas.GetComponentsInChildren<CyberBackdrop>(true);
        Assert.AreEqual(1, found.Length);
    }

    [Test]
    public void Backdrop_DefaultsToMinimal_GradientGlowVignetteOnly()
    {
        // 既定はミニマル:グラデ + 中央グロー + ビネットのみ。テンプレ要素は生成しない。
        var canvas = MakeCanvas();
        var backdrop = CyberBackdrop.Ensure(canvas);
        Assert.AreEqual(BackdropStyle.Minimal, backdrop.style);
        Assert.IsNotNull(backdrop.transform.Find("Gradient"), "グラデはある");
        Assert.IsNotNull(backdrop.transform.Find("CenterGlow"), "中央グローはある");
        Assert.IsNotNull(backdrop.transform.Find("Vignette"), "ビネットはある");
        // 「デモ感」の主因だったテンプレ要素が無いこと
        Assert.IsNull(backdrop.transform.Find("ScanLines"), "スキャンラインは無い");
        Assert.IsNull(backdrop.transform.Find("GlowLine"), "動く光線は無い");
        Assert.IsNull(backdrop.transform.Find("HorizonGrid"), "床グリッドは無い");
        Assert.IsNull(backdrop.transform.Find("Beams"), "斜め光線は無い");
        Assert.IsNull(backdrop.transform.Find("Particles"), "浮遊ドットは無い");
    }

    [Test]
    public void Backdrop_CyberStyle_CreatesTemplateElements()
    {
        // Cyber は明示オプトインで従来の盛りだくさん構成
        var canvas = MakeCanvas();
        var backdrop = CyberBackdrop.Ensure(canvas, BackdropStyle.Cyber);
        Assert.AreEqual(BackdropStyle.Cyber, backdrop.style);
        Assert.IsNotNull(backdrop.transform.Find("Gradient"));
        Assert.IsNotNull(backdrop.transform.Find("ScanLines"));
        Assert.IsNotNull(backdrop.transform.Find("GlowLine"));
    }

    [Test]
    public void SaberTitleBackdrop_Ensure_BuildsNeonArenaOnce()
    {
        var canvas = MakeCanvas();
        var a = SaberTitleBackdrop.Ensure(canvas);
        var b = SaberTitleBackdrop.Ensure(canvas);

        Assert.AreSame(a, b, "タイトル専用背景は重複生成しない");
        Assert.AreEqual(0, a.transform.GetSiblingIndex(), "背景は Canvas の最背面に置く");
        Assert.IsNotNull(a.transform.Find("Gradient"));
        Assert.IsNotNull(a.transform.Find("StarField"));
        Assert.IsNotNull(a.transform.Find("PerspectiveStage/Grid"));
        Assert.IsNotNull(a.transform.Find("ArenaRails/RedLowerRailCore"));
        Assert.IsNotNull(a.transform.Find("ArenaRails/BlueLowerRailCore"));
        Assert.IsNotNull(a.transform.Find("PerspectiveStage/HorizonCore"));
    }

    [Test]
    public void TitleSceneSkin_FindTextByContent_ReturnsMatch()
    {
        var canvas = MakeCanvas();
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(canvas.transform, false);
        var t = go.GetComponent<Text>();
        t.text = "3D SABER";

        var found = TitleSceneSkin.FindTextByContent(canvas, "3D SABER");
        Assert.AreSame(t, found);
    }

    [Test]
    public void TitleSceneSkin_FindTextByContent_NotFound_ReturnsNull()
    {
        var canvas = MakeCanvas();
        var found = TitleSceneSkin.FindTextByContent(canvas, "Nothing");
        Assert.IsNull(found);
    }

    [TestCase(-1, 0)]
    [TestCase(0, 0)]
    [TestCase(1, 1)]
    [TestCase(5, 5)]
    [TestCase(10, 10)]
    [TestCase(12, 10)]
    public void DifficultyMeter_ClampsToTenSegments(int level, int expected)
    {
        Assert.AreEqual(expected, SongSelectSkin.MeterSegmentsForLevel(level));
    }

    [TestCase(0, "LEVEL --")]
    [TestCase(1, "LEVEL 01")]
    [TestCase(10, "LEVEL 10")]
    public void DifficultyLevel_FormatsAsTwoDigits(int level, string expected)
    {
        Assert.AreEqual(expected, SongSelectSkin.FormatDifficultyLevel(level));
    }

    [TestCase(0, "Easy", "EASY")]
    [TestCase(1, "Normal", "NORMAL")]
    [TestCase(2, "Hard", "MASTER")]
    public void DifficultyRibbon_UsesRequestedDisplayNames(int index, string source, string expected)
    {
        Assert.AreEqual(expected, SongSelectSkin.DifficultyDisplayName(index, source));
    }

    [Test]
    public void DifficultyRibbon_DefaultsToEasyWithRequestedLevels()
    {
        var go = new GameObject("SongSelectController");
        created.Add(go);
        var controller = go.AddComponent<SongSelectController>();

        Assert.AreEqual(0, controller.SelectedDifficultyIndex);
        Assert.AreEqual(4, controller.DifficultyDisplayLevelAt(0));
        Assert.AreEqual(6, controller.DifficultyDisplayLevelAt(1));
        Assert.AreEqual(8, controller.DifficultyDisplayLevelAt(2));
    }

    [Test]
    public void DifficultyStar_AlternatesOuterAndInnerVertices()
    {
        Assert.AreEqual(1f, DifficultyStarGraphic.NormalizedPoint(0).magnitude, 0.001f);
        Assert.AreEqual(0.48f, DifficultyStarGraphic.NormalizedPoint(1).magnitude, 0.001f);
        Assert.Greater(DifficultyStarGraphic.NormalizedPoint(0).y, 0.99f);
    }

    [Test]
    public void DifficultyRibbon_SelectionEaseHasSoftOvershoot()
    {
        Assert.AreEqual(0f, DifficultyRibbonItem.EaseOutBack(0f), 0.001f);
        Assert.Greater(DifficultyRibbonItem.EaseOutBack(0.7f), 1f);
        Assert.AreEqual(1f, DifficultyRibbonItem.EaseOutBack(1f), 0.001f);
    }

    [TestCase(120f, 0.5)]
    [TestCase(150f, 0.4)]
    [TestCase(0f, 0.5)]
    public void StartCountdown_UsesChartBeatLength(float bpm, double expected)
    {
        Assert.AreEqual(expected, GameStartCountdown.BeatSeconds(bpm), 0.0001);
    }

    [TestCase(9.9, 10.0, 0.5, 0)]
    [TestCase(10.0, 10.0, 0.5, 0)]
    [TestCase(10.5, 10.0, 0.5, 1)]
    [TestCase(11.0, 10.0, 0.5, 2)]
    [TestCase(11.5, 10.0, 0.5, 3)]
    [TestCase(20.0, 10.0, 0.5, 3)]
    public void StartCountdown_AdvancesOneStepPerBeat(
        double now, double firstBeat, double beatLength, int expected)
    {
        Assert.AreEqual(expected, GameStartCountdown.StepAt(now, firstBeat, beatLength));
    }

    [TestCase(-1, "3")]
    [TestCase(0, "3")]
    [TestCase(1, "2")]
    [TestCase(2, "1")]
    [TestCase(3, "START!")]
    [TestCase(9, "START!")]
    public void StartCountdown_UsesRequestedLabels(int step, string expected)
    {
        Assert.AreEqual(expected, GameStartCountdown.TokenForStep(step));
    }
}
