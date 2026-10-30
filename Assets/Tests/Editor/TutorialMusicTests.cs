using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class TutorialMusicTests
{
    [Test]
    public void LoopHasExactlyEightBeatsAndPreservesSamplesWithoutLossyCompression()
    {
        var clip = Resources.Load<AudioClip>(TutorialController.MusicResourcePath);
        Assert.NotNull(clip);
        Assert.AreEqual(48000, clip.frequency);
        Assert.AreEqual(2, clip.channels);
        Assert.AreEqual(192000, clip.samples);
        Assert.AreEqual(TutorialProgram.LoopSeconds, clip.length, 1e-6);
        var importer = (AudioImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
        Assert.AreEqual(AudioCompressionFormat.PCM, importer.defaultSampleSettings.compressionFormat);
        Assert.AreEqual(AudioClipLoadType.DecompressOnLoad, importer.defaultSampleSettings.loadType);
        Assert.IsTrue(importer.defaultSampleSettings.preloadAudioData);
    }

    [Test]
    public void AllEightBeatsAreAudibleWithHeadroomAndNoLoopSeamClick()
    {
        var clip = Resources.Load<AudioClip>(TutorialController.MusicResourcePath);
        Assert.NotNull(clip);
        var samples = new float[clip.samples * clip.channels];
        Assert.IsTrue(clip.GetData(samples, 0));
        float peak = 0;
        foreach (float value in samples) peak = Mathf.Max(peak, Mathf.Abs(value));
        Assert.That(peak, Is.InRange(.3f, .89f), "曲と同じくピークを -1 dBFS 以下に抑え、無音でもない");
        for (int channel = 0; channel < clip.channels; channel++)
            Assert.Less(Mathf.Abs(samples[channel] - samples[samples.Length - clip.channels + channel]), .002f, "ループ境界の段差");
        for (int beat = 0; beat < 8; beat++)
        {
            double onset = Rms(samples, clip, beat * .5, .10);
            double tail = Rms(samples, clip, beat * .5 + .32, .10);
            Assert.Greater(onset, .03, "各表拍が聞こえる: " + beat);
            Assert.Greater(onset, tail * 2, "表拍の立ち上がりが余韻より明瞭: " + beat);
        }
    }

    [Test]
    public void PlaysAsLoudAsTheSongsEvenOnSmallSpeakers()
    {
        var clip = Resources.Load<AudioClip>(TutorialController.MusicResourcePath);
        Assert.NotNull(clip);
        var samples = new float[clip.samples * clip.channels];
        Assert.IsTrue(clip.GetData(samples, 0));
        double gain = 20 * Math.Log10(TutorialController.MusicVolume);
        double full = Loudness(samples, clip.channels, clip.frequency, 0) + gain;
        double small = Loudness(samples, clip.channels, clip.frequency, 150) + gain;
        // 2026-10-30 から曲は前もって大きくした master.ogg で鳴らす(Tools/Audio/SongLoudness/build_master.py)。練習の音は同じ道具で、
        // 拍がはっきり聞こえる決まり(上の、立ち上がり > 余韻の2倍)の中で一番大きくした(v2.5 で +12 dB して出口で削って鳴っていたのと
        // 同じ大きさ。削れはない)。
        Assert.AreEqual(-11.6, full, .5, "v2.5 で鳴っていた大きさ");
        // 会場のノートPC・モニターのスピーカーは 150 Hz 未満がほぼ出ない。曲は低音を除いても 0.1〜2.1 dB しか下がらない。
        Assert.Less(full - small, 3, "低音に偏らず、小さいスピーカーでも曲並みに聞こえる");
    }

    static double Rms(float[] samples, AudioClip clip, double start, double length)
    {
        int from = (int)(start * clip.frequency) * clip.channels;
        int count = (int)(length * clip.frequency) * clip.channels;
        double energy = 0;
        for (int i = from; i < from + count; i++) energy += samples[i] * samples[i];
        return Math.Sqrt(energy / count);
    }

    // ITU-R BS.1770 の K 特性(48 kHz の係数)で測る。ループを2周させて2周目だけを平均する(ほぼ一定の音なのでゲートは省く)。
    // highPassHz > 0 のときは、その周波数の2次バターワースのハイパスを2段重ねる(小さいスピーカーの目安)。
    static double Loudness(float[] samples, int channels, int rate, double highPassHz)
    {
        Assert.AreEqual(48000, rate, "K 特性の係数は 48 kHz 用");
        int frames = samples.Length / channels;
        double energy = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            var shelf = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, .73248077421585);
            var weighting = new Biquad(1, -2, 1, -1.99004745483398, .99007225036621);
            var filters = highPassHz > 0
                ? new[] { shelf, weighting, Biquad.HighPass(highPassHz, rate), Biquad.HighPass(highPassHz, rate) }
                : new[] { shelf, weighting };
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < frames; i++)
                {
                    double value = samples[i * channels + channel];
                    foreach (var filter in filters) value = filter.Step(value);
                    if (pass == 1) energy += value * value;
                }
        }
        return -.691 + 10 * Math.Log10(energy / frames);
    }

    sealed class Biquad
    {
        readonly double b0, b1, b2, a1, a2;
        double x1, x2, y1, y2;
        public Biquad(double b0, double b1, double b2, double a1, double a2) { this.b0 = b0; this.b1 = b1; this.b2 = b2; this.a1 = a1; this.a2 = a2; }
        public static Biquad HighPass(double hz, int rate)
        {
            double k = Math.Tan(Math.PI * hz / rate), norm = 1 / (1 + Math.Sqrt(2) * k + k * k);
            return new Biquad(norm, -2 * norm, norm, 2 * (k * k - 1) * norm, (1 - Math.Sqrt(2) * k + k * k) * norm);
        }
        public double Step(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }
}
