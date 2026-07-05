using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class NoteTimingCueTests
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
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    // ---- 純関数 ----

    [Test]
    public void RingScale_StartsAtStartScale_AtApproachTime()
    {
        Assert.AreEqual(3.0f, NoteTimingCue.ComputeRingScale(2.0, 2f, 3.0f, 1.1f), 0.001f);
    }

    [Test]
    public void RingScale_ReachesEndScale_AtHitTime()
    {
        Assert.AreEqual(1.1f, NoteTimingCue.ComputeRingScale(0.0, 2f, 3.0f, 1.1f), 0.001f);
    }

    [Test]
    public void RingScale_StaysAtEndScale_AfterHitTime()
    {
        // ロングノーツの滞留中も収束したまま
        Assert.AreEqual(1.1f, NoteTimingCue.ComputeRingScale(-1.5, 2f, 3.0f, 1.1f), 0.001f);
    }

    [Test]
    public void RingScale_LinearMidpoint()
    {
        // dt = approach/2 → ちょうど中間サイズ
        Assert.AreEqual((3.0f + 1.1f) * 0.5f, NoteTimingCue.ComputeRingScale(1.0, 2f, 3.0f, 1.1f), 0.001f);
    }

    [Test]
    public void RingAlpha_ZeroBeforeVisiblePortion()
    {
        // approach=2, portion=0.9 → 残り 1.8s より前は非表示
        Assert.AreEqual(0f, NoteTimingCue.ComputeRingAlpha(1.9, 2f, 0.9f), 0.001f);
        Assert.AreEqual(0f, NoteTimingCue.ComputeRingAlpha(1.8, 2f, 0.9f), 0.001f);
    }

    [Test]
    public void RingAlpha_FullAtHitTimeAndAfter()
    {
        Assert.AreEqual(1f, NoteTimingCue.ComputeRingAlpha(0.0, 2f, 0.9f), 0.001f);
        Assert.AreEqual(1f, NoteTimingCue.ComputeRingAlpha(-0.5, 2f, 0.9f), 0.001f);
    }

    [Test]
    public void RingAlpha_MonotonicallyIncreases_AsNoteApproaches()
    {
        float far = NoteTimingCue.ComputeRingAlpha(1.5, 2f, 0.9f);
        float mid = NoteTimingCue.ComputeRingAlpha(1.0, 2f, 0.9f);
        float near = NoteTimingCue.ComputeRingAlpha(0.3, 2f, 0.9f);
        Assert.Less(far, mid);
        Assert.Less(mid, near);
    }

    [Test]
    public void IsInWindow_Boundaries()
    {
        // 早め 0.135 / 遅め 0.27 の非対称窓(NoteSpawner の既定値と一致)
        Assert.IsTrue(NoteTimingCue.IsInWindow(0.135, 0.135f, 0.27f), "早め境界ちょうどは窓内");
        Assert.IsFalse(NoteTimingCue.IsInWindow(0.136, 0.135f, 0.27f), "早め境界の外");
        Assert.IsTrue(NoteTimingCue.IsInWindow(-0.27, 0.135f, 0.27f), "遅め境界ちょうどは窓内");
        Assert.IsFalse(NoteTimingCue.IsInWindow(-0.28, 0.135f, 0.27f), "遅め境界の外");
        Assert.IsTrue(NoteTimingCue.IsInWindow(0.0, 0.135f, 0.27f), "HitTime ちょうどは窓内");
    }

    [Test]
    public void GhostAlpha_ShortWindow_HiddenUntilVisibleSeconds()
    {
        // 残り visibleSeconds(0.7s) を切るまで完全非表示(密な譜面での重なり防止)
        Assert.AreEqual(0f, NoteTimingCue.ComputeGhostAlpha(2.0, 0.7f), 0.001f);
        Assert.AreEqual(0f, NoteTimingCue.ComputeGhostAlpha(0.7, 0.7f), 0.001f);
        // HitTime ちょうどとその後(ロング滞留中)は最大
        Assert.AreEqual(1f, NoteTimingCue.ComputeGhostAlpha(0.0, 0.7f), 0.001f);
        Assert.AreEqual(1f, NoteTimingCue.ComputeGhostAlpha(-0.3, 0.7f), 0.001f);
        // 中間は 0..1 の間で接近につれ単調増加
        float far = NoteTimingCue.ComputeGhostAlpha(0.5, 0.7f);
        float near = NoteTimingCue.ComputeGhostAlpha(0.2, 0.7f);
        Assert.Greater(far, 0f);
        Assert.Less(far, 1f);
        Assert.Greater(near, far);
    }

    // ---- コンポーネント統合 ----

    private (GameObject go, CuttableNote note, NoteTimingCue cue) MakeNoteWithCue(bool buildGhost = false)
    {
        // MeshRenderer 無しの素の GameObject を使う
        // (EditMode で renderer.material に触れないようにするため)
        var go = new GameObject("note");
        created.Add(go);
        var note = go.AddComponent<CuttableNote>();
        var cue = go.AddComponent<NoteTimingCue>();
        cue.buildGhost = buildGhost;
        cue.Initialize(note, judgeZ: 0f);
        return (go, note, cue);
    }

    [Test]
    public void Initialize_BuildsRingWithFourBars()
    {
        var (go, _, cue) = MakeNoteWithCue();
        var ring = go.transform.Find("TimingRing");
        Assert.IsNotNull(ring, "TimingRing が note の子として生成される");
        Assert.AreEqual(4, ring.childCount, "枠は4本のバーで構成される");
        Assert.AreSame(ring, cue.RingRoot);
    }

    [Test]
    public void Initialize_BuildsGhostByDefault_WithoutFill()
    {
        // 短時間表示方式(ghostVisibleSeconds)で復活:既定で生成される。
        // ヘルパーを使わず素の AddComponent でコンポーネント既定値そのものを検証する。
        var go = new GameObject("note");
        created.Add(go);
        var note = go.AddComponent<CuttableNote>();
        var cue = go.AddComponent<NoteTimingCue>();
        Assert.IsTrue(cue.buildGhost, "buildGhost の既定は true(短時間表示方式)");
        cue.Initialize(note, judgeZ: 0f);
        Assert.IsNotNull(cue.GhostRoot, "既定で着地ゴーストを生成する");
        // 内側の塗り(GhostFill)は霞の原因なので廃止済み:枠 4 本のみ
        Assert.IsNull(cue.GhostRoot.transform.Find("GhostFill"), "GhostFill は生成しない");
        Assert.AreEqual(4, cue.GhostRoot.transform.childCount, "ゴーストは枠 4 本のみ");
    }

    [Test]
    public void Initialize_BuildsGhostAtJudgePlane_WhenEnabled()
    {
        // buildGhost=true を明示すれば従来どおり生成できる(オプトイン)
        var (go, _, cue) = MakeNoteWithCue(buildGhost: true);
        Assert.IsNotNull(cue.GhostRoot, "着地ゴーストが生成される");
        // judgeZ=0 + ghostZBias の位置
        Assert.AreEqual(cue.ghostZBias, cue.GhostRoot.transform.position.z, 0.001f);
        Assert.AreEqual(go.transform.position.x, cue.GhostRoot.transform.position.x, 0.001f);
        Assert.AreEqual(go.transform.position.y, cue.GhostRoot.transform.position.y, 0.001f);
    }

    [Test]
    public void Tick_SetsInWindowFlag()
    {
        var (_, _, cue) = MakeNoteWithCue();
        cue.Tick(1.5, 2f, 0.135f, 0.27f);
        Assert.IsFalse(cue.InWindow, "まだ遠い");
        cue.Tick(0.05, 2f, 0.135f, 0.27f);
        Assert.IsTrue(cue.InWindow, "判定窓内");
        cue.Tick(-0.5, 2f, 0.135f, 0.27f);
        Assert.IsFalse(cue.InWindow, "窓を過ぎた");
    }

    [Test]
    public void Tick_RingConvergesOnNote()
    {
        var (_, _, cue) = MakeNoteWithCue();
        cue.Tick(2.0, 2f, 0.135f, 0.27f);
        float farScale = cue.RingRoot.localScale.x;
        cue.Tick(0.0, 2f, 0.135f, 0.27f);
        float hitScale = cue.RingRoot.localScale.x;
        Assert.AreEqual(cue.ringStartScale, farScale, 0.001f);
        Assert.AreEqual(cue.ringEndScale, hitScale, 0.001f);
    }

    [Test]
    public void Tick_AfterMiss_ClearsInWindow()
    {
        var (_, note, cue) = MakeNoteWithCue();
        cue.Tick(0.05, 2f, 0.135f, 0.27f);
        Assert.IsTrue(cue.InWindow);
        note.MarkMiss();
        cue.Tick(0.05, 2f, 0.135f, 0.27f);
        Assert.IsFalse(cue.InWindow, "Miss 後は窓内扱いにしない");
    }

    [Test]
    public void OnDestroy_CleansUpGhost()
    {
        // EditMode では DestroyImmediate しても OnDestroy が呼ばれないため、
        // 既存テストの流儀(NoteVisualsTests の Update と同様)でリフレクション起動する。
        var (go, _, cue) = MakeNoteWithCue(buildGhost: true);
        var ghost = cue.GhostRoot;
        Assert.IsNotNull(ghost);
        var m = typeof(NoteTimingCue).GetMethod("OnDestroy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(m, "OnDestroy メソッドが見つからない");
        m.Invoke(cue, null);
        Assert.IsTrue(ghost == null, "ノーツ破棄でゴーストも破棄される");
    }

    // ---- NoteSpawner 統合 ----

    [Test]
    public void Spawner_AttachesTimingCue_ByDefault()
    {
        var sGo = new GameObject("spawner");
        created.Add(sGo);
        var sp = sGo.AddComponent<NoteSpawner>();
        var prefab = new GameObject("notePrefab");
        created.Add(prefab);
        prefab.AddComponent<CuttableNote>();
        sp.notePrefab = prefab;
        sp.approachTime = 2.0f;

        var chart = new ChartData { bpm = 120f };
        chart.notes.Add(new NoteData { time = 1000f, x = 0, y = 0, type = "tap" });
        CuttableNote spawned = null;
        sp.OnNoteSpawned += n => spawned = n;
        sp.SetChart(chart);
        sp.Tick(0.0);

        Assert.IsNotNull(spawned);
        Assert.IsNotNull(spawned.TimingCue, "既定でタイミングキューが付く");
        Assert.IsNotNull(spawned.GetComponent<NoteTimingCue>());
    }

    [Test]
    public void Spawner_SkipsTimingCue_WhenDisabled()
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
        chart.notes.Add(new NoteData { time = 1000f, x = 0, y = 0, type = "tap" });
        CuttableNote spawned = null;
        sp.OnNoteSpawned += n => spawned = n;
        sp.SetChart(chart);
        sp.Tick(0.0);

        Assert.IsNotNull(spawned);
        Assert.IsNull(spawned.TimingCue);
        Assert.IsNull(spawned.GetComponent<NoteTimingCue>());
    }
}
