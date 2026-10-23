using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class AudioStopBoundaryPlayTests
{
    GameObject root;

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
    }

    [UnityTest]
    public IEnumerator GoldStopCancelsDelayedCoinAndDestroyReleasesOwnedSource()
    {
        root = new GameObject("GoldStopBoundary");
        var borrowed = root.AddComponent<AudioSource>();
        var gold = root.AddComponent<GoldNoteSfx>();
        gold.PlayCritical(Vector3.zero);
        Assert.True(root.GetComponents<AudioSource>().Any(s => s.isPlaying));

        // 無音部分の途中で止め、硬貨が鳴る時刻をまたぐ。
        gold.StopSounds();
        Assert.True(root.GetComponents<AudioSource>().All(s => !s.isPlaying));
        yield return new WaitForSecondsRealtime(.25f);
        Assert.True(root.GetComponents<AudioSource>().All(s => !s.isPlaying));

        Object.Destroy(gold);
        yield return null;
        yield return null;
        CollectionAssert.AreEqual(new[] { borrowed }, root.GetComponents<AudioSource>());
    }

    [UnityTest]
    public IEnumerator DisabledCountdownNeverPlaysItsRemainingCues()
    {
        root = new GameObject("CountdownStopBoundary", typeof(RectTransform));
        var countdown = root.AddComponent<GameStartCountdown>();
        countdown.Begin(120, "Normal");
        var sources = root.GetComponents<AudioSource>();
        Assert.IsNotEmpty(sources);
        countdown.enabled = false;

        // STARTと最後の斬撃音の予約時刻も通過させる。
        double until = Time.realtimeSinceStartupAsDouble + 3;
        while (Time.realtimeSinceStartupAsDouble < until)
        {
            Assert.True(sources.All(s => !s.isPlaying));
            yield return null;
        }
    }
}
