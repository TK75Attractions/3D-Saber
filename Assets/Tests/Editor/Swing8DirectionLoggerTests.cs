using NUnit.Framework;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;

public class Swing8DirectionLoggerTests
{
    private readonly List<GameObject> created = new List<GameObject>();
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [TearDown]
    public void Cleanup()
    {
        for (int i = created.Count - 1; i >= 0; i--)
        {
            if (created[i] == null) continue;
            foreach (var component in created[i].GetComponents<MonoBehaviour>())
            {
                Lifecycle(component, "OnDisable");
                Lifecycle(component, "OnDestroy");
            }
            Object.DestroyImmediate(created[i]);
        }
        created.Clear();
    }

    private Swing8DirectionLogger MakeLogger(UdpImuBridge bridge = null)
    {
        var go = new GameObject("SwingLoggerTest");
        created.Add(go);
        var logger = go.AddComponent<Swing8DirectionLogger>();
        Lifecycle(logger, "Awake");
        Lifecycle(logger, "OnEnable");
        if (bridge != null)
            typeof(Swing8DirectionLogger).GetMethod("Subscribe", PrivateInstance)
                .Invoke(logger, new object[] { bridge });
        return logger;
    }

    // EditModeではUnityの起動・停止コールバックを明示的に実行する。
    private static void Lifecycle(MonoBehaviour component, string name)
    {
        component.GetType().GetMethod(name, PrivateInstance)?.Invoke(component, null);
    }

    private static SwingEvent SwingAt(long received)
    {
        return new SwingEvent(1, SwingDirection.Left, 0.8f, 10, received, double.NaN);
    }

    private static void Deliver(Swing8DirectionLogger logger, SwingEvent swing)
    {
        typeof(Swing8DirectionLogger).GetMethod("HandleSwing", PrivateInstance)
            .Invoke(logger, new object[] { swing });
    }

