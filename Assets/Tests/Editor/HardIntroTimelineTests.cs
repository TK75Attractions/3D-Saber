using NUnit.Framework;
using UnityEngine;

public class HardIntroTimelineTests
{
    [TestCase("Hard", true)] [TestCase("HARD", true)] [TestCase("easy", false)]
    [TestCase("Normal", false)] [TestCase(null, false)]
    public void OnlyHardUsesTheIntro(string difficulty, bool expected)
    { Assert.AreEqual(expected, HardIntroTimeline.EnabledFor(difficulty)); }

    [Test]
    public void PhotosAreContiguousAndFirstAttachmentIsLast()
    {
        Assert.AreEqual(-1, HardIntroTimeline.PhotoAt(4.39f));
        Assert.AreEqual(0, HardIntroTimeline.PhotoAt(4.4f));
        Assert.AreEqual(1, HardIntroTimeline.PhotoAt(8.8f));
        Assert.AreEqual(2, HardIntroTimeline.PhotoAt(13.2f));
        Assert.AreEqual(-1, HardIntroTimeline.PhotoAt(17.8f));
        Assert.AreEqual("UI/HardIntro/campus-gate", HardIntroTimeline.PhotoResource(2));
        for (int i = 0; i < 3; i++) Assert.NotNull(Resources.Load<Texture2D>(HardIntroTimeline.PhotoResource(i)));
    }

    [Test]
    public void TitlePrecedesDifficultyAndNoiseClearsBeforeLines()
    {
        Assert.Less(HardIntroTimeline.NameEnd, HardIntroTimeline.LevelStart);
        Assert.Less(HardIntroTimeline.ClearEnd, HardIntroTimeline.LineStart);
        Assert.Greater(HardIntroTimeline.NoiseAt(12f), .20f);
        Assert.AreEqual(0, HardIntroTimeline.NoiseAt(HardIntroTimeline.LineStart));
        for (int i = 0; i < 8; i++)
        {
            Assert.Greater(Mathf.Abs(HardIntroTimeline.LineOffset(HardIntroTimeline.LineStart, i, false).x), 1280);
            Assert.AreEqual(Vector2.zero, HardIntroTimeline.LineOffset(HardIntroTimeline.PlayStart, i, false));
            Assert.AreEqual(Vector2.zero, HardIntroTimeline.LineOffset(20f, i, true));
        }
    }

    [Test]
    public void OriginalTransparentGlyphsAndCompleteSignalAreIncluded()
    {
        int[] widths = { 540, 569, 311, 348, 109, 225, 352 };
        for (int i = 1; i <= 7; i++)
        {
            var glyph = Resources.Load<Texture2D>("UI/HardIntro/glyph-" + i.ToString("D2"));
            Assert.NotNull(glyph); Assert.AreEqual(widths[i - 1], glyph.width);
        }
        var clip = Resources.Load<AudioClip>("UI/HardIntro/MorseSignal");
        Assert.NotNull(clip); Assert.AreEqual(24f, clip.length, .001f);
        Assert.AreEqual(1, clip.channels);
    }
}
