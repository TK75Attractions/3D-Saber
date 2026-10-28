using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 音の出力先がサンプルレートの違う機器へ変わると、AudioSettings.dspTime が「それまでのサンプル数 ÷ 新しいレート」に跳ぶ
// (2026-10-28 ユーザー「演奏中に音の出力先が変わると強制的にリザルト画面に行ってしまってる」)。
// 本物の作り直し(AudioSettings.Reset でサンプルレートを変える)のあとも、曲の時計は実時間どおりに続くことを確かめる。
public class SongPlayerDspJumpPlayTests
{
    AudioConfiguration original;
    GameObject go;
    AudioClip clip;

    [SetUp]
    public void SetUp()
    {
        original = AudioSettings.GetConfiguration();
    }

    [TearDown]
    public void TearDown()
    {
        if (go != null) Object.Destroy(go);
        if (clip != null) Object.Destroy(clip);
        var now = AudioSettings.GetConfiguration();
        if (now.sampleRate != original.sampleRate) AudioSettings.Reset(original);
    }

    SongPlayer MakePlayer()
    {
        go = new GameObject("SongPlayerDspJump", typeof(AudioSource));
        var player = go.AddComponent<SongPlayer>();
        int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
        clip = AudioClip.Create("DspJumpTone", rate * 60, 1, rate, false);
        var samples = new float[rate * 60];
        for (int i = 0; i < samples.Length; i++) samples[i] = .02f * Mathf.Sin(2 * Mathf.PI * 220f * i / rate);
        clip.SetData(samples, 0);
        player.Clip = clip;
        return player;
    }

    IEnumerator WaitUntilPlaying(SongPlayer player)
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (!(player.IsPlaying && player.SongTime > .5) && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.True(player.IsPlaying, "曲が鳴り始める");
    }

    [UnityTest]
    public IEnumerator SongClockKeepsRealTimeAcrossASampleRateChange()
    {
        var player = MakePlayer();
        player.Play();
        yield return WaitUntilPlaying(player);

        foreach (int rate in new[] { original.sampleRate == 44100 ? 48000 : 44100, original.sampleRate })
        {
            double songBefore = player.SongTime, realBefore = Time.realtimeSinceStartupAsDouble, dspBefore = AudioSettings.dspTime;
            int followed = player.DspJumpsFollowed;
            var config = AudioSettings.GetConfiguration();
            config.sampleRate = rate;
            Assert.True(AudioSettings.Reset(config), "サンプルレートを変えて音の仕組みを作り直す");
            yield return null;
            double songAfter = player.SongTime, realAfter = Time.realtimeSinceStartupAsDouble, dspAfter = AudioSettings.dspTime;
            Debug.Log($"SongPlayerDspJumpPlayTests: rate={rate} dsp {dspBefore:F3}→{dspAfter:F3} real +{realAfter - realBefore:F3} "
                + $"song {songBefore:F3}→{songAfter:F3} jump={player.LastDspJump:F3}");
            Assert.AreEqual(followed + 1, player.DspJumpsFollowed, "作り直しのあとの最初の読みで跳んだ分を直す");
            Assert.That(songAfter - songBefore, Is.EqualTo(realAfter - realBefore).Within(.25),
                "曲の時計は実時間どおりに進む(DSP の時計の跳びを持ち込まない)");
            Assert.True(player.IsPlaying, "曲は鳴っている扱いのまま");
            yield return new WaitForSecondsRealtime(.3f);
            double later = player.SongTime;
            Assert.That(later - songAfter, Is.EqualTo(Time.realtimeSinceStartupAsDouble - realAfter).Within(.25), "そのあとも同じ速さで進む");
        }
    }

    // 作り直しの知らせが無いときは、何もずらさない(ふだんの音の遅れに実時間を混ぜない)。
    [UnityTest]
    public IEnumerator WithoutAnAudioResetTheClockIsNotTouched()
    {
        var player = MakePlayer();
        player.Play();
        yield return WaitUntilPlaying(player);
        int followed = player.DspJumpsFollowed;
        for (int i = 0; i < 20; i++) { _ = player.SongTime; yield return null; }
        Assert.AreEqual(followed, player.DspJumpsFollowed);
    }
}
