using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

// 受信 thread が networkLifecycleLock を持ったまま Bonjour / P2P bridge の process を起動・停止している間も、
// main thread の毎フレームの読み出し(SaberInputBridge の台名、運営表示の状態)が待たされないことを確認する。
// ソケットや外部 process は使わず、lock の保持だけを再現する。
public class PhoneSaberReceiverStateReadTests
{
    const int HoldMilliseconds = 1500;

    [Test]
    public void StationAndServiceStatusDoNotWaitForReceiverLifecycleLock()
    {
        var go = new GameObject("受信機の状態読み出しテスト");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
        var type = typeof(InputPoint);
        type.GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, "B");
        object gate = type.GetField("networkLifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input);
        Assert.NotNull(gate);

        using (var acquired = new ManualResetEventSlim(false))
        using (var release = new ManualResetEventSlim(false))
        {
            var holder = new Thread(() =>
            {
                lock (gate)
                {
                    acquired.Set();
                    release.Wait(HoldMilliseconds);
                }
            }) { IsBackground = true, Name = "PhoneSaber lifecycle lock holder (test)" };
            try
            {
                holder.Start();
                Assert.IsTrue(acquired.Wait(2000), "テスト用 thread が lock を取得できない");

                var watch = Stopwatch.StartNew();
                string station = input.StationLabel;
                bool bonjour = input.BonjourPublisherRunning;
                bool discovery = input.DiscoveryResponderRunning;
                watch.Stop();

                Assert.AreEqual("B", station);
                Assert.IsFalse(bonjour);
                Assert.IsFalse(discovery);
                Assert.Less(watch.ElapsedMilliseconds, 500,
                    "受信 thread の lifecycle 処理中も台名・状態の読み出しで main thread を止めない");
            }
            finally
            {
                release.Set();
                holder.Join(HoldMilliseconds + 1000);
                Object.DestroyImmediate(go);
            }
        }
    }

    [Test]
    public void StationLabelIsEmptyBeforeReceiversStart()
    {
        var go = new GameObject("台名の初期値テスト");
        go.SetActive(false);
        try
        {
            var input = go.AddComponent<InputPoint>();
            Assert.AreEqual("", input.StationLabel);
        }
        finally { Object.DestroyImmediate(go); }
    }
}
