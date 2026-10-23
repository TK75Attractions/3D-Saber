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

    // 止めて音を差し替え、再開する(本編の立て直しと同じ順)。
    double ReplaceAndResume()
    {
        player.Pause();
        double held = player.SongTime;
        player.ReplaceClipWhilePaused(clip);
        player.Resume();
        return held;
    }

    // 鳴らし直したフレームが予約時刻をまたぐ(重いフレーム)。音は予約より遅れて始まる(LateAudioStart… と同じ再現)。
    double ReplaceAndDelayAudio(out int seek)
    {
        double held = ReplaceAndResume();
        seek = source.timeSamples;
        System.Threading.Thread.Sleep(350);
        return held;
    }

    double RestartAndDelayAudio(out int seek)
    {
        source.Stop();
        player.RestartSource();
        double held = player.SongTime;
        seek = source.timeSamples;
        System.Threading.Thread.Sleep(350);
        return held;
    }

    // 音の出力先が変わって曲を読み直したあと(本編)。鳴らし直しが遅れて始まっても、時計は音より先へ進まない。
    [UnityTest]
    public IEnumerator RecoveryWaitsForSamplesAndRemainsSynchronized()
    {
        Create(6f);
        player.PlayScheduled(AudioSettings.dspTime + .05, 2.0);
        yield return AwaitSamples(2.2);
        AssertSynchronized();
        double held = ReplaceAndDelayAudio(out int seek);
        Assert.That(seek / (double)clip.frequency, Is.EqualTo(held).Within(1.0 / clip.frequency), "止めた位置から鳴らし直す");
        Assert.AreEqual(2.0, player.PlayStartSeconds);
        AssertSynchronized();
        yield return AwaitSamples(held + .1);
        for (int i = 0; i < 15; i++) { AssertSynchronized(); yield return null; }
    }

    // 練習(同梱の音を今の位置から鳴らし直す)。
    [UnityTest]
    public IEnumerator RestartSourceWaitsForSamplesAndRemainsSynchronized()
    {
        Create(6f);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.2);
        AssertSynchronized();
        double before = player.SongTime;
        double held = RestartAndDelayAudio(out int seek);
        Assert.GreaterOrEqual(held, before);
        Assert.That(held, Is.EqualTo(seek / (double)clip.frequency).Within(1.0 / clip.frequency), "今の曲時刻の位置から鳴らし直す");
        AssertSynchronized();
        yield return AwaitSamples(held + .1);
        for (int i = 0; i < 15; i++) { AssertSynchronized(); yield return null; }
    }

    [UnityTest]
    public IEnumerator ReplacedLoopKeepsItsLapsWhenTheFirstReadIsAfterTheWrap() => VerifyLoopRecovery(true);

    [UnityTest]
    public IEnumerator RestartedLoopKeepsItsLapsWhenTheFirstReadIsAfterTheWrap() => VerifyLoopRecovery(false);

    IEnumerator VerifyLoopRecovery(bool replace)
    {
        Create(1f);
        source.loop = true;
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.05);
        AssertSynchronized();
        double deadline = Time.realtimeSinceStartupAsDouble + 4;
        while (player.SongTime < 2.65 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.That(player.SongTime, Is.InRange(2.65, 2.85), "2周済みで末尾の手前から立て直す");
        int seek;
        double held = replace ? ReplaceAndDelayAudio(out seek) : RestartAndDelayAudio(out seek);
        Assert.That(seek / (double)clip.frequency, Is.InRange(.6, .95));

        // 再生進行を確認してから折り返すまで、SongTime は読まない。
        yield return AwaitSamples(seek / (double)clip.frequency + .02);
        deadline = Time.realtimeSinceStartupAsDouble + 2;
        while ((source.timeSamples == 0 || source.timeSamples >= seek) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.True(source.isPlaying);
        Assert.Greater(source.timeSamples, 0);
        Assert.Less(source.timeSamples, seek);
        double time = player.SongTime;
        double actual = held + (clip.samples - seek + source.timeSamples) / (double)clip.frequency;
        AudioSettings.GetDSPBufferSize(out int frames, out _);
        Assert.That(time, Is.EqualTo(actual).Within(frames / (double)AudioSettings.outputSampleRate + .003));
        Assert.GreaterOrEqual(time, held);

        deadline = Time.realtimeSinceStartupAsDouble + 1.2;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            double next = player.SongTime;
            Assert.GreaterOrEqual(next, time);
            time = next;
            yield return null;
        }
        source.loop = false;
        deadline = Time.realtimeSinceStartupAsDouble + 2;
        while (source.isPlaying && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.False(source.isPlaying);
        double end = player.SongTime;
        yield return new WaitForSecondsRealtime(.1f);
        Assert.Greater(player.SongTime, end + .05);
    }

    [UnityTest]
    public IEnumerator PauseDuringRecoveryCancelsTheOldScheduleAndPreservesThePlace()
    {
        Create(6f);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.2);
        AssertSynchronized();
        foreach (bool replaceAgain in new[] { false, true })
        {
            // 鳴らし直しの予約時刻より前に、また止める(続けて出力先が変わったとき)。
            double held = ReplaceAndResume();
            player.Pause();
            Assert.That(player.SongTime, Is.EqualTo(held).Within(.000001));
            yield return new WaitForSecondsRealtime(.3f);
            Assert.That(player.SongTime, Is.EqualTo(held).Within(.000001));
            Assert.False(source.isPlaying, "取り消した予約時刻を過ぎても鳴らさない");
            if (replaceAgain) player.ReplaceClipWhilePaused(clip);
            player.Resume();
            Assert.That(player.SongTime, Is.EqualTo(held).Within(.000001));
            yield return AwaitSamples(held + .1);
            AssertSynchronized();
        }
    }

    [UnityTest]
    public IEnumerator PendingRecoveryIsClearedByStopNewScheduleAndReplacement()
    {
        Create(6f);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.2);
        AssertSynchronized();
        ReplaceAndResume();
        player.Stop();
        Assert.Zero(player.SongTime);
        Assert.False(player.IsScheduled);
        player.PlayScheduled(AudioSettings.dspTime + .3);
        Assert.Less(player.SongTime, -.2);
        Assert.Zero(source.timeSamples);
        yield return AwaitSamples(.1);
        AssertSynchronized();

        ReplaceAndResume();
        player.PlayScheduled(AudioSettings.dspTime + .3, 1.5);
        Assert.That(player.SongTime, Is.InRange(1.1, 1.3));
        Assert.AreEqual(1.5, player.PlayStartSeconds);
        yield return AwaitSamples(1.6);
        AssertSynchronized();

        ReplaceAndResume();
        var other = AudioClip.Create("AudioClockTestOther", clip.samples, 1, clip.frequency, false);
        player.ReplaceClipWhilePaused(other);
        Assert.False(player.IsScheduled, "止めていないときの差し替えは、待っている鳴らし直しも取り消す");
        Assert.Zero(player.SongTime);
        yield return new WaitForSecondsRealtime(.45f);
        Assert.False(source.isPlaying);
        player.Clip = clip;
        Object.DestroyImmediate(other);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.1);
        AssertSynchronized();

        double held = ReplaceAndResume();
        player.Pause();
        player.ReplaceClipWhilePaused(null);
        player.Resume();
        yield return new WaitForSecondsRealtime(.1f);
        Assert.Greater(player.SongTime, held + .05);
        Assert.IsNull(player.Clip);
    }

    [UnityTest]
    public IEnumerator RecoveryWithoutSampleProgressFallsBackToDsp()
    {
        Create(1f);
        source.loop = true;
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.2);
        AssertSynchronized();
        double held = ReplaceAndResume();
        int seek = source.timeSamples;
        source.Stop(); // 再生装置の不調でサンプルが進まない場合を代用する。
        yield return new WaitForSecondsRealtime(.15f);
        Assert.That(player.SongTime, Is.EqualTo(held).Within(.000001), "停止による0への戻りをループと誤認しない");
        double limit = System.Math.Max(1.0, clip.length - seek / (double)clip.frequency + .25);
        yield return new WaitForSecondsRealtime((float)(limit + SongPlayer.RestartLeadSeconds + .1));
        double after = player.SongTime;
        Assert.Greater(after, held + limit);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.Greater(player.SongTime, after + .05);
    }

    [UnityTest]
    public IEnumerator RecoveryWhoseTailEndsBeforeTheFirstReadFallsBackToDsp()
    {
        Create(2f);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.2);
        AssertSynchronized();
        player.Pause();
        double held = player.SongTime;
        int end = (int)System.Math.Round(held * clip.frequency) + 48;
        var shortClip = AudioClip.Create("RecoveryShortTail", end, 1, clip.frequency, false);
        try
        {
            player.ReplaceClipWhilePaused(shortClip);
            player.Resume();
            yield return new WaitForSecondsRealtime(1.25f);
            Assert.False(source.isPlaying);
            Assert.Greater(player.SongTime, held + 1);
        }
        finally
        {
            player.Stop();
            Object.DestroyImmediate(shortClip);
        }
    }

    [UnityTest]
    public IEnumerator RecoveryAfterTheClipEndsContinuesTheOutro()
    {
        Create(.3f);
        player.PlayScheduled(AudioSettings.dspTime + .05);
        yield return AwaitSamples(.05);
        AssertSynchronized();
        yield return new WaitForSecondsRealtime(.5f);
        Assert.Greater(player.SongTime, player.Duration);
        player.Pause();
        double held = player.SongTime;
        player.ReplaceClipWhilePaused(clip);
        player.Resume();
        Assert.That(player.SongTime, Is.EqualTo(held).Within(.000001));
        yield return new WaitForSecondsRealtime(.2f);
        Assert.Greater(player.SongTime, held + .1);
        Assert.False(source.isPlaying);
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

    // 頭の無音を飛ばす曲(仕様面の点検 7)。SongTime は音源の頭からの秒のまま、鳴らし始めた瞬間に開始位置になり、実際の音声と揃う。
    [UnityTest]
    public IEnumerator StartOffsetPlaysFromTheMiddleOnTheSameClock()
    {
        Create(6f);
        player.PlayScheduled(AudioSettings.dspTime + .3, 2.0);
        Assert.AreEqual(2.0, player.PlayStartSeconds, 1 / 48000.0);
        Assert.IsFalse(player.IsPlaying, "予約中はまだ鳴っていない");
        Assert.That(player.SongTime, Is.InRange(1.6, 1.9), "鳴らし始めまでは開始位置の手前を数える");
        System.Threading.Thread.Sleep(350); // 開始フレームが予約時刻をまたいでも、実際の音声が進むまで開始位置で待つ。
        yield return AwaitSamples(2.1);
        Assert.IsTrue(player.IsPlaying);
        for (int i = 0; i < 15; i++) { AssertSynchronized(); yield return null; }
        player.Pause();
        double paused = player.SongTime;
        Assert.Greater(paused, 2.0);
        player.Resume();
        Assert.AreEqual(paused, player.SongTime, .02);
        player.Stop();
        Assert.AreEqual(0, player.PlayStartSeconds);
        player.PlayScheduled(AudioSettings.dspTime + .1);
        Assert.AreEqual(0, player.PlayStartSeconds, "次の再生は頭から(前の開始位置を残さない)");
        player.PlayScheduled(AudioSettings.dspTime + .1, 5.5);
        Assert.AreEqual(0, player.PlayStartSeconds, "最後の1秒より後ろからは鳴らさない");
        player.PlayScheduled(AudioSettings.dspTime + .1, double.NaN);
        Assert.AreEqual(0, player.PlayStartSeconds);
    }

    // 開始前(カウント中)に止めて再開しても、開始位置から鳴らす。
    [UnityTest]
    public IEnumerator PauseBeforeTheOffsetStartResumesFromTheOffset()
    {
        Create(6f);
        player.PlayScheduled(AudioSettings.dspTime + .4, 1.5);
        player.Pause();
        Assert.Less(player.SongTime, 1.5);
        yield return new WaitForSecondsRealtime(.2f);
        player.Resume();
        yield return AwaitSamples(1.6);
        for (int i = 0; i < 10; i++) { AssertSynchronized(); yield return null; }
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
