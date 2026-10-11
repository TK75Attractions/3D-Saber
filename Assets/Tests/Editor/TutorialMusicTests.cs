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
        Assert.That(peak, Is.InRange(.3f, .7f), "効果音を重ねる余裕を残し、無音でもない");
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

    static double Rms(float[] samples, AudioClip clip, double start, double length)
    {
        int from = (int)(start * clip.frequency) * clip.channels;
        int count = (int)(length * clip.frequency) * clip.channels;
        double energy = 0;
        for (int i = from; i < from + count; i++) energy += samples[i] * samples[i];
        return Math.Sqrt(energy / count);
    }
}
