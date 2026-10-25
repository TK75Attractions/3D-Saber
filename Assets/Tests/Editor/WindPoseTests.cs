using NUnit.Framework;
using UnityEngine;

// W(風)の出方(2026-10-26 ユーザー「W譜面のノーツの出現の仕方の改善の余地もある」)。
// 出た瞬間は小さく回り込みながら大きくなり、流れる速さで葉のように傾く。矢印は回さない。静かな区間では元の姿。
public class WindPoseTests
{
    [Test]
    public void NotesGrowAndSpinInRightAfterTheyAppear()
    {
        Assert.AreEqual(WindRules.GrowFrom, WindRules.GrowScale(0), 1e-6f);
        Assert.Greater(WindRules.GrowScale(WindRules.GrowSeconds * .5), WindRules.GrowFrom);
        Assert.AreEqual(1f, WindRules.GrowScale(WindRules.GrowSeconds), 1e-6f);
        Assert.AreEqual(1f, WindRules.GrowScale(5), 1e-6f);
        Assert.AreEqual(1f, WindRules.GrowScale(double.NaN), 1e-6f, "壊れた時刻は本来の大きさ");
        Assert.AreEqual(WindRules.SpinInDegrees, WindRules.SpinIn(0), 1e-4f);
        Assert.AreEqual(0f, WindRules.SpinIn(WindRules.GrowSeconds), 1e-4f);
    }

    [Test]
    public void NotesBankTowardWhereTheyAreBlownAndStandStraightWhenCalm()
    {
        // 右から流れて来る(左へ動く)ノーツは左へ傾く(反時計回り、正の角)。左からは逆。揺れなし・拍 0。
        float fromRight = WindRules.BankDegrees(2f, 0, 0, 0, .7, 1, 0, 2);
        float fromLeft = WindRules.BankDegrees(-2f, 0, 0, 0, .7, 1, 0, 2);
        Assert.Greater(fromRight, 0f);
        Assert.Less(fromLeft, 0f);
        Assert.AreEqual(-fromRight, fromLeft, 1e-4f);
        Assert.LessOrEqual(Mathf.Abs(WindRules.BankDegrees(3f, 0, 0, 1f, .6, 1, .3, 2)), WindRules.MaxBankDegrees);
        Assert.AreEqual(0f, WindRules.BankDegrees(2f, 0, 0, .5f, WindRules.CalmSeconds, 1, 0, 2), "静かな区間はまっすぐ");
        Assert.AreEqual(0f, WindRules.BankDegrees(2f, 0, 0, .5f, -.2, 1, 0, 2), "届いたあともまっすぐ");
    }

    GameObject root;
    NoteSpawner spawner;
    bool projector;

    [SetUp]
    public void SetUp()
    {
        projector = DisplaySettings.ProjectorMode;
        DisplaySettings.SetProjectorModeForTest(false);
        root = new GameObject("WindPoseTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.name = "WindPosePrefab";
        prefab.transform.SetParent(root.transform, false);
        prefab.transform.localScale = new Vector3(.8f, .8f, .5f);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.simultaneousGuideEnabled = false;
        spawner.approachTime = 1;
        spawner.spawnZ = 20;
        spawner.judgeZ = 0;
        spawner.ConfigureFloorGuide(-3);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        DisplaySettings.SetProjectorModeForTest(projector);
    }

    CuttableNote SpawnAt(double now, string direction)
    {
        var chart = new ChartData { bpm = 140 };
        chart.notes.Add(new NoteData { time = 3000, x = .5f, y = .2f, windX = 2f, windY = .3f, windSway = .3f,
            type = direction == "none" ? "tap" : "direction", direction = direction });
        spawner.SetChart(chart);
        spawner.Tick(now);
        Assert.AreEqual(1, spawner.AliveCount);
        return spawner.LiveNotes[0];
    }

    [Test]
    public void ATapNoteAppearsSmallTurnedAndSettlesToItsOwnPose()
    {
        var note = SpawnAt(2.0, "none");   // ちょうど出たコマ(届く1秒前)
        var baseScale = new Vector3(.8f, .8f, .5f);
        Assert.AreEqual(baseScale.x * WindRules.GrowFrom, note.transform.localScale.x, 1e-4f, "出た瞬間は小さい");
        Assert.AreEqual(baseScale.z, note.transform.localScale.z, 1e-4f, "奥行きの長さは変えない");
        Assert.Greater(Mathf.Abs(Mathf.DeltaAngle(0, note.transform.eulerAngles.z)), 30f, "回り込んで出てくる");

        spawner.Tick(2.0 + WindRules.GrowSeconds + .05);
        Assert.AreEqual(baseScale.x, note.transform.localScale.x, 1e-4f, "すぐ本来の大きさ");

        spawner.Tick(3.0 - WindRules.CalmSeconds);
        Assert.AreEqual(baseScale, note.transform.localScale);
        Assert.AreEqual(Quaternion.identity, note.transform.rotation, "判定の前はまっすぐ");
        spawner.Tick(3.0);
        Assert.AreEqual(Quaternion.identity, note.transform.rotation);
    }

    [Test]
    public void ArrowNotesGrowButNeverTurn()
    {
        var note = SpawnAt(2.0, "up");
        Assert.Less(note.transform.localScale.x, .8f);
        Assert.AreEqual(Quaternion.identity, note.transform.rotation, "矢印の向きを読み違えないよう回さない");
        spawner.Tick(2.4);
        Assert.AreEqual(Quaternion.identity, note.transform.rotation);
    }
}
