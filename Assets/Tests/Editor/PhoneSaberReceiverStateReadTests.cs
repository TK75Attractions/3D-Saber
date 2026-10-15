using System.Diagnostics;
using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 受信 thread が networkLifecycleLock を持ったまま Bonjour / P2P bridge の process を起動・停止している間も、
// main thread の毎フレームの読み出し(SaberInputBridge の台名、運営表示の状態)が待たされないことを確認する。
// ソケットや外部 process は使わず、lock の保持だけを再現する。
public class PhoneSaberReceiverStateReadTests
{
    const int HoldMilliseconds = 1500;

    // standalone では Unity の native object を作らず、座標取込みの managed 経路を実行する。
    public static InputPoint CreateIdleInput()
    {
#if PHONESABER_FRAME_HOST
        var input = (InputPoint)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(InputPoint));
        SetField(input, "lockObj", new object());
        SetField(input, "lockObj2", new object());
        SetField(input, "<RedCoordinateTiming>k__BackingField", new PhoneSaberCoordinateTiming());
        SetField(input, "<BlueCoordinateTiming>k__BackingField", new PhoneSaberCoordinateTiming());
        SetField(input, "redInputStats", new PhoneSaberPacketStatistics());
        SetField(input, "blueInputStats", new PhoneSaberPacketStatistics());
#else
        var go = new GameObject("新着なしの座標読み取り");
        go.SetActive(false);
        var input = go.AddComponent<InputPoint>();
#endif
        input.useDirectWorldMapping = true;
        input.camWidth = 1920; input.camHeight = 1080;
        input.sensitivity = 1;
        input.worldScale = new Vector2(5.5f, 3);
        return input;
    }

    static void SetField(InputPoint input, string name, object value) => typeof(InputPoint)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, value);

    public static Action BindIdleRead(InputPoint input)
    {
        var apply = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), input, typeof(InputPoint)
            .GetMethod("ApplyLatestInput", BindingFlags.Instance | BindingFlags.NonPublic));
        return () => apply(false);
    }

    // 受信の書込みを再現する delegate は計測前に作る。ソケット・reflection・boxing を計測へ混ぜない。
    public static Action BindPacket(InputPoint input, bool blue, bool stick = true)
    {
        var target = Expression.Constant(input);
        var writes = new System.Collections.Generic.List<Expression>();
        string suffix = blue ? "2" : "";
        Action<string, Expression> set = (field, value) => writes.Add(Expression.Assign(
            Expression.Field(target, typeof(InputPoint).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)), value));
        set("rawX" + suffix, Expression.Constant(0f));
        set("rawY" + suffix, Expression.Constant(0f));
        string prefix = blue ? "rawX2" : "rawX1";
        set(prefix + "a", Expression.Constant(-0.5f)); set(prefix + "b", Expression.Constant(0.5f));
        prefix = blue ? "rawY2" : "rawY1";
        set(prefix + "a", Expression.Constant(-0.25f)); set(prefix + "b", Expression.Constant(0.25f));
        set("rawSender" + suffix, Expression.Constant(blue ? "blue" : "red"));
        set("rawReceiveTimestampTicks" + suffix, Expression.Call(typeof(Stopwatch).GetMethod("GetTimestamp")));
        set("hasStickData" + suffix, Expression.Constant(stick));
        set("hasNewData" + suffix, Expression.Constant(true));
        writes.Add(Expression.Empty());
        return Expression.Lambda<Action>(Expression.Block(writes)).Compile();
    }

    public static void DestroyIdleInput(InputPoint input)
    {
#if !PHONESABER_FRAME_HOST
        UnityEngine.Object.DestroyImmediate(input.gameObject);
#endif
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AutomaticPredictionExpiresBeforeTheNextPeriodicRefresh(bool blue)
    {
        var input = CreateIdleInput();
        try
        {
            double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
            var expired = new PhoneSaberAutomaticLeadPolicy();
            var fresh = new PhoneSaberAutomaticLeadPolicy();
            expired.Update(now - 1, null, 44, null, now - 0.1);
            fresh.Update(now - 1, null, 60, null, now + 10);
            SetField(input, "redPrediction", blue ? fresh : expired);
            SetField(input, "bluePrediction", blue ? expired : fresh);
            SetField(input, "<PredictionAutomatic>k__BackingField", true);
            SetField(input, "<LastReceivedMonotonicTime>k__BackingField", now);
            SetField(input, "<LastReceivedMonotonicTime2>k__BackingField", now);
            // 次の定期更新より前でも、受信時計で期限切れになった色だけは即座に解除する。
            SetField(input, "nextPredictionRefresh", now + 1);
            SetField(input, "networkShutdown", false);
            var update = (Action)Delegate.CreateDelegate(typeof(Action), input, typeof(InputPoint)
                .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic));
            update();
            Assert.AreEqual(0, input.PredictionLead(blue), "受信時計の期限切れを次の定期更新まで持ち越さない");
            Assert.AreEqual("fallback", input.PredictionSource(blue));
            Assert.AreEqual(60, input.PredictionLead(!blue), "有効な別色の予測量は維持する");
        }
        finally { DestroyIdleInput(input); }
    }

    [TestCase("lockObj")]
    [TestCase("lockObj2")]
    public void IdleCoordinatesDoNotWaitForReceiverLock(string field)
    {
        var input = CreateIdleInput();
        Action read = BindIdleRead(input);
        object gate = typeof(InputPoint).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input);
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            lock (gate) { acquired.Set(); release.Wait(HoldMilliseconds); }
        }) { IsBackground = true };
        try
        {
            read(); // JIT は計測外。
            holder.Start();
            Assert.IsTrue(acquired.Wait(2000));
            var watch = Stopwatch.StartNew();
            read();
            watch.Stop();
            Assert.Less(watch.ElapsedMilliseconds, 500, "新着のない色の受信 lock で main thread を止めない");
        }
        finally
        {
            release.Set(); holder.Join(HoldMilliseconds + 1000);
            DestroyIdleInput(input);
        }
    }

    [Test]
    public void IdleCoordinatesAllocateZeroBytes()
    {
        var input = CreateIdleInput();
        try
        {
            Action read = BindIdleRead(input);
            for (int i = 0; i < 1000; i++) read();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++) read();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
        }
        finally { DestroyIdleInput(input); }
    }

    [Test]
    public void BothColoursConsumePacketsAndAllocateZeroBytesBetweenPackets()
    {
        var input = CreateIdleInput();
        try
        {
            Action read = BindIdleRead(input), red = BindPacket(input, false), blue = BindPacket(input, true);
            Action point = BindPacket(input, false, false);
            red(); read();
            Assert.IsTrue(input.HasValidStickEndpoints);
            Assert.IsFalse(input.HasValidStickEndpoints2);
            blue(); read();
            Assert.AreEqual(new Vector2(-2.75f, -0.75f), input.LocalStickRawA);
            Assert.AreEqual(input.LocalStickRawA, input.LocalStickRawA2);
            Assert.AreEqual(new Vector2(2.75f, 0.75f), input.LocalStickRawB2);
            point(); read();
            Assert.IsFalse(input.HasValidStickEndpoints);
            Assert.IsTrue(input.HasValidStickEndpoints2);
            red(); blue(); read();
            for (int i = 0; i < 1000; i++) { red(); blue(); read(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++)
            {
                if (i % 32 == 0) { red(); blue(); }
                read();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
            Assert.IsTrue(input.HasValidStickEndpoints);
            Assert.IsTrue(input.HasValidStickEndpoints2);
        }
        finally { DestroyIdleInput(input); }
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [TestCase("pendingFreezeReceiveLog1")]
    [TestCase("pendingFreezeReceiveLog2")]
    public void PendingDiagnosticIsDrainedEvenWithoutNewCoordinates(string field)
    {
        var input = CreateIdleInput();
        try
        {
            SetField(input, field, "受信停止診断");
            BindIdleRead(input)();
            Assert.IsNull(typeof(InputPoint).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(input));
        }
        finally { DestroyIdleInput(input); }
    }
#endif

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
