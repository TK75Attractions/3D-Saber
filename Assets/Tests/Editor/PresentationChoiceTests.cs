using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

public class WrongDirectionCueTests
{
    [Test]
    public void SameSwingAndSuppressedSwingsCannotRepeat()
    {
        var gate = new WrongDirectionCueGate();
        Assert.True(gate.TrySignal(1, 0));
        Assert.False(gate.TrySignal(2, .249));
        Assert.True(gate.TrySignal(3, .25), "境界の0.25秒から次の振りを通知できる");
        Assert.False(gate.TrySignal(2, .5), "抑制済みの同じ振りを後から通知しない");
        Assert.False(gate.TrySignal(1, 1));
        Assert.True(gate.TrySignal(4, 1));
        gate.Reset();
        Assert.True(gate.TrySignal(1, 0), "再利用で履歴と待ち時間を消す");
        Assert.AreNotEqual(WrongDirectionCueGate.NewSwing(), WrongDirectionCueGate.NewSwing());
    }

    [Test]
    public void WrongDirectionOnlyTintsBarsAndLeavesNoteUnjudged()
    {
        var go = new GameObject("WrongDirectionTest");
        try
        {
            var note = go.AddComponent<CuttableNote>();
            note.RequiredDirection = CutDirection.Right;
            NoteSpawner.BuildArrow(go.transform, note.RequiredDirection);
            var arrow = note.Arrow;
            arrow.Bind(note, Color.red);
            var bars = go.transform.Find("Arrow/Bars").GetComponent<MeshRenderer>().sharedMaterial;
            var backing = go.transform.Find("Arrow/ArrowBacking").GetComponent<MeshRenderer>().sharedMaterial;
            Color black = backing.GetColor(NoteArrowMaterials.BaseColorId);
            int signals = 0, cuts = 0, judgments = 0, misses = 0;
            note.OnWrongDirection += _ => signals++;
            note.OnCut += (_, __, ___) => cuts++;
            note.OnJudged += (_, __, ___, ____) => judgments++;
            note.OnMiss += _ => misses++;
            ulong swing = WrongDirectionCueGate.NewSwing();

            note.Cut(Vector3.zero, Vector3.left * 8, CutDirection.None, SaberHand.Any, swing);
            note.Cut(Vector3.zero, Vector3.left * 8, CutDirection.None, SaberHand.Any, swing);
            Assert.AreEqual(1, signals);
            Assert.Zero(cuts + judgments + misses);
            Assert.False(note.IsCut || note.IsMissed || note.IsFinalized);
            Assert.AreEqual(1, note.RemainingCuts);
            Assert.AreEqual(Color.red, bars.GetColor(NoteArrowMaterials.BaseColorId));
            Assert.AreEqual(black, backing.GetColor(NoteArrowMaterials.BaseColorId));
            arrow.Tick(.1f);
            Assert.AreEqual(Color.Lerp(Color.red, Color.white, .5f), bars.GetColor(NoteArrowMaterials.BaseColorId));
            arrow.Tick(.1f);
            Assert.AreEqual(Color.white, bars.GetColor(NoteArrowMaterials.BaseColorId));

            note.ResetForSpawn();
            arrow.Bind(note, Color.blue);
            note.OnWrongDirection += _ => signals++;
            note.Cut(Vector3.zero, Vector3.left * 8, CutDirection.None, SaberHand.Any, swing);
            Assert.AreEqual(2, signals);
            Assert.AreEqual(Color.blue, bars.GetColor(NoteArrowMaterials.BaseColorId));
            go.SetActive(false);
            // EditModeでも無効化時の片付けを明示して点検する。
            typeof(NoteArrowMaterials).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(arrow, null);
            Assert.AreEqual(Color.white, bars.GetColor(NoteArrowMaterials.BaseColorId));
            Assert.AreEqual(black, backing.GetColor(NoteArrowMaterials.BaseColorId));
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void VisualOnlyAndWrongHandDoNotSignal()
    {
        var go = new GameObject("DirectionExclusions");
        try
        {
            var note = go.AddComponent<CuttableNote>();
            note.RequiredDirection = CutDirection.Right;
            note.RequiredHand = SaberHand.Right;
            int signals = 0;
            note.OnWrongDirection += _ => signals++;
            note.Cut(Vector3.zero, Vector3.left * 8, CutDirection.None, SaberHand.Left);
            Assert.Zero(signals);
            Assert.False(note.IsCut);
            note.DirectionVisualOnly = true;
            note.Cut(Vector3.zero, Vector3.left * 8, CutDirection.None, SaberHand.Right);
            Assert.Zero(signals);
            Assert.True(note.IsCut);
        }
        finally { Object.DestroyImmediate(go); }
    }
}

public class ComboBreakTests
{
    [TestCase(9, 0, false)]
    [TestCase(10, 0, true)]
    [TestCase(100, 0, true)]
    [TestCase(10, 1, false)]
    [TestCase(0, 0, false)]
    public void BreakThreshold(int before, int after, bool expected)
        => Assert.AreEqual(expected, GameHUDSkin.ShouldBreakCombo(before, after));

    [TestCase(0f, false, 0f, 1f, 0f, 1f)]
    [TestCase(.14f, false, -15f, .91f, 1f, .5f)]
    [TestCase(.28f, false, -60f, .82f, 1f, 0f)]
    [TestCase(.1f, true, 0f, 1f, 0f, .5f)]
    [TestCase(.2f, true, 0f, 1f, 0f, 0f)]
    public void PoseMatchesSelectedValues(float age, bool reduced, float y, float scale, float gray, float alpha)
    {
        GameHUDSkin.ComboBreakFrame(age, reduced, out float actualY, out float actualScale, out float actualGray, out float actualAlpha);
        Assert.AreEqual(y, actualY, 1e-5f);
        Assert.AreEqual(scale, actualScale, 1e-5f);
        Assert.AreEqual(gray, actualGray, 1e-5f);
        Assert.AreEqual(alpha, actualAlpha, 1e-5f);
        GameHUDSkin.ComboBreakFrame(.1f, false, out _, out _, out actualGray, out _);
        Assert.AreEqual(1f, actualGray, "0.1秒で灰色へ到達");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GhostKeepsOldDigitsAndCannotEraseTheNewCombo(bool reduced)
    {
        bool saved = DisplaySettings.ReducedEffects;
        var scoreGo = new GameObject("ComboBreakScore");
        var hudGo = new GameObject("ComboBreakHUD");
        try
        {
            DisplaySettings.SetReducedEffectsForTest(reduced);
            var score = scoreGo.AddComponent<ScoreManager>();
            var hud = hudGo.AddComponent<GameHUDSkin>();
            hud.Build();
            int children = hud.transform.childCount;
            var update = typeof(GameHUDSkin).GetMethod("UpdateCombo", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < 10; i++) score.RegisterHit(JudgmentTier.Perfect);
            update.Invoke(hud, null);
            var live = hud.transform.Find("ComboValue").GetComponent<TextMeshProUGUI>();
            var ghost = hud.transform.Find("ComboBreakValue").GetComponent<TextMeshProUGUI>();
            Vector2 origin = live.rectTransform.anchoredPosition;
            Vector3 scale = live.rectTransform.localScale;
            score.RegisterMiss();
            update.Invoke(hud, null);
            Assert.AreEqual("", live.text);
            Assert.AreEqual("10", ghost.text);
            Assert.True(ghost.gameObject.activeSelf);
            Assert.AreEqual(scale, ghost.rectTransform.localScale);
            score.RegisterHit(JudgmentTier.Perfect);
            update.Invoke(hud, null);
            hud.TickComboBreak(.1f);
            Assert.AreEqual("1", live.text);
            Assert.AreEqual("10", ghost.text);
            Assert.AreEqual(origin, live.rectTransform.anchoredPosition);
            if (reduced)
            {
                Assert.AreEqual(origin, ghost.rectTransform.anchoredPosition);
                Assert.AreEqual(scale, ghost.rectTransform.localScale);
            }
            else Assert.AreEqual(GameHUDSkin.TierColor(JudgmentTier.Miss), ghost.colorGradient.topLeft);
            hud.TickComboBreak(reduced ? .1f : .18f);
            Assert.False(ghost.gameObject.activeSelf);
            Assert.AreEqual("1", live.text);
            Assert.AreEqual(children, hud.transform.childCount, "コンボ切れのたびにTMPを作らない");
        }
        finally
        {
            Object.DestroyImmediate(hudGo);
            Object.DestroyImmediate(scoreGo);
            DisplaySettings.SetReducedEffectsForTest(saved);
        }
    }
}

public class BadFragmentTests
{
    [TestCase(JudgmentTier.Perfect, .35f)]
    [TestCase(JudgmentTier.Great, .35f)]
    [TestCase(JudgmentTier.Good, .35f)]
    [TestCase(JudgmentTier.Bad, 1.6f)]
    [TestCase(JudgmentTier.Miss, .35f)]
    public void OnlyBadSelectsHeavyGravity(JudgmentTier tier, float expected)
        => Assert.AreEqual(expected, CuttableNote.FragmentGravityScale(tier));

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void BadFallsHeavilyWithSameInitialSpeedAndPoolReuseResetsIt(bool low, bool projector)
    {
        bool savedLow = DisplaySettings.ReducedEffects, savedProjector = DisplaySettings.ProjectorMode;
        var root = new GameObject("BadFragmentTest");
        try
        {
            DisplaySettings.SetReducedEffectsForTest(low);
            DisplaySettings.SetProjectorModeForTest(projector);
            var pool = root.AddComponent<NoteFragmentPool>();
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(root.transform);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var note = go.AddComponent<CuttableNote>();
            note.IsPooled = true;
            note.FragmentPool = pool;
            Cut(note, JudgmentTier.Bad);
            var pieces = root.GetComponentsInChildren<SlicePieceDecay>();
            Assert.AreEqual(2, pieces.Length);
            foreach (var piece in pieces)
            {
                Assert.AreEqual(1.6f, piece.GravityScale);
                Assert.AreEqual(8f * note.saberVelocityScale * 1.9f, piece.Velocity.x, 1e-4f);
                Assert.AreEqual(note.sliceSeparationImpulse * 1.9f, Mathf.Abs(piece.Velocity.y), 1e-4f);
                Assert.AreEqual(-12f, piece.Velocity.z, 1e-4f);
                float before = piece.Velocity.y;
                piece.Step(.1f);
                float expected = (before + Physics.gravity.y * 1.6f * .1f) * Mathf.Exp(-.1f * .1f);
                Assert.AreEqual(expected, piece.Velocity.y, 1e-4f);
                piece.Release();
            }
            Cut(note, null);
            Assert.AreEqual(2, pool.CreatedCount, "同じ破片を使い回す");
            foreach (var piece in pieces)
            {
                Assert.True(piece.gameObject.activeSelf);
                Assert.AreEqual(.35f, piece.GravityScale, "ノーツのBAD履歴も残さない");
                piece.Release();
            }
            var rented = pool.Rent();
            Assert.Zero(rented.GravityScale, "貸出時点で前の重力を消す");
            rented.Launch(pool, Vector3.right, Vector3.zero, true, 1f, .5f, 0f);
            Assert.AreEqual(1f, rented.GravityScale, "通常のLaunchは元の重力");
            rented.Release();

            note.ResetForSpawn();
            note.RequiredCutCount = note.RemainingCuts = 1;
            note.gameObject.SetActive(true);
            Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
            note.MarkMiss();
            Assert.True(note.IsMissed);
            Assert.Zero(root.GetComponentsInChildren<SlicePieceDecay>().Length, "MISSに破片を追加しない");
        }
        finally
        {
            Object.DestroyImmediate(root);
            DisplaySettings.SetReducedEffectsForTest(savedLow);
            DisplaySettings.SetProjectorModeForTest(savedProjector);
        }
    }

    static void Cut(CuttableNote note, JudgmentTier? tier)
    {
        note.ResetForSpawn();
        note.RequiredCutCount = note.RemainingCuts = 1;
        note.FlowDebris = true;
        note.FlowNoteVelocity = new Vector3(0f, 0f, -20f);
        // 実際のScoreManagerと同じく、OnCut内で確定判定を通知してから破片を作る。
        if (tier.HasValue) note.OnCut += (n, p, v) => n.NotifyJudgment(tier.Value, p, v);
        note.gameObject.SetActive(true);
        note.Cut(note.transform.position, Vector3.right * 8);
    }
}
