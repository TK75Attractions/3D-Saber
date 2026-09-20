using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class CameraImuLifecyclePlayTests
{
    [UnityTest]
    public IEnumerator DisableClearsPendingAndReleasesLegacyWithoutAnotherTick()
    {
        var root = new GameObject("CameraImuLifecycle");
        var saber = root.AddComponent<SaberCutJudge>(); saber.autonomous = false;
        var fusion = root.AddComponent<CameraImuJudgment>();
        fusion.GetComponent<GameplaySwingAdapter>().readLegacyStream = false;
        fusion.mode = CameraImuMode.ImuAndCamera;
        fusion.Configure(null, saber, null);
        try
        {
            yield return null;
            double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            fusion.ReceiveSwing(new GameplaySwing(1, PhysicalSaberSide.Right, now));
            fusion.Tick(now, 1);
            Assert.That(fusion.PendingSwingCount, Is.EqualTo(1)); Assert.True(saber.ExternalJudgment);
            fusion.enabled = false;
            Assert.That(fusion.PendingSwingCount, Is.Zero); Assert.False(saber.ExternalJudgment);
            fusion.enabled = true;
            fusion.Tick(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp), 1.01);
            Assert.That(fusion.PendingSwingCount, Is.Zero);
        }
        finally { Object.Destroy(root); }
        yield return null;
    }
}
