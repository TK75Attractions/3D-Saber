using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;

// 曲の前の 3・2・1(2026-10-05 Web 試作の案A「斬る数字」+ 案B「拍ごとに点くゲートの辺」)。
public class CountInCountdownTests
{
    readonly List<Object> created = new List<Object>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
        created.Clear();
        foreach (var c in Object.FindObjectsByType<GameStartCountdown>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Object.DestroyImmediate(c.gameObject);
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    GameStartCountdown NewCountdown()
    {
        var countdown = GameStartCountdown.Ensure();
        created.Add(countdown.gameObject);
        return countdown;
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root) { var hit = FindDeep(child, name); if (hit != null) return hit; }
        return null;
    }

    [TestCase(100f, .6)]
    [TestCase(92f, 60.0 / 92)]
    [TestCase(130f, 60.0 / 130)]
    [TestCase(148f, 120.0 / 148)]          // 1拍 0.405 秒は短いので2拍で1カウント
    [TestCase(173f, 120.0 / 173)]
    [TestCase(57.333333f, 60.0 / 57.333333)] // 12/8 の付点4分(1.05秒)はそのまま
    [TestCase(0f, .5)]
    public void OneCountIsABeatOrTwoBeatsForFastSongs(float bpm, double expected)
    {
        Assert.AreEqual(expected, GameStartCountdown.CountSeconds(bpm), 1e-4);
    }

    [Test]
    public void CountsNeverFlashMoreThanAboutTwiceASecond()
    {
        foreach (float bpm in new[] { 57.33f, 90f, 92f, 100f, 118f, 130f, 148f, 173f, 200f, 260f })
            Assert.GreaterOrEqual(GameStartCountdown.CountSeconds(bpm), GameStartCountdown.MinCountSeconds - 1e-9, bpm + " BPM");
    }

    [TestCase(.524, .224)]
    [TestCase(2.4, .42)]
    [TestCase(.3, .22)]
    [TestCase(double.NaN, .42)]
    public void StartWordLeavesBeforeTheFirstNoteReachesTheGate(double firstNote, double expected)
    {
        Assert.AreEqual(expected, GameStartCountdown.StartHoldSeconds(firstNote), 1e-6);
    }

    [Test]
    public void GateEdgesLightLeftThenRightThenTopAndBottom()
    {
        Assert.IsTrue(CountInGateCue.IsLit(CountInGateCue.Side.Left, 0));
        Assert.IsFalse(CountInGateCue.IsLit(CountInGateCue.Side.Right, 0));
        Assert.IsTrue(CountInGateCue.IsLit(CountInGateCue.Side.Right, 1));
        Assert.IsFalse(CountInGateCue.IsLit(CountInGateCue.Side.Top, 1));
        Assert.IsTrue(CountInGateCue.IsLit(CountInGateCue.Side.Top, 2));
        Assert.IsTrue(CountInGateCue.IsLit(CountInGateCue.Side.Bottom, 2));
        var accent = UISkinPalette.LogoGreen;
        Assert.AreEqual(UISkinPalette.LogoBlue, CountInGateCue.ColorFor(CountInGateCue.Side.Left, 0, accent), "左手と同じ青");
        Assert.AreEqual(UISkinPalette.LogoRed, CountInGateCue.ColorFor(CountInGateCue.Side.Right, 1, accent), "右手と同じ赤");
        Assert.AreEqual(Color.white, CountInGateCue.ColorFor(CountInGateCue.Side.Top, 2, accent));
        Assert.AreEqual(accent, CountInGateCue.ColorFor(CountInGateCue.Side.Left, 3, accent), "START は難易度の色");
    }

