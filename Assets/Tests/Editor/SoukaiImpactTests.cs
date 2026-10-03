using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 爽快感カタログ 手5(流れる破片)・手8(切れた刃の反応)。
public class SoukaiImpactTests
{
    readonly List<Object> created = new List<Object>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
        created.Clear();
        SaberBladeVisual.ChorusBoost = 0;
    }

    CuttableNote Note()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); created.Add(go);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.position = new Vector3(0, 0, 2);
        return go.AddComponent<CuttableNote>();
    }

    static List<SlicePieceDecay> Pieces()
        => new List<SlicePieceDecay>(Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));

    // ---- 手5 ----

    [Test]
    public void FlowDebrisKeepsNoteMomentumFallsLightlyAndSpinsAlongTheSwing()
    {
        var note = Note();
        note.FlowDebris = true;
        note.FlowNoteVelocity = new Vector3(0, 0, -20);
        note.Cut(note.transform.position, Vector3.right * 8, CutDirection.None, SaberHand.Any);
        var pieces = Pieces();
        Assert.AreEqual(2, pieces.Count, "2片に割れる");
        foreach (var piece in pieces)
        {
            Assert.AreEqual(CuttableNote.FlowPieceLife, piece.life, 1e-4f, "寿命を短くして手前へ抜けさせる");
            Assert.AreEqual(-20 * CuttableNote.FlowMomentumKeep, piece.Velocity.z, .01f, "ノーツの前進の勢いを残す");
            Assert.AreEqual(CuttableNote.FlowGravityScale, piece.GravityScale, 1e-4f, "軽い重力");
            // 右へ振ると切断面の法線は上下(±Y)。回転はその軸まわりが主になる。
            Assert.Greater(Mathf.Abs(piece.AngularVelocity.y), Mathf.Abs(piece.AngularVelocity.x) + Mathf.Abs(piece.AngularVelocity.z),
                "振った向きに沿って転がる");
            float vy = piece.Velocity.y;
            piece.Step(.2f);
            Assert.Less(piece.Velocity.y, vy, "重力で落ちる");
        }
    }

    [Test]
    public void SeparationSpeedStillUsesTheProtected19Factor()
    {
        var note = Note();
        note.FlowDebris = true;
        note.FlowNoteVelocity = new Vector3(0, 0, -20);
        note.Cut(note.transform.position, Vector3.right * 8, CutDirection.None, SaberHand.Any);
        // 切断の初速 =(法線×2.5 + 振りの速度×0.35)× 1.9。前進の勢いはここに掛けない。
        Vector3 expected = (Vector3.up * note.sliceSeparationImpulse + Vector3.right * 8 * note.saberVelocityScale) * 1.9f;
        float expectedXY = new Vector2(expected.x, expected.y).magnitude;
        foreach (var piece in Pieces())
            Assert.AreEqual(expectedXY, new Vector2(piece.Velocity.x, piece.Velocity.y).magnitude, .01f);
    }

    [Test]
    public void NotesOutsideGameplayKeepTheOldDebris()
    {
        var note = Note();
        note.Cut(note.transform.position, Vector3.right * 8, CutDirection.None, SaberHand.Any);
        foreach (var piece in Pieces())
        {
            Assert.AreEqual(note.pieceLife, piece.life, 1e-4f);
            Assert.AreEqual(0f, piece.Velocity.z, .01f, "前進の勢いを足さない");
            Assert.AreEqual(note.pieceUseGravity ? 1f : 0f, piece.GravityScale, 1e-4f);
        }
    }

    [Test]
    public void SpawnerTurnsOnFlowForTapsButNotForLongNotes()
    {
        var root = new GameObject("FlowSpawner"); created.Add(root);
        var prefab = new GameObject("FlowNotePrefab", typeof(CuttableNote)); created.Add(prefab);
        var spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab; spawner.buildTimingCues = false;
        var spawned = new List<CuttableNote>();
        spawner.OnNoteSpawned += n => { spawned.Add(n); created.Add(n.gameObject); };
        var chart = new ChartData();
        chart.notes.Add(new NoteData { time = 1000, type = "tap", color = "red", count = 1, x = 1 });
        chart.notes.Add(new NoteData { time = 1000, type = "long", color = "blue", count = 3, x = -1 });
        spawner.SetChart(chart); spawner.Tick(1);
        Assert.AreEqual(2, spawned.Count);
        var tap = spawned.Find(n => n.RequiredCutCount == 1);
        var hold = spawned.Find(n => n.RequiredCutCount > 1);
        Assert.IsTrue(tap.FlowDebris && hold.FlowDebris);
        Assert.AreEqual(-spawner.Speed, tap.FlowNoteVelocity.z, 1e-4f, "タップはノーツと同じ速さで流れている");
        Assert.AreEqual(Vector3.zero, hold.FlowNoteVelocity, "ロングは判定面に留まるので勢いを残さない");
    }

    // ---- 手8 ----

    static SaberBladeVisual Blade(List<Object> owned)
    {
        var go = new GameObject("BladeCutTest"); owned.Add(go);
        return go.AddComponent<SaberBladeVisual>();
    }

    static void Swing(SaberBladeVisual visual, float from, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            float x = i * .25f;
            visual.Show(new Vector3(x, 0, 0), new Vector3(x, 1, 0), .12f, Color.red, from + i * .016f);
        }
    }

    // 刃の芯(不透明に近い帯)の白さ。赤い刃なら緑と青の成分が白へ寄るほど大きい。
    static float CoreWhiteness(Mesh mesh)
    {
        float best = 0;
        foreach (var c in mesh.colors) if (c.a > .95f) best = Mathf.Max(best, Mathf.Min(c.r, Mathf.Min(c.g, c.b)));
        return best;
    }

    [Test]
    public void CutLeavesAThinSlashAlongTheSwingAndFlashesTheCoreBriefly()
    {
        var visual = Blade(created);
        Swing(visual, 1f, 6);
        var mesh = visual.GetComponent<MeshFilter>().sharedMesh;
        int before = mesh.vertexCount;
        float white = CoreWhiteness(mesh);
        Assert.Less(white, .95f, "切る前の芯は刃の色が少し乗っている");

        visual.NotifyCut();
        Assert.GreaterOrEqual(visual.SlashPointCount, 2, "直前の振りの弧を覚える");
        float t = 1f + 6 * .016f;
        visual.Show(new Vector3(1.6f, 0, 0), new Vector3(1.6f, 1, 0), .12f, Color.red, t);
        Assert.Greater(mesh.vertexCount, before, "細い斬り跡を足す");
        Assert.Greater(CoreWhiteness(mesh), white + .03f, "切った直後は芯が白くなる");

        visual.Show(new Vector3(1.7f, 0, 0), new Vector3(1.7f, 1, 0), .12f, Color.red, t + .08f);
        Assert.AreEqual(white, CoreWhiteness(mesh), .001f, "芯の白は 60ms で戻る");
        int withSlash = mesh.vertexCount;

        visual.Show(new Vector3(1.75f, 0, 0), new Vector3(1.75f, 1, 0), .12f, Color.red, t + .1f);
        visual.Show(new Vector3(1.8f, 0, 0), new Vector3(1.8f, 1, 0), .12f, Color.red, t + .21f);
        Assert.Less(mesh.vertexCount, withSlash, "斬り跡は 0.2 秒で消える");
    }

    [Test]
    public void CutGlowStrengthensTheTrailWithoutChangingTheBladeWidth()
    {
        float MaxRibbonAlpha(SaberBladeVisual v)
        {
            var mesh = v.GetComponent<MeshFilter>().sharedMesh;
            var uvs = new List<Vector3>(); mesh.GetUVs(0, uvs);
            var colors = mesh.colors; float max = 0;
            for (int i = 0; i < colors.Length; i++) if (uvs[i].z > .5f) max = Mathf.Max(max, colors[i].a);
            return max;
        }
        var plain = Blade(created); Swing(plain, 1f, 6);
        var cut = Blade(created); Swing(cut, 1f, 5); cut.NotifyCut();
        cut.Show(new Vector3(1.25f, 0, 0), new Vector3(1.25f, 1, 0), .12f, Color.red, 1f + 5 * .016f);
        Assert.Greater(MaxRibbonAlpha(cut), MaxRibbonAlpha(plain) * 2f, "切った直後の軌跡は約3倍濃い");
    }

    [Test]
    public void ChorusBoostMakesTheTrailLonger()
    {
        Assert.AreEqual(SaberBladeVisual.TrailLifetime, SaberBladeVisual.LifetimeFor(0), 1e-5f);
        Assert.Greater(SaberBladeVisual.LifetimeFor(1), SaberBladeVisual.TrailLifetime * 1.5f);
        var visual = Blade(created);
        for (int i = 0; i < 12; i++) visual.Show(new Vector3(i * .02f, 0, 0), new Vector3(i * .02f, 1, 0), .12f, Color.cyan, i * .015f);
        int normal = visual.VertexCount;
        SaberBladeVisual.ChorusBoost = 1;
        var boosted = Blade(created);
        for (int i = 0; i < 12; i++) boosted.Show(new Vector3(i * .02f, 0, 0), new Vector3(i * .02f, 1, 0), .12f, Color.cyan, i * .015f);
        Assert.Greater(boosted.VertexCount, normal, "サビ中は長い軌跡を残す");
    }

    [Test]
    public void CutFlashGoesToTheHandThatCutAndSkipsBadMissAndTimeouts()
    {
        var host = new GameObject("CutFlashHost"); created.Add(host);
        var score = host.AddComponent<ScoreManager>();
        SaberCutJudge Judge(string name, SaberHand hand)
        {
            var go = new GameObject(name); created.Add(go);
            var bridge = go.AddComponent<SaberInputBridge>();
            var judge = go.AddComponent<SaberCutJudge>();
            judge.hand = hand; judge.bladeProvider = bridge;
            return judge;
        }
        var right = Judge("Right", SaberHand.Right);
        var left = Judge("Left", SaberHand.Left);
        var flash = SaberCutFlash.Attach(host, score, right, left);
        Assert.AreSame(left.bladeProvider, flash.BridgeFor(SaberHand.Left));
        Assert.AreSame(right.bladeProvider, flash.BridgeFor(SaberHand.Right));
        Assert.AreSame(right.bladeProvider, flash.BridgeFor(SaberHand.Any), "マウスなど手の区別が無いときは1本目");

        score.RegisterHit(JudgmentTier.Perfect);
        score.RegisterHit(JudgmentTier.Great);
        score.RegisterHit(JudgmentTier.Good);
        Assert.AreEqual(3, flash.NotifiedCount);
        score.RegisterHit(JudgmentTier.Bad);
        score.RegisterHit(JudgmentTier.Miss);
        Assert.AreEqual(3, flash.NotifiedCount, "Bad と Miss では光らせない");
    }
}
