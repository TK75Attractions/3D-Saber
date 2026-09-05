using NUnit.Framework;
using UnityEngine;

public class JudgmentSfxTests
{
    [TearDown]
    public void Cleanup()
    {
        foreach (var s in Object.FindObjectsByType<JudgmentSfx>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (s != null) Object.DestroyImmediate(s.gameObject);
        }
    }

    [Test]
    public void Beep_ReturnsValidClip()
    {
        AudioClip clip = JudgmentSfx.Beep(440f, 0.1f);
        Assert.IsNotNull(clip);
        Assert.AreEqual(1, clip.channels);
        Assert.Greater(clip.samples, 0);
    }

    [Test]
    public void Buzz_ReturnsValidClip()
    {
        AudioClip clip = JudgmentSfx.Buzz(110f, 0.1f);
        Assert.IsNotNull(clip);
        Assert.Greater(clip.samples, 0);
    }

    [Test]
    public void ClipFor_UnassignedTiersRemainPlayable()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        // クリップ未指定でも null を返さない
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Perfect));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Great));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Good));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Bad));
        Assert.IsNotNull(sfx.ClipFor(JudgmentTier.Miss));
    }

    [Test]
    public void ClipFor_PrefersAssignedClip()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        sfx.perfectClip = JudgmentSfx.Beep(1000f, 0.05f);
        Assert.AreSame(sfx.perfectClip, sfx.ClipFor(JudgmentTier.Perfect));
    }

    [Test]
    public void ClipFor_DefaultHitsUseBundledCutButMissDoesNot()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        var cut = Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut");
        Assert.IsNotNull(cut, "通常カット音がビルドに含まれる場所から読める");
        Assert.AreEqual(48000, cut.frequency);
        Assert.AreEqual(1, cut.channels);
        Assert.AreEqual(0.22f, cut.length, 0.001f);
        foreach (var tier in new[] { JudgmentTier.Perfect, JudgmentTier.Great, JudgmentTier.Good, JudgmentTier.Bad })
            Assert.AreSame(cut, sfx.ClipFor(tier));
        Assert.AreNotSame(cut, sfx.ClipFor(JudgmentTier.Miss));
    }

    [Test]
    public void ClipForCut_DistinguishesTapFlickAndLong_AndKeepsMissSound()
    {
        var go = new GameObject("sfx", typeof(AudioSource));
        var sfx = go.AddComponent<JudgmentSfx>();
        var tap = sfx.ClipForCut(JudgmentTier.Perfect, CutDirection.None, 1);
        var flick = sfx.ClipForCut(JudgmentTier.Perfect, CutDirection.Up, 1);
        var longEnd = sfx.ClipForCut(JudgmentTier.Perfect, CutDirection.None, 4);
        Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/Saber_NoteCut"), tap);
        Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/Saber_FlickCut"), flick);
        Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/Saber_LongFinish"), longEnd);
        Assert.IsNotNull(flick);
        Assert.IsNotNull(longEnd);
        Assert.AreNotSame(tap, flick);
        Assert.AreNotSame(flick, longEnd);
        Assert.AreNotSame(tap, longEnd);
        Assert.AreSame(longEnd, sfx.ClipForCut(JudgmentTier.Great, CutDirection.Up, 4));
        Assert.AreSame(sfx.ClipFor(JudgmentTier.Miss), sfx.ClipForCut(JudgmentTier.Miss, CutDirection.Up, 1));
    }

    [TestCase(1, "none", false, "Saber_NoteCut")]
    [TestCase(1, "up", false, "Saber_FlickCut")]
    [TestCase(3, "up", false, "Saber_LongFinish")]
    [TestCase(3, "none", true, null)]
    public void NoteJudgment_RoutesActualCutsAndSuppressesLongTimeout(
        int count, string direction, bool timeOut, string expectedName)
    {
        var root = new GameObject("sfx", typeof(AudioSource), typeof(JudgmentSfx));
        var sfx = root.GetComponent<JudgmentSfx>();
        var score = root.AddComponent<ScoreManager>();
        sfx.scoreManager = score;
        var spawner = root.AddComponent<NoteSpawner>();
        var prefab = new GameObject("notePrefab", typeof(CuttableNote));
        prefab.transform.SetParent(root.transform);
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.buildTimingCues = false;
        score.Bind(spawner);
        CuttableNote note = null;
        spawner.OnNoteSpawned += n => note = n;
        AudioClip received = null;
        bool judged = false;
        score.OnJudgment += (tier, _) =>
        {
            judged = true;
            received = sfx.ClipForCurrentJudgment(tier);
        };
        var chart = new ChartData { bpm = 120f };
        chart.notes.Add(new NoteData { time = 0, count = count, direction = direction });
        spawner.SetChart(chart);
        spawner.Tick(0);
        Assert.IsNotNull(note);
        note.shatterDebrisCount = 0;
        int cuts = timeOut ? 1 : count;
        for (int i = 0; i < cuts; i++)
            note.Cut(Vector3.zero, Vector3.up * 10f, CutDirection.Up, SaberHand.Any);
        if (timeOut) note.MarkMiss();

        Assert.IsTrue(judged);
        Assert.AreEqual(count, score.LastCutCount);
        Assert.AreEqual(CutDirectionHelper.Parse(direction), score.LastCutDirection);
        Assert.AreEqual(timeOut, score.LastCutTimedOut);
        if (expectedName == null) Assert.IsNull(received, "時間切れで完了音を出さない");
        else Assert.AreSame(Resources.Load<AudioClip>("Audio/SFX/" + expectedName), received);

        score.Reset();
        Assert.AreEqual(1, score.LastCutCount);
        Assert.AreEqual(CutDirection.None, score.LastCutDirection);
        Assert.IsFalse(score.LastCutTimedOut);
    }
}