    [Test]
    public void ShowsOnlyTheNumbersAndStartWithoutTheVeilOrFrame()
    {
        var countdown = NewCountdown();
        countdown.Begin(100f, "normal", 0f);
        var texts = countdown.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t => t.text).Distinct().ToArray();
        CollectionAssert.IsSubsetOf(texts, new[] { "3", "2", "1", "START" }, "英語の小さな文字は出さない");
        foreach (var name in new[] { "StageVeil", "CenterShade", "DiamondFrame", "RedSyncRail", "BlueSyncRail", "Status", "Bpm", "Follow" })
            Assert.IsNull(FindDeep(countdown.transform, name), name + " は使わない");
        Assert.AreEqual(0f, countdown.GetComponent<CanvasGroup>().alpha, "最初の拍までは何も見せない");
    }

    [Test]
    public void EachNumberIsCutOnTheNextBeatAndStartIsTheBiggest()
    {
        var countdown = NewCountdown();
        countdown.Begin(100f, "hard", 0f, 0, 2.4);
        double first = countdown.FirstBeatDspTime, c = countdown.CountLength;
        countdown.Apply(first + .1);
        Assert.AreEqual(1f, countdown.GetComponent<CanvasGroup>().alpha);
        Assert.IsTrue(countdown.NumberAt(0).Root.gameObject.activeSelf, "「3」が出ている");
        Assert.IsFalse(countdown.NumberAt(0).ShowingPieces);
        countdown.Apply(first + c + .1);
        Assert.IsTrue(countdown.NumberAt(0).ShowingPieces, "次の拍で「3」が斬られて割れる");
        Assert.AreEqual(2, countdown.NumberAt(0).PieceCount);
        Assert.IsTrue(countdown.NumberAt(1).Root.gameObject.activeSelf, "同じ拍で「2」が出る");
        Assert.Greater(countdown.ActiveSparkCount, 0, "斬った所から火花が散る");
        Assert.AreEqual(4, countdown.NumberAt(2).PieceCount, "「1」は青と赤の X で4つに割れる");
        countdown.Apply(countdown.SongStartDspTime + .05);
        Assert.IsTrue(countdown.NumberAt(2).ShowingPieces, "START の瞬間に「1」が割れる");
        Assert.IsTrue(countdown.StartWord.Root.gameObject.activeSelf, "START が出る");
        Assert.Greater(countdown.StartWord.Whole.preferredWidth, countdown.NumberAt(0).Whole.preferredWidth * 1.5f, "START は数字より大きい");
        countdown.Apply(countdown.SongStartDspTime + countdown.StartHold + .1);
        Assert.IsTrue(countdown.StartWord.ShowingPieces, "START も最初のノーツの前に斬られて消える");
    }

    // 2026-10-05 の撮影で見つけた不具合: 片の文字が切り抜きの四角の中心に置かれ、画面の外に出ていた。
    [Test]
    public void CutPiecesStartExactlyWhereTheWholeTextWas()
    {
        var countdown = NewCountdown();
        countdown.Begin(100f, "normal", 0f);
        var all = new[] { countdown.NumberAt(0), countdown.NumberAt(1), countdown.NumberAt(2), countdown.StartWord };
        foreach (var text in all)
        {
            text.ShowPieces(0f, 108f, 44f);
            for (int p = 0; p < text.PieceCount; p++)
            {
                var piece = text.PieceText(p).transform;
                Assert.Less(Vector3.Distance(piece.position, text.Whole.transform.position), .5f, text.Root.name + " の片" + p + " は元の文字と同じ位置から割れ始める");
                Assert.Less(Quaternion.Angle(piece.rotation, text.Whole.transform.rotation), .5f, text.Root.name + " の片" + p + " は傾かずに始まる");
            }
        }
    }

    [Test]
    public void ReducedEffectsKeepsTheCutsButDropsTheSparks()
    {
        DisplaySettings.SetReducedEffectsForTest(true);
        var countdown = NewCountdown();
        countdown.Begin(100f, "hard", 0f);
        countdown.Apply(countdown.FirstBeatDspTime + countdown.CountLength + .05);
        Assert.IsTrue(countdown.NumberAt(0).ShowingPieces);
        Assert.AreEqual(0, countdown.ActiveSparkCount);
    }

    [Test]
    public void GateEdgesAndFloorLightPerBeatAndAreRestoredAfterStart()
    {
        var gateGo = new GameObject("JudgeGate"); created.Add(gateGo);
        gateGo.AddComponent<JudgeGateFrame>().Build(new Vector3(4, 2, 1));
        var spawnerGo = new GameObject("CountInSpawner"); created.Add(spawnerGo);
        var spawner = spawnerGo.AddComponent<NoteSpawner>();
        spawner.ConfigureFloorGuide(0f);
        var countdown = NewCountdown();
        double start = countdown.Begin(100f, "easy", 0f, 0, 2.4);
        double first = countdown.FirstBeatDspTime, c = countdown.CountLength;
        var left = gateGo.transform.Find("GateLeft").GetComponent<MeshRenderer>();
        var right = gateGo.transform.Find("GateRight").GetComponent<MeshRenderer>();
        var top = gateGo.transform.Find("GateTop").GetComponent<MeshRenderer>();
        var block = new MaterialPropertyBlock();

        countdown.Apply(first + .1);
        left.GetPropertyBlock(block); Color l = block.GetColor("_EmissionColor");
        right.GetPropertyBlock(block); Color r = block.GetColor("_EmissionColor");
        Assert.Greater(l.b, l.r, "3: 左の辺が青く点く");
        Assert.Less(r.maxColorComponent, l.maxColorComponent, "まだ点かない右の辺は暗い");
        Assert.Greater(spawner.FloorGuide.CountTintAmount, 0f, "床の判定線も色付く");

        countdown.Apply(first + c + .1);
        right.GetPropertyBlock(block); r = block.GetColor("_EmissionColor");
        Assert.Greater(r.r, r.b, "2: 右の辺が赤く点く");

        countdown.Apply(first + 2 * c + .1);
        top.GetPropertyBlock(block); Color t = block.GetColor("_EmissionColor");
        Assert.Greater(Mathf.Min(t.r, Mathf.Min(t.g, t.b)), .5f, "1: 上下の辺が白く点く");

        countdown.Apply(start + .05);
        left.GetPropertyBlock(block); l = block.GetColor("_EmissionColor");
        Assert.Greater(l.g, l.r, "START: ゲート全体が難易度(EASY の緑)の色");

        countdown.Apply(countdown.EndDspTime + .01);
        Assert.IsFalse(left.HasPropertyBlock(), "終わったら上書きを外して元の材質に戻す");
        Assert.IsFalse(right.HasPropertyBlock());
        Assert.IsFalse(top.HasPropertyBlock());
        Assert.AreEqual(0f, spawner.FloorGuide.CountTintAmount, "床も白に戻す");
        Assert.IsTrue(countdown == null, "演出が終わったら消える");
    }

    static float Peak(float[] s) { float m = 0; foreach (var v in s) m = Mathf.Max(m, Mathf.Abs(v)); return m; }

    [Test]
    public void CountSoundsAreShortAndTheStartSoundIsLonger()
    {
        var sounds = new[] { ProceduralSfx.CountTick(), ProceduralSfx.CountSwish(), ProceduralSfx.CountStart() };
        foreach (var s in sounds)
        {
            foreach (var v in s) Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v));
            Assert.LessOrEqual(Peak(s), .91f, "割れない");
            Assert.Greater(Peak(s), .3f, "無音ではない");
            Assert.Less(Mathf.Abs(s[s.Length - 1]), .02f, "最後は無音へ収まる");
        }
        var tick = sounds[0];
        Assert.AreEqual(.07f, tick.Length / (float)ProceduralSfx.Rate, .002f, "拍の音は約70ms");
        Assert.GreaterOrEqual(sounds[2].Length, tick.Length * 4, "START は拍の音の4倍以上長い");
        // 立ち上がりが速いほど、鳴ったと感じる時刻が拍の頭にそろう
        float early = 0;
        for (int i = 0; i < (int)(.005f * ProceduralSfx.Rate); i++) early = Mathf.Max(early, Mathf.Abs(tick[i]));
        Assert.Greater(early, Peak(tick) * .5f, "最初の5msで大きく立ち上がる");
    }
}
