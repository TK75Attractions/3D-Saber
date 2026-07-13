using NUnit.Framework;
using UnityEngine;

public class HapticFeedbackTests
{
    [Test]
    public void DurationFor_DescendsByTier()
    {
        var go = new GameObject("h");
        var h = go.AddComponent<HapticFeedback>();
        Assert.Greater(h.DurationFor(JudgmentTier.Perfect), h.DurationFor(JudgmentTier.Bad));
        Assert.AreEqual(0f, h.DurationFor(JudgmentTier.Miss));
        Object.DestroyImmediate(go);
    }

    [Test]
    public void HapticCommand_DefaultMode_IsBackwardCompatible()
    {
        bool prev = Haptic.handAwareCommands;
        try
        {
            Haptic.handAwareCommands = false;
            Assert.AreEqual("1\n", Haptic.Command("1", SaberHand.Left), "既定では手情報を付けない(旧トラッカー互換)");
            Assert.AreEqual("0\n", Haptic.Command("0", SaberHand.Right));
        }
        finally
        {
            Haptic.handAwareCommands = prev;
        }
    }

    [Test]
    public void HapticCommand_HandAwareMode_AppendsHand()
    {
        bool prev = Haptic.handAwareCommands;
        try
        {
            Haptic.handAwareCommands = true;
            Assert.AreEqual("1,L\n", Haptic.Command("1", SaberHand.Left));
            Assert.AreEqual("1,R\n", Haptic.Command("1", SaberHand.Right));
            Assert.AreEqual("1\n", Haptic.Command("1", SaberHand.Any), "手不明のときは従来形式");
        }
        finally
        {
            Haptic.handAwareCommands = prev;
        }
    }

    [Test]
    public void CuttableNote_RecordsAcceptedCutterHand()
    {
        var go = new GameObject("note");
        try
        {
            var note = go.AddComponent<CuttableNote>();
            note.RequiredHand = SaberHand.Left;
            note.RequiredCutCount = 2; // ロング: 1回目のカットでは破壊されない(テスト後始末を単純に)
            note.RemainingCuts = 2;
            note.IsJudgeable = true;

            // 誤った手 → 切れず、記録も残らない
            note.Cut(Vector3.zero, new Vector3(10f, 0f, 0f), CutDirection.None, SaberHand.Right);
            Assert.AreEqual(0, note.CutsAchieved, "担当外の手では切れない");
            Assert.AreEqual(SaberHand.Any, note.LastCutterHand);

            // 正しい手 → 受理され、手が記録される
            note.Cut(Vector3.zero, new Vector3(10f, 0f, 0f), CutDirection.None, SaberHand.Left);
            Assert.AreEqual(1, note.CutsAchieved);
            Assert.AreEqual(SaberHand.Left, note.LastCutterHand, "受理されたカットの手が残る");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    [Test]
    public void ScoreManager_TracksLastCutHand_AndResetsOnMiss()
    {
        var sGo = new GameObject("spawner");
        var mGo = new GameObject("score");
        var prefab = new GameObject("notePrefab");
        try
        {
            var spawner = sGo.AddComponent<NoteSpawner>();
            prefab.AddComponent<CuttableNote>();
            spawner.notePrefab = prefab;
            spawner.approachTime = 2.0f;
            spawner.spawnZ = 20f;
            spawner.judgeZ = 0f;

            var score = mGo.AddComponent<ScoreManager>();
            score.Bind(spawner);

            CuttableNote captured = null;
            spawner.OnNoteSpawned += n => captured = n;

            // time=0: songPlayer が無い(=曲時刻0)ため、時刻0のノーツなら誤差0=Perfect になり
            // Miss 経由の LastCutHand リセットを踏まない。
            var chart = new ChartData { bpm = 100f };
            chart.notes.Add(new NoteData { time = 0, x = 0, y = 0, type = "tap", color = "blue" });
            spawner.SetChart(chart);
            spawner.Tick(0.0);

            Assert.IsNotNull(captured);
            captured.Cut(Vector3.zero, new Vector3(10f, 0f, 0f), CutDirection.None, SaberHand.Left);
            Assert.AreEqual(SaberHand.Left, score.LastCutHand, "青ノーツを左手で切った記録");

            score.RegisterMiss();
            Assert.AreEqual(SaberHand.Any, score.LastCutHand, "Miss で Any に戻る");
        }
        finally
        {
            Object.DestroyImmediate(sGo);
            Object.DestroyImmediate(mGo);
            Object.DestroyImmediate(prefab);
            foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (n != null) Object.DestroyImmediate(n.gameObject);
            }
        }
    }
}
