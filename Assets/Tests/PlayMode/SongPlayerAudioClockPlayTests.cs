using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 判定設定や実譜面を変更せず、音声スレッドの実カーソルと比較する。
public class SongPlayerAudioClockPlayTests
{
    GameObject owner;
    AudioClip clip;
    AudioSource source;
    SongPlayer player;

    void Create(float duration = 3f)
    {
        owner = new GameObject("AudioClockTest", typeof(AudioSource));
        source = owner.GetComponent<AudioSource>();
        source.playOnAwake = false;
        player = owner.AddComponent<SongPlayer>();
        clip = AudioClip.Create("AudioClockTestSilence", Mathf.RoundToInt(duration * 48000), 1, 48000, false);
        player.Clip = clip;
    }

    [TearDown]
    public void Cleanup()
    {
        if (player != null) player.Stop();
        if (owner != null) Object.DestroyImmediate(owner);
        if (clip != null) Object.DestroyImmediate(clip);
    }

    IEnumerator AwaitSamples(double seconds)
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 4;
        while (source.timeSamples / (double)clip.frequency < seconds && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.GreaterOrEqual(source.timeSamples / (double)clip.frequency, seconds, "実際の音声再生が始まること");
    }

    void AssertSynchronized()
    {
        double time = player.SongTime;
        double actual = source.timeSamples / (double)clip.frequency;
        // 読み取り間に音声バッファが切り替わる場合のみ1ブロックまで許容する。
        AudioSettings.GetDSPBufferSize(out int frames, out _);
        Assert.That(time, Is.EqualTo(actual).Within(frames / (double)AudioSettings.outputSampleRate + .003));
    }

    [UnityTest]
    public IEnumerator LateAudioStartDoesNotLeaveNotesAheadForTheRestOfTheSong()
    {
        Create();
        player.PlayScheduled(AudioSettings.dspTime + .05);
        System.Threading.Thread.Sleep(350); // 開始フレームが予約時刻をまたぐ不具合を再現する。
        yield return AwaitSamples(.1);
        for (int i = 0; i < 15; i++) { AssertSynchronized(); yield return null; }
    }

    [UnityTest]
    public IEnumerator PreRollAndReplayKeepTheirOwnStartAndResetOldAudioPosition()
    {
        Create();
        player.PlayScheduled(AudioSettings.dspTime + .3);
        Assert.IsTrue(player.IsScheduled);
        Assert.Less(player.SongTime, -.2);
        Assert.IsFalse(player.IsPlaying);
        yield return AwaitSamples(.2);
        AssertSynchronized();
        player.Stop();
        Assert.AreEqual(0, player.SongTime);
        Assert.IsFalse(player.IsScheduled);
        player.PlayScheduled(AudioSettings.dspTime + .3);
        Assert.AreEqual(0, source.timeSamples);
        Assert.Less(player.SongTime, -.2);
        yield return AwaitSamples(.1);
        AssertSynchronized();
    }

    [UnityTest]
    public IEnumerator LoopClockAndOutroContinueInsteadOfReturningToZero()
    {
        Create(.3f);
        source.loop = true;
        player.PlayScheduled(AudioSettings.dspTime + .1);
        yield return AwaitSamples(.05);
        AssertSynchronized();
        double deadline = Time.realtimeSinceStartupAsDouble + 3;
        double previous = player.SongTime;
        while (player.SongTime < .75 && Time.realtimeSinceStartupAsDouble < deadline)
        {
            Assert.GreaterOrEqual(player.SongTime, previous);
            previous = player.SongTime;
            yield return null;
        }
        Assert.GreaterOrEqual(player.SongTime, .75);
        source.loop = false;
        while (source.isPlaying && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsFalse(source.isPlaying);
        double end = player.SongTime;
        yield return new WaitForSecondsRealtime(.1f);
        Assert.Greater(player.SongTime, end + .05, "音源終了後もリザルトまでの余韻を進める");
    }
}
