using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 2本セーバーの「手」ルール:色マッピング・カット可否・ノーツの手ゲート・Judge の手渡し。
public class SaberHandTests
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
        // スポーナー経由で生まれたノーツも回収する
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    // ---- NoteSpawner の色→手 設定 ----

    [Test]
    public void Spawner_SetsRequiredHandFromChartColor()
    {
        var sGo = new GameObject("spawner");
        created.Add(sGo);
        var sp = sGo.AddComponent<NoteSpawner>();
        sp.buildTimingCues = false;
        var prefab = new GameObject("notePrefab");
        created.Add(prefab);
        prefab.AddComponent<CuttableNote>();
        sp.notePrefab = prefab;
        sp.approachTime = 2.0f;

        var chart = new ChartData { bpm = 120f };
        chart.notes.Add(new NoteData { time = 1000f, color = "blue" });
        chart.notes.Add(new NoteData { time = 1200f, color = "red" });
        chart.notes.Add(new NoteData { time = 1400f, color = "gold" });
        var spawned = new List<CuttableNote>();
        sp.OnNoteSpawned += n => spawned.Add(n);
        sp.SetChart(chart);
        sp.Tick(0.0);

        Assert.AreEqual(3, spawned.Count);
        Assert.AreEqual(SaberHand.Left, spawned[0].RequiredHand, "blue → 左手");
        Assert.AreEqual(SaberHand.Right, spawned[1].RequiredHand, "red → 右手");
        Assert.AreEqual(SaberHand.Any, spawned[2].RequiredHand, "gold → どちらでも");
        Assert.IsTrue(spawned[2].IsGold);
    }

    // ---- 色 → 手のマッピング ----

    [Test]
    public void FromColor_BlueIsLeft_RedIsRight()
    {
        Assert.AreEqual(SaberHand.Left, SaberHandHelper.FromColor("blue"));
        Assert.AreEqual(SaberHand.Right, SaberHandHelper.FromColor("red"));
        Assert.AreEqual(SaberHand.Right, SaberHandHelper.FromColor("RED"), "大文字小文字は無視");
    }

    [Test]
    public void FromColor_GoldAndDefaultAreAny()
    {
        Assert.AreEqual(SaberHand.Any, SaberHandHelper.FromColor("gold"));
        Assert.AreEqual(SaberHand.Any, SaberHandHelper.FromColor("default"));
        Assert.AreEqual(SaberHand.Any, SaberHandHelper.FromColor(""));
        Assert.AreEqual(SaberHand.Any, SaberHandHelper.FromColor(null));
    }

    [Test]
    public void CanCut_Matrix()
    {
        // Any はどちらの立場でも常に可
        Assert.IsTrue(SaberHandHelper.CanCut(SaberHand.Any, SaberHand.Left));
        Assert.IsTrue(SaberHandHelper.CanCut(SaberHand.Any, SaberHand.Right));
        Assert.IsTrue(SaberHandHelper.CanCut(SaberHand.Left, SaberHand.Any));
        Assert.IsTrue(SaberHandHelper.CanCut(SaberHand.Right, SaberHand.Any));
        // 一致は可、不一致は不可
        Assert.IsTrue(SaberHandHelper.CanCut(SaberHand.Left, SaberHand.Left));
        Assert.IsTrue(SaberHandHelper.CanCut(SaberHand.Right, SaberHand.Right));
        Assert.IsFalse(SaberHandHelper.CanCut(SaberHand.Left, SaberHand.Right));
        Assert.IsFalse(SaberHandHelper.CanCut(SaberHand.Right, SaberHand.Left));
    }

    [Test]
    public void Other_SwapsHands()
    {
        Assert.AreEqual(SaberHand.Right, SaberHandHelper.Other(SaberHand.Left));
        Assert.AreEqual(SaberHand.Left, SaberHandHelper.Other(SaberHand.Right));
        Assert.AreEqual(SaberHand.Any, SaberHandHelper.Other(SaberHand.Any));
    }

    [Test]
    public void HandColor_LeftIsBlue_RightIsRed()
    {
        var left = SaberHandHelper.HandColor(SaberHand.Left);
        Assert.That(left.b, Is.GreaterThan(0.8f), "左手は青系");
        var right = SaberHandHelper.HandColor(SaberHand.Right);
        Assert.That(right.r, Is.GreaterThan(0.8f), "右手は赤系");
        Assert.That(right.b, Is.LessThan(0.6f));
    }

    // ---- ノーツの手ゲート ----

    private CuttableNote MakeNote(SaberHand requiredHand, int cutCount = 1)
    {
        var go = new GameObject("note");
        created.Add(go);
        var note = go.AddComponent<CuttableNote>();
        note.RequiredHand = requiredHand;
        note.RequiredCutCount = cutCount;
        note.RemainingCuts = cutCount;
        note.IsJudgeable = true;
        return note;
    }

    [Test]
    public void Cut_WrongHand_IsIgnored_NoteStaysCuttable()
    {
        var note = MakeNote(SaberHand.Left);
        note.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right);
        Assert.IsFalse(note.IsCut, "誤った手では切れない");
        Assert.IsFalse(note.IsMissed, "ペナルティも無い");
        // その後、正しい手なら切れる
        note.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Left);
        Assert.IsTrue(note.IsCut);
    }

    [Test]
    public void Cut_AnyHandNote_CutByEitherHand()
    {
        var noteL = MakeNote(SaberHand.Any);
        noteL.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Left);
        Assert.IsTrue(noteL.IsCut, "無色/金はどちらの手でも切れる(左)");

        var noteR = MakeNote(SaberHand.Any);
        noteR.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right);
        Assert.IsTrue(noteR.IsCut, "無色/金はどちらの手でも切れる(右)");
    }

    [Test]
    public void Cut_AnyCutter_CutsHandGatedNote()
    {
        // マウスフォールバック(Any)は手指定ノーツも切れる
        var note = MakeNote(SaberHand.Left);
        note.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Any);
        Assert.IsTrue(note.IsCut);
    }

    [Test]
    public void LongNote_PartialCuts_AreHandGated()
    {
        var note = MakeNote(SaberHand.Right, cutCount: 3);
        // 誤った手の部分カットは無効
        note.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Left);
        Assert.AreEqual(3, note.RemainingCuts, "誤った手ではカウントが減らない");
        Assert.AreEqual(0, note.CutsAchieved);
        // 正しい手なら減る
        note.Cut(Vector3.zero, Vector3.right, CutDirection.None, SaberHand.Right);
        Assert.AreEqual(2, note.RemainingCuts);
        Assert.AreEqual(1, note.CutsAchieved);
    }

    [Test]
    public void LegacyCutOverloads_BehaveAsAnyHand()
    {
        // 既存の2引数/3引数 Cut は Any 扱い(後方互換)
        var note = MakeNote(SaberHand.Left);
        note.Cut(Vector3.zero, Vector3.right);
        Assert.IsTrue(note.IsCut);
    }

    // ---- SaberCutJudge の手渡し ----

    private (SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge) MakeBladeSaber(SaberHand hand)
    {
        var go = new GameObject("saber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        var bridge = go.AddComponent<SaberInputBridge>();
        var judge = go.AddComponent<SaberCutJudge>();
        // EditMode では Awake が呼ばれないため参照は手で配線する
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        judge.hand = hand;
        return (judge, tracker, bridge);
    }

    // ブレードでノーツ(0,0)を横切って抜ける一連の動きをシミュレートする
    private void SwingAcrossOrigin(SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge)
    {
        tracker.Tick(Vector3.zero, 0.1f);
        tracker.Tick(new Vector3(1f, 0f, 0f), 0.05f); // Speed=20
        bridge.OverrideBlade(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f)); // ノーツに接触
        judge.TryCut();
        bridge.OverrideBlade(new Vector3(3f, 3f, 0f), new Vector3(5f, 3f, 0f)); // 離脱
        tracker.Tick(new Vector3(4f, 3f, 0f), 0.05f);
        judge.TryCut();
    }

    [Test]
    public void Judge_WrongHand_DoesNotCutHandGatedNote()
    {
        var note = MakeNote(SaberHand.Left);
        var (judge, tracker, bridge) = MakeBladeSaber(SaberHand.Right);
        SwingAcrossOrigin(judge, tracker, bridge);
        Assert.IsFalse(note.IsCut, "右手セーバーでは青(左手)ノーツは切れない");
    }

    [Test]
    public void Judge_MatchingHand_CutsHandGatedNote()
    {
        var note = MakeNote(SaberHand.Left);
        var (judge, tracker, bridge) = MakeBladeSaber(SaberHand.Left);
        SwingAcrossOrigin(judge, tracker, bridge);
        Assert.IsTrue(note.IsCut, "左手セーバーで青ノーツが切れる");
    }

    [Test]
    public void Judge_MouseFallback_CutsAnyColor()
    {
        var note = MakeNote(SaberHand.Left);
        var (judge, tracker, bridge) = MakeBladeSaber(SaberHand.Right);
        bridge.OverrideMouseFallback(true); // マウス操作中は手の区別なし
        Assert.AreEqual(SaberHand.Any, judge.EffectiveHand());
        SwingAcrossOrigin(judge, tracker, bridge);
        Assert.IsTrue(note.IsCut, "マウスフォールバック時は全色切れる");
    }
}
