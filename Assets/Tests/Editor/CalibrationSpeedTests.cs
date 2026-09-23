using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 判定調整画面のノーツ速度3段(2.0 / 1.0 / 0.5 秒)。下書きに含め、保存で本番へ、破棄で元へ。
public class CalibrationSpeedTests
{
    readonly string[] intKeys = { "judgmentOffsetMs", CalibrationDraft.ActiveKey, CalibrationDraft.SpeakerKey, CalibrationDraft.HeadphoneKey };
    int[] savedInts; bool[] existedInts;
    bool hadApproach; float savedApproach;

    [SetUp]
    public void Setup()
    {
        savedInts = intKeys.Select(k => PlayerPrefs.GetInt(k)).ToArray(); existedInts = intKeys.Select(PlayerPrefs.HasKey).ToArray();
        hadApproach = PlayerPrefs.HasKey("noteApproachTime"); savedApproach = GameSession.NoteApproachTime;
        foreach (var k in intKeys) PlayerPrefs.DeleteKey(k);
        GameSession.ResetNoteApproachTime();
    }

    [TearDown]
    public void Cleanup()
    {
        for (int i = 0; i < intKeys.Length; i++) { if (existedInts[i]) PlayerPrefs.SetInt(intKeys[i], savedInts[i]); else PlayerPrefs.DeleteKey(intKeys[i]); }
        if (hadApproach) GameSession.NoteApproachTime = savedApproach; else GameSession.ResetNoteApproachTime();
        PlayerPrefs.Save();
    }

    [Test]
    public void Presets_AreTwoOneAndHalfSeconds_InOrder()
    {
        CollectionAssert.AreEqual(new[] { 2f, 1f, .5f }, NoteSpeedPreset.Seconds, "1段目 2.0秒 / 2段目 1.0秒 / 3段目 0.5秒");
        Assert.AreEqual(3, NoteSpeedPreset.Count);
        Assert.AreEqual(2f, NoteSpeedPreset.SecondsFor(0)); Assert.AreEqual(1f, NoteSpeedPreset.SecondsFor(1)); Assert.AreEqual(.5f, NoteSpeedPreset.SecondsFor(2));
        Assert.AreEqual(.5f, NoteSpeedPreset.SecondsFor(99), "範囲外は端に丸める");
        StringAssert.Contains("2.0秒", NoteSpeedPreset.Caption(0, false)); StringAssert.StartsWith("●", NoteSpeedPreset.Caption(1, true));
    }

    [TestCase(2f, 0)] [TestCase(1f, 1)] [TestCase(.5f, 2)]
    [TestCase(1.4f, 1)] [TestCase(3.5f, 0)] [TestCase(.7f, 2)]
    public void StepFor_PicksTheNearestPreset(float approach, int expected)
    {
        Assert.AreEqual(expected, NoteSpeedPreset.StepFor(approach));
    }

    [Test]
    public void Draft_StartsFromSavedSpeed_AndCommitWritesTheGameSetting()
    {
        GameSession.NoteApproachTime = 1f;
        var d = new CalibrationDraft();
        Assert.AreEqual(1, d.SpeedStep); Assert.False(d.IsDirty);
        d.SetSpeedStep(0);
        Assert.AreEqual(0, d.SpeedStep); Assert.AreEqual(2f, d.ApproachTime); Assert.True(d.IsDirty, "速度の変更も未保存扱い");
        Assert.AreEqual(1f, GameSession.NoteApproachTime, "保存するまで本番の設定は変わらない");
        d.Commit();
        Assert.AreEqual(2f, GameSession.NoteApproachTime); Assert.False(d.IsDirty);
        Assert.AreEqual(0, new CalibrationDraft().SpeedStep, "次に開いたときも保存した段が選ばれている");
    }

    [Test]
    public void Draft_RestoreSaved_RevertsSpeedWithoutWriting()
    {
        GameSession.NoteApproachTime = .5f;
        var d = new CalibrationDraft(); d.SetSpeedStep(0);
        Assert.True(d.IsDirty);
        d.RestoreSaved();
        Assert.AreEqual(2, d.SpeedStep); Assert.False(d.IsDirty); Assert.AreEqual(.5f, GameSession.NoteApproachTime);
    }

    [Test]
    public void Overlay_HasThreeSpeedButtons_AndMarksTheCurrentStep()
    {
        GameSession.NoteApproachTime = 1f;
        var host = new GameObject("host", typeof(SongPlayer), typeof(NoteSpawner), typeof(ScoreManager), typeof(AudioSource));
        try
        {
            var ctl = host.AddComponent<CalibrationController>();
            ctl.Initialize(host.GetComponent<SongPlayer>(), host.GetComponent<NoteSpawner>(), host.GetComponent<ScoreManager>(), 0);
            var overlay = ctl.Overlay;
            Assert.AreEqual(3, overlay.SpeedButtonCount);
            StringAssert.StartsWith("●", overlay.SpeedCaption(1), "保存値 1.0秒 = 2段目が選択中");
            ctl.SetSpeedStep(2); overlay.Refresh();
            StringAssert.StartsWith("●", overlay.SpeedCaption(2));
            Assert.False(overlay.SpeedCaption(1).StartsWith("●"));
            Assert.True(ctl.Draft.IsDirty);
            Assert.AreEqual(1f, GameSession.NoteApproachTime, "画面で選んだだけでは保存しない");
        }
        finally
        {
            foreach (var o in Object.FindObjectsByType<CalibrationOverlay>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(o.gameObject);
            foreach (var p in Object.FindObjectsByType<SaberUIPointer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            Object.DestroyImmediate(host);
        }
    }
}
