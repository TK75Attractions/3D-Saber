using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 中断・差し替え・極短音源でも、音と譜面の時計を同じ状態へ復帰させる。
public class SongPlayerRecoveryPlayTests
{
    GameObject owner;
    SongPlayer player;
    AudioClip clip, replacement;

    void Create(float seconds = 2)
    {
        owner = new GameObject("SongRecovery", typeof(AudioSource), typeof(SongPlayer));
        player = owner.GetComponent<SongPlayer>();
        owner.GetComponent<AudioSource>().playOnAwake = false;
        clip = AudioClip.Create("RecoverySilence", Mathf.RoundToInt(seconds * 48000), 1, 48000, false);
        player.Clip = clip;
    }

    [TearDown]
    public void Cleanup()
    {
        if (owner != null) Object.DestroyImmediate(owner);
        if (clip != null) Object.DestroyImmediate(clip);
        if (replacement != null) Object.DestroyImmediate(replacement);
    }

    [UnityTest]
    public IEnumerator PauseBeforeStartPreservesTheRemainingLeadIn()
    {
        Create();
        player.PlayScheduled(AudioSettings.dspTime + .5);
        player.Pause();
        double before = player.SongTime;
        Assert.Less(before, 0);
        Assert.False(player.IsPlaying);
        yield return new WaitForSecondsRealtime(.2f);
        Assert.AreEqual(before, player.SongTime, .002);
        player.Resume();
        Assert.AreEqual(before, player.SongTime, .03);
        yield return new WaitForSecondsRealtime(.65f);
        Assert.True(player.IsPlaying);
        Assert.Greater(player.SongTime, 0);
    }

    [UnityTest]
    public IEnumerator PauseDuringPlaybackFreezesBothAudioAndTheJudgmentClock()
    {
        Create();
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return new WaitForSecondsRealtime(.3f);
        double before = player.SongTime;
        player.Pause();
        yield return null;
        int sample = owner.GetComponent<AudioSource>().timeSamples;
        yield return new WaitForSecondsRealtime(.2f);
        Assert.AreEqual(before, player.SongTime, .03);
        Assert.AreEqual(sample, owner.GetComponent<AudioSource>().timeSamples);
        player.Resume();
        yield return new WaitForSecondsRealtime(.15f);
        Assert.That(player.SongTime, Is.GreaterThan(before + .1));
        double actual = owner.GetComponent<AudioSource>().timeSamples / 48000.0;
        Assert.That(player.SongTime, Is.EqualTo(actual).Within(.07));
    }

    [UnityTest]
    public IEnumerator ReplacingTheClipCancelsTheOldSongSchedule()
    {
        Create();
        player.PlayScheduled(AudioSettings.dspTime + .1);
        replacement = AudioClip.Create("ReplacementSilence", 48000, 1, 48000, false);
        player.Clip = replacement;
        Assert.False(player.IsScheduled);
        Assert.Zero(player.SongTime);
        yield return new WaitForSecondsRealtime(.2f);
        Assert.False(owner.GetComponent<AudioSource>().isPlaying);
    }

    [UnityTest]
    public IEnumerator ClipWhichEndsBeforeTheFirstClockReadDoesNotFreezeAtZero()
    {
        Create(.04f);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return new WaitForSecondsRealtime(1.25f);
        Assert.Greater(player.SongTime, 1, "音源を観測し損ねても終了判定まで時計を進める");
    }

    [UnityTest]
    public IEnumerator DisablingSongPlayerAlsoStopsItsSoundAndSchedule()
    {
        Create();
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return new WaitForSecondsRealtime(.2f);
        player.enabled = false;
        Assert.False(player.IsScheduled);
        Assert.False(owner.GetComponent<AudioSource>().isPlaying);
    }

    [UnityTest]
    public IEnumerator DestroyingOnlyASpawnerRemovesItsUnpooledNotesFromAnExternalRoot()
    {
        Create();
        var spawner = owner.AddComponent<NoteSpawner>();
        spawner.reuseNotes = false;
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        var prefab = new GameObject("RecoveryNotePrefab", typeof(CuttableNote));
        prefab.transform.SetParent(owner.transform);
        spawner.notePrefab = prefab;
        var externalRoot = new GameObject("ExternalRecoveryNotes");
        externalRoot.transform.SetParent(owner.transform);
        spawner.noteRoot = externalRoot.transform;
        spawner.SetChart(new ChartData { notes = new List<NoteData> { new NoteData { time = 1000 } } });
        spawner.Tick(1);
        var note = spawner.LiveNotes[0];
        Object.Destroy(spawner);
        yield return null;
        yield return null;
        Assert.True(note == null);
        Assert.Zero(externalRoot.transform.childCount);
    }
}