    [TestCase(0f)]
    [TestCase(0.1f)]
    [TestCase(2f)]
    public void DelayedDelivery_ExpiresFromReceiptRegardlessOfTimeScale(float timeScale)
    {
        var logger = MakeLogger();
        long received = System.Diagnostics.Stopwatch.Frequency * 10;
        long At(double seconds) => received + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
        float previousScale = Time.timeScale;
        try
        {
            Time.timeScale = timeScale;
            Deliver(logger, SwingAt(received).WithMainThreadHandoff(At(0.12)));
            Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(0.30, At(0.29), out CutDirection direction));
            Assert.AreEqual(CutDirection.Left, direction);
            Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(0.30, At(0.29), out _), "参照しても消費しない");
            Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(0.30, At(0.31), out direction));
            Assert.AreEqual(CutDirection.None, direction);
        }
        finally { Time.timeScale = previousScale; }
    }

    [TestCase(0.299999, 0.30, true)]
    [TestCase(0.30, 0.30, true)]
    [TestCase(0.300001, 0.30, false)]
    [TestCase(0.0, 0.0, true)]
    [TestCase(0.001, 0.0, false)]
    [TestCase(0.0, -0.1, false)]
    [TestCase(-0.001, 0.30, false)]
    public void RecentHint_RespectsInclusiveBoundaryAndRejectsFutureReceipt(double age, double maximum, bool expected)
    {
        var logger = MakeLogger();
        long received = System.Diagnostics.Stopwatch.Frequency * 10;
        Deliver(logger, SwingAt(received));
        long now = received + (long)(age * System.Diagnostics.Stopwatch.Frequency);
        Assert.AreEqual(expected, Swing8DirectionLogger.TryGetRecent(maximum, now, out _));
    }

    [Test]
    public void DisableThenEnable_DoesNotRestoreCachedOrQueuedHint()
    {
        var logger = MakeLogger();
        Deliver(logger, SwingAt(SwingMonotonicClock.Timestamp));
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out _));
        logger.enabled = false;
        Lifecycle(logger, "OnDisable");
        Assert.IsFalse(Swing8DirectionLogger.TryGetLatest(out _, out _));
        var queuedWhileDisabled = SwingAt(SwingMonotonicClock.Timestamp);
        logger.enabled = true;
        Lifecycle(logger, "OnEnable");
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
        Deliver(logger, queuedWhileDisabled);
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
        Deliver(logger, SwingAt(SwingMonotonicClock.Timestamp));
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out _));
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<System.Action> callbacks = new Queue<System.Action>();
        public override void Post(SendOrPostCallback callback, object state) => callbacks.Enqueue(() => callback(state));
        public void Drain()
        {
            while (callbacks.Count > 0) callbacks.Dequeue()();
        }
    }

    private UdpImuBridge MakeBridge(QueuedContext context)
    {
        var go = new GameObject("SwingBridgeTest");
        created.Add(go);
        var bridge = go.AddComponent<UdpImuBridge>();
        // キューと購読だけを検証。ソケット開始とDontDestroyOnLoadはPlayModeテストで確認する。
        bridge.ConfigureForTests(0);
        typeof(UdpImuBridge).GetField("mainThreadContext", PrivateInstance).SetValue(bridge, context);
        return bridge;
    }

    private static void Receive(UdpImuBridge bridge, string packet)
    {
        typeof(UdpImuBridge).GetMethod("ProcessReceivedMessage", PrivateInstance)
            .Invoke(bridge, new object[] { packet, SwingMonotonicClock.Timestamp });
    }

    [Test]
    public void DisconnectAndReconnect_ClearHintAndAcceptFreshSwingInSameQueue()
    {
        var context = new QueuedContext();
        var bridge = MakeBridge(context);
        MakeLogger(bridge);
        Receive(bridge, "SWING:7,left,0.8,123"); // STATEなしでも従来どおり受理。
        context.Drain();
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out _));
        Receive(bridge, "STATE:DISCONNECTED");
        context.Drain();
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
        Receive(bridge, "STATE:CONNECTED");
        context.Drain();
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
        // 状態通知の処理より前に受信済みでも、新しいセッションの入力は捨てない。
        Receive(bridge, "STATE:CONNECTED");
        Receive(bridge, "SWING:0,right,0.8,456");
        context.Drain();
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out CutDirection direction));
        Assert.AreEqual(CutDirection.Right, direction);
    }

    [Test]
    public void BridgeReenabled_RejectsHintQueuedBeforeRestart()
    {
        var context = new QueuedContext();
        var bridge = MakeBridge(context);
        MakeLogger(bridge);
        Receive(bridge, "SWING:1,left,0.8,123");
        context.Drain();
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out _));
        Receive(bridge, "SWING:2,left,0.8,124");
        bridge.enabled = false;
        Lifecycle(bridge, "OnDisable");
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
        bridge.enabled = true;
        Lifecycle(bridge, "OnEnable");
        context.Drain();
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
        Receive(bridge, "SWING:3,right,0.8,125");
        context.Drain();
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out CutDirection direction));
        Assert.AreEqual(CutDirection.Right, direction);
    }

    [Test]
    public void DestroyedBridge_ClearsItsDirectionHint()
    {
        var context = new QueuedContext();
        var bridge = MakeBridge(context);
        MakeLogger(bridge);
        Receive(bridge, "SWING:1,left,0.8,123");
        context.Drain();
        Assert.IsTrue(Swing8DirectionLogger.TryGetRecent(1, out _));
        Lifecycle(bridge, "OnDisable");
        Lifecycle(bridge, "OnDestroy");
        Object.DestroyImmediate(bridge.gameObject);
        Assert.IsFalse(Swing8DirectionLogger.TryGetRecent(1, out _));
    }

    [Test]
    public void Get8DirectionIndex_RightIsZero()
    {
        Assert.AreEqual(0, Swing8DirectionLogger.Get8DirectionIndex(new Vector2(1, 0)));
    }

    [Test]
    public void Get8DirectionIndex_UpIsTwo()
    {
        Assert.AreEqual(2, Swing8DirectionLogger.Get8DirectionIndex(new Vector2(0, 1)));
    }

    [Test]
    public void Get8DirectionIndex_DiagonalUpRightIsOne()
    {
        Assert.AreEqual(1, Swing8DirectionLogger.Get8DirectionIndex(new Vector2(1, 1)));
    }

    [Test]
    public void FromSwing8Index_MatchesEnum()
    {
        Assert.AreEqual(CutDirection.Right, CutDirectionHelper.FromSwing8Index(0));
        Assert.AreEqual(CutDirection.UpRight, CutDirectionHelper.FromSwing8Index(1));
        Assert.AreEqual(CutDirection.Up, CutDirectionHelper.FromSwing8Index(2));
        Assert.AreEqual(CutDirection.DownRight, CutDirectionHelper.FromSwing8Index(7));
        Assert.AreEqual(CutDirection.None, CutDirectionHelper.FromSwing8Index(99));
    }

    [Test]
    public void SwingEventDirections_MapToExistingCutDirections()
    {
        Assert.AreEqual(0, Swing8DirectionLogger.ToLegacyDirectionIndex(SwingDirection.Right));
        Assert.AreEqual(2, Swing8DirectionLogger.ToLegacyDirectionIndex(SwingDirection.Up));
        Assert.AreEqual(4, Swing8DirectionLogger.ToLegacyDirectionIndex(SwingDirection.Left));
        Assert.AreEqual(6, Swing8DirectionLogger.ToLegacyDirectionIndex(SwingDirection.Down));
        Assert.AreEqual(-1, Swing8DirectionLogger.ToLegacyDirectionIndex(SwingDirection.Unknown));
    }

    [Test]
    public void TryGetLatest_NoInstance_ReturnsFalse()
    {
        // 直前テストで Instance が残っていないことを確認しつつ既定挙動を検証。
        foreach (var l in Object.FindObjectsByType<Swing8DirectionLogger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Object.DestroyImmediate(l.gameObject);
        }
        bool ok = Swing8DirectionLogger.TryGetLatest(out CutDirection d, out float t);
        Assert.IsFalse(ok);
        Assert.AreEqual(CutDirection.None, d);
    }
}
