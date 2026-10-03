using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// 爽快感カタログ 山1(サビ入りのドロップとサビ中の強調)。
public class ChorusDropTests
{
    readonly List<Object> created = new List<Object>();
    AudioSource music;
    Transform stage;
    ChorusDrop drop;
    JudgmentSfx sfx;

    static StagePerformanceTimeline Timeline() => new StagePerformanceTimeline
    {
        sections = new[]
        {
            new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20, intensity = 1 },
            new StagePerformanceTimeline.Section { startSeconds = 30, endSeconds = 40, intensity = .45f }, // 助走は対象外
        }
    };

    [SetUp]
    public void Setup()
    {
        var song = new GameObject("ChorusSong", typeof(AudioSource)); created.Add(song);
        music = song.GetComponent<AudioSource>(); music.volume = .8f;
        var stageGo = new GameObject("ChorusStage"); created.Add(stageGo);
        stage = stageGo.transform; stage.position = new Vector3(0, -1, 3);
        var gate = new GameObject("JudgeGate"); created.Add(gate);
        gate.AddComponent<JudgeGateFrame>().Build(new Vector3(4, 2, 1));
        var sfxGo = new GameObject("ChorusSfx", typeof(AudioSource)); created.Add(sfxGo);
        sfx = sfxGo.AddComponent<JudgmentSfx>();
        var host = new GameObject("ChorusDropHost"); created.Add(host);
        drop = host.AddComponent<ChorusDrop>();
        drop.Setup(Timeline(), music, 120, stage, sfx);
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    [TearDown]
    public void Cleanup()
    {
        if (drop != null) drop.ResetEffects();
        foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
        created.Clear();
        SaberBladeVisual.ChorusBoost = 0;
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    void Run(double from, double to, float step = .02f)
    {
        for (double t = from; t <= to + 1e-9; t += step) drop.Tick(t, step);
    }

    [Test]
    public void OnlyStrongSectionsBecomeDropEntries()
    {
        CollectionAssert.AreEqual(new[] { 10.0 }, drop.Entries);
        Assert.AreEqual(.5f, drop.BeatSeconds, 1e-4f, "120BPM の1拍");
    }

    [Test]
    public void LastBeatIsDuckedBy2dBAndRestoredAtTheEntry()
    {
        Run(8, 9.4);
        Assert.IsFalse(drop.IsDucking);
        Assert.AreEqual(.8f, music.volume, 1e-4f);
        Run(9.42, 9.6);
        Assert.IsTrue(drop.IsDucking, "入口の1拍前から下げる");
        Assert.AreEqual(.8f * ChorusDrop.DuckFactor, music.volume, 1e-4f);
        Assert.AreEqual(Mathf.Pow(10, -2f / 20), ChorusDrop.DuckFactor, 1e-5f);
        Run(9.62, 10.02);
        Assert.IsFalse(drop.IsDucking);
        Assert.AreEqual(.8f, music.volume, 1e-4f, "入口で元の音量へ戻す");
    }

    [Test]
    public void EntryIsScheduledAheadAndFiresOnce()
    {
        Run(9, 9.84);
        Assert.AreEqual(0, drop.ScheduledCount);
        Run(9.86, 9.9);
        Assert.AreEqual(1, drop.ScheduledCount, "入口の少し前に曲の時計で予約する");
        Run(9.92, 12);
        Assert.AreEqual(1, drop.ScheduledCount, "二重に予約しない");
        Assert.AreEqual(1, drop.FiredCount);
    }

    [Test]
    public void RingAndBackgroundKickPlayOnceAndReturnExactly()
    {
        Vector3 basePosition = stage.position;
        Run(9.5, 9.98);
        Assert.IsTrue(drop.HasRing);
        drop.Tick(10.0, .02f);
        drop.Tick(10.04, .04f);
        Assert.GreaterOrEqual(drop.RingAge, 0f, "ゲート枠のリングが広がる");
        Assert.Greater(stage.position.z, basePosition.z, "舞台を奥へ押す");
        Assert.AreEqual(basePosition.x, stage.position.x, 1e-5f, "押すのは奥行きだけ");
        Assert.AreEqual(basePosition.y, stage.position.y, 1e-5f);
        Run(10.06, 11);
        Assert.Less(drop.RingAge, 0f, "リングは消える");
        Assert.AreEqual(basePosition, stage.position, "舞台は元の位置へ戻る");
    }

    [Test]
    public void ReducedEffectsKeepsTheSoundButSkipsRingAndKick()
    {
        DisplaySettings.SetReducedEffectsForTest(true);
        Vector3 basePosition = stage.position;
        Run(9.5, 10.1);
        Assert.AreEqual(1, drop.FiredCount);
        Assert.AreEqual(1, drop.ScheduledCount);
        Assert.Less(drop.RingAge, 0f);
        Assert.AreEqual(basePosition, stage.position);
    }

    [Test]
    public void ChorusBoostsTheBladeAndCutSoundOnlyDuringStrongSections()
    {
        Run(14, 15);
        Assert.AreEqual(1f, drop.Chorus, 1e-4f);
        Assert.AreEqual(1f, SaberBladeVisual.ChorusBoost, 1e-4f);
        Assert.AreEqual(1f, sfx.ChorusLevel, 1e-4f);
        Run(34, 35);
        Assert.AreEqual(0f, drop.Chorus, 1e-4f, "助走区間では強めない");
        Assert.AreEqual(0f, SaberBladeVisual.ChorusBoost, 1e-4f);
        Assert.AreEqual(0f, sfx.ChorusLevel, 1e-4f);
    }

    [Test]
    public void RewindDoesNotReplayAPassedEntryButReplaysItWhenReached()
    {
        Run(9.5, 12);
        Assert.AreEqual(1, drop.FiredCount);
        drop.Tick(5, .02f); // 巻き戻し
        Assert.AreEqual(1, drop.FiredCount);
        Assert.AreEqual(0f, SaberBladeVisual.ChorusBoost);
        Run(5.02, 10.1);
        Assert.AreEqual(2, drop.FiredCount, "巻き戻した先から再び入口を通ったら出す");
    }

    [Test]
    public void StartingInsideTheChorusDoesNotFireThePassedEntry()
    {
        Run(15, 16);
        Assert.AreEqual(0, drop.FiredCount);
        Assert.AreEqual(0, drop.ScheduledCount);
    }

    [Test]
    public void ResetRestoresVolumeStageAndBoost()
    {
        Vector3 basePosition = stage.position;
        Run(9.6, 9.7);
        Assert.IsTrue(drop.IsDucking);
        drop.ResetEffects();
        Assert.AreEqual(.8f, music.volume, 1e-4f);
        Run(9.9, 10.02);
        drop.ResetEffects();
        Assert.AreEqual(basePosition, stage.position);
        Assert.AreEqual(0f, SaberBladeVisual.ChorusBoost);
    }

    [Test]
    public void StrongEvaluationIgnoresTheLowRunUp()
    {
        var timeline = Timeline();
        Assert.Greater(timeline.Evaluate(35), 0f, "従来の評価は助走も含む");
        Assert.AreEqual(0f, timeline.EvaluateStrong(35));
        Assert.AreEqual(1f, timeline.EvaluateStrong(15), 1e-4f);
    }

    [Test]
    public void ChorusSparkleIsAddedOnlyWhereTheCutHasNoBrightTail()
    {
        var flick = Resources.Load<AudioClip>("Audio/SFX/Saber_FlickCut");
        sfx.ChorusLevel = 0;
        Assert.IsFalse(sfx.WantsChorusSparkle(JudgmentTier.Great, sfx.ClipFor(JudgmentTier.Great)), "サビ以外では足さない");
        sfx.ChorusLevel = 1;
        Assert.IsTrue(sfx.WantsChorusSparkle(JudgmentTier.Great, sfx.ClipFor(JudgmentTier.Great)));
        Assert.IsTrue(sfx.WantsChorusSparkle(JudgmentTier.Good, sfx.ClipFor(JudgmentTier.Good)));
        Assert.IsFalse(sfx.WantsChorusSparkle(JudgmentTier.Perfect, sfx.ClipFor(JudgmentTier.Perfect)), "Perfect の音には元から余韻がある");
        Assert.IsTrue(sfx.WantsChorusSparkle(JudgmentTier.Perfect, flick), "フリックの Perfect には足す");
        Assert.IsFalse(sfx.WantsChorusSparkle(JudgmentTier.Bad, sfx.ClipFor(JudgmentTier.Bad)));
        Assert.IsFalse(sfx.WantsChorusSparkle(JudgmentTier.Miss, sfx.ClipFor(JudgmentTier.Miss)));
        Assert.IsFalse(sfx.WantsChorusSparkle(JudgmentTier.Great, null), "音が無いとき(金・時間切れ)は足さない");
    }
}
