using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PerfBoundaryTests
{
    void Tick(PhoneSaberPerfLog log, double at)
    {
        Time.realtimeSinceStartupAsDouble = at;
        typeof(PhoneSaberPerfLog).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(log, null);
    }

    [TestCase("scene")]
    [TestCase("vsync")]
    [TestCase("target")]
    public void NewConditionsDoNotInheritOldWindow(string change)
    {
        PhoneSaberEventLog.Lines.Clear();
        SceneManager.Active = new Scene { name="Menu", handle=1 };
        QualitySettings.vSyncCount = 1; Application.targetFrameRate = -1;
        var log = new PhoneSaberPerfLog();
        Tick(log, 0); Tick(log, 1); Tick(log, 4);
        if (change == "scene") SceneManager.Active = new Scene { name="Game", handle=2 };
        if (change == "vsync") QualitySettings.vSyncCount = 0;
        if (change == "target") Application.targetFrameRate = 30;
        Tick(log, 5); Tick(log, 10);
        Assert.AreEqual(0, PhoneSaberEventLog.Lines.Count, "古い条件の5秒を新しい条件のperfに混ぜない");
        Tick(log, 15);
        Assert.AreEqual(1, PhoneSaberEventLog.Lines.Count);
        StringAssert.Contains("fps=0.2", PhoneSaberEventLog.Lines[0]);
    }
}
#if AFTER
public class AutoLatencyWindowTests
{
    static void Invoke(PhoneSaberPerfLog log, string method) => typeof(PhoneSaberPerfLog)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(log, null);
    [Test] public void ColourBreakdownsUseTheirOwnCoordinateSender()
    {
        Time.realtimeSinceStartupAsDouble = 0;
        var log = new PhoneSaberPerfLog();
        var input = new InputPoint { RedCoordinateSender="red", BlueCoordinateSender="blue",
            LastReceivedMonotonicTime=9.993, LastReceivedMonotonicTime2=9.986, StationLabel="A" };
        InputPoint.Instance = input;
        PhoneSaberEventLog.Lines.Clear(); PhoneSaberEventLog.Kinds.Clear();
        Invoke(log, "OnEnable"); Invoke(log, "Update");
        try
        {
            var text = System.IO.File.ReadAllLines("PhoneSaber/tools/fixtures/protocol_metrics.tsv")[1].Split('\t')[1];
            var red = System.Text.Encoding.ASCII.GetBytes(text);
            var blue = System.Text.Encoding.ASCII.GetBytes(text.Replace("36.9", "80.0").Replace("44.1", "85.0"));
            input.Metrics.Receive(red, red.Length, "red", 5); input.Metrics.Receive(blue, blue.Length, "blue", 5);
            input.RedCoordinateTiming.Applied(9.993, 9.998); input.RedCoordinateTiming.Read(9.999);
            input.BlueCoordinateTiming.Applied(9.986, 9.998); input.BlueCoordinateTiming.Read(9.999);
            input.RedCoordinateTiming.BeforeRender(10, 1); input.BlueCoordinateTiming.BeforeRender(10, 1);
            Time.realtimeSinceStartupAsDouble = 10; Invoke(log, "Update");
            int r = PhoneSaberEventLog.Kinds.IndexOf("RED:latency-estimate");
            int b = PhoneSaberEventLog.Kinds.IndexOf("BLUE:latency-estimate");
            Assert.GreaterOrEqual(r, 0); Assert.GreaterOrEqual(b, 0);
            StringAssert.Contains("total-ms=53.8", PhoneSaberEventLog.Lines[r]);
            StringAssert.Contains("total-ms=103.9", PhoneSaberEventLog.Lines[b]);
            input.BlueCoordinateSender = "127.0.0.1";
            StringAssert.Contains("P2P", PhoneSaberPerfLog.LatencyLine(true, 10));
            StringAssert.Contains("届いていません", PhoneSaberPerfLog.LatencyLine(true, 10));
        }
        finally { Invoke(log, "OnDisable"); InputPoint.Instance = null; }
    }
    [Test] public void SceneChangesDoNotPostponeTenSecondDiagnosticLog()
    {
        Time.realtimeSinceStartupAsDouble = 0;
        var log = new PhoneSaberPerfLog(); InputPoint.Instance = new InputPoint();
        PhoneSaberEventLog.Lines.Clear(); PhoneSaberEventLog.Kinds.Clear(); Invoke(log, "OnEnable");
        try
        {
            for (int i = 0; i <= 10; i++)
            {
                SceneManager.Active = new Scene {name="changing",handle=100+i};
                Time.realtimeSinceStartupAsDouble = i * 2; Invoke(log, "Update");
            }
            Assert.AreEqual(4, PhoneSaberEventLog.Kinds.Count);
            foreach (string kind in PhoneSaberEventLog.Kinds) StringAssert.EndsWith(":latency-estimate", kind);
        }
        finally { Invoke(log, "OnDisable"); InputPoint.Instance = null; }
    }
}
public class CoordinateTimingTests
{
    [Test]
    public void ShownLatencyDoesNotDependOnReadersAndDoesNotGrowWhileInputIsSilent()
    {
        // 2026-10-13 の実機: 曲選択では計測用 getter を誰も読まず、age が数十秒まで伸びて「遅延の目安」に入っていた。
        var t = new PhoneSaberCoordinateTiming();
        t.BeforeRender(0.5, 1);
        Assert.AreEqual(0, t.Shown.Count, "座標が来る前は標本なし");
        t.Applied(1, 1.001); t.BeforeRender(1.004, 2);
        Assert.AreEqual(1, t.Shown.Count); Assert.AreEqual(4, t.Shown.MedianMs, 1e-9);
        // 同じ座標のまま何秒描いても増えない（読み出しも無い）。
        for (int frame = 3; frame < 600; frame++) t.BeforeRender(1 + frame * 0.016, frame);
        Assert.AreEqual(1, t.Shown.Count);
        // 次の座標は、その受信から最初の描画直前まで。同じフレームの2回目の呼び出しは数えない。
        t.Applied(20, 20.002); t.BeforeRender(20.010, 600); t.BeforeRender(20.011, 600);
        Assert.AreEqual(2, t.Shown.Count); Assert.AreEqual(10, t.Shown.P95Ms, 1e-9);
        t.ResetSource(); t.BeforeRender(21, 601);
        Assert.AreEqual(0, t.Shown.Count, "送信元が替わったら前の端末の座標を数えない");
        t.Applied(22, 22.001); t.BeforeRender(22.003, 602);
        Assert.AreEqual(1, t.Shown.Count); Assert.AreEqual(3, t.Shown.MedianMs, 1e-9);
    }

    [Test] public void SourceSwitchDoesNotAttributeOldReadToNewPhone()
    {
        var t = new PhoneSaberCoordinateTiming();
        t.Applied(1, 1.001); t.Read(1.002); t.BeforeRender(1.003, 1);
        t.ResetSource(); t.Applied(2, 2.001); t.BeforeRender(2.003, 2);
        Assert.AreEqual(0, t.Age.Count);
        t.Read(2.004); t.BeforeRender(2.005, 3);
        Assert.AreEqual(1, t.Age.Count); Assert.AreEqual(5, t.Age.MedianMs, 1e-9);
    }
    [Test] public void RepeatedReadAndRenderDoNotCountPacketTwice()
    {
        var t = new PhoneSaberCoordinateTiming();
        t.Applied(1, 1.005); t.Read(1.006); t.Read(1.008);
        t.BeforeRender(1.010, 1); t.BeforeRender(1.011, 1); t.BeforeRender(1.020, 2);
        Assert.AreEqual(1, t.FirstRead.Count); Assert.AreEqual(6, t.FirstRead.MedianMs, 1e-9);
        Assert.AreEqual(1, t.FirstRender.Count); Assert.AreEqual(10, t.FirstRender.MedianMs, 1e-9);
        Assert.AreEqual(2, t.Age.Count); Assert.AreEqual(15, t.Age.MedianMs, 1e-9);
    }
    [Test] public void NoNewPacketPreservesSampleAndFirstUse()
    {
        var t = new PhoneSaberCoordinateTiming();
        t.Applied(1, 1.005); t.Read(1.006); t.Applied(1, 1.020); t.Read(1.021);
        Assert.IsFalse(t.NeedsFirstRead); Assert.AreEqual(1, t.Apply.Count);
        Assert.AreEqual(5, t.Apply.MedianMs, 1e-9); Assert.AreEqual(1, t.FirstRead.Count);
    }
    [Test] public void UnreadNewPacketIsNotReportedAsDisplayed()
    {
        var t = new PhoneSaberCoordinateTiming();
        t.Applied(1, 1.005); t.Read(1.006); t.BeforeRender(1.010, 1);
        t.Applied(1.015, 1.016); t.BeforeRender(1.020, 2);
        Assert.AreEqual(15, t.Age.MedianMs, 1e-9); Assert.AreEqual(1, t.FirstRender.Count);
        t.Read(1.021); t.BeforeRender(1.025, 3);
        Assert.AreEqual(2, t.FirstRender.Count); Assert.AreEqual(6, t.FirstRead.MedianMs, 1e-9);
    }
    [Test] public void SkippedPacketsHaveNoFirstUseSample()
    {
        var t = new PhoneSaberCoordinateTiming();
        t.Applied(1, 1.001); t.Applied(1.002, 1.003); t.Read(1.004); t.BeforeRender(1.006, 1);
        Assert.AreEqual(2, t.Apply.Count); Assert.AreEqual(1, t.FirstRead.Count);
        Assert.AreEqual(4, t.FirstRender.MedianMs, 1e-9);
    }
    [Test] public void EmptyWindowIsUnknownAndResetKeepsSampleIdentity()
    {
        var t = new PhoneSaberCoordinateTiming(); t.BeforeRender(1, 1);
        StringAssert.Contains("median-ms=unknown", t.Format("red"));
        t.Applied(1, 1.005); t.Read(1.006); t.BeforeRender(1.010, 2); t.ClearWindow();
        t.Read(1.020); t.BeforeRender(1.025, 3);
        Assert.AreEqual(0, t.FirstRead.Count); Assert.AreEqual(0, t.FirstRender.Count);
        Assert.AreEqual(25, t.Age.MedianMs, 1e-9);
    }
    [Test] public void HotTimingPathDoesNotAllocate()
    {
        var t = new PhoneSaberCoordinateTiming();
        for(int i=0;i<100;i++) {t.Applied(i,i+.001); t.Read(i+.002); t.BeforeRender(i+.003,i);}
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=100;i<10100;i++) {t.Applied(i,i+.001); t.Read(i+.002); t.BeforeRender(i+.003,i);}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.AreEqual(0,allocated);
        Assert.AreEqual(10100,t.Age.Count);
    }
    [Test] public void CapacityIsBoundedAndDropsAreVisible()
    {
        var t = new PhoneSaberCoordinateTiming(); t.Applied(0,0); t.Read(0);
        for(int i=0;i<20000;i++) t.BeforeRender(i*.001,i);
        Assert.AreEqual(16384,t.Age.Count); StringAssert.Contains("timing-dropped=3616",t.Format("red"));
    }
}
public class PresentationTests
{
    [TestCase(60,60,1,30,"unknown",0,"vsync")]
    [TestCase(30,60,2,-1,"unknown",0,"vsync")]
    [TestCase(120,60,0,120,"unknown",0,"target")]
    [TestCase(30,60,1,-1,"1",0,"os-low-power-suspected")]
    [TestCase(30,60,1,-1,"unknown",0,"cpu-or-os-unknown")]
    [TestCase(30,60,1,-1,"0",33,"gpu-suspected")]
    [TestCase(900,60,0,-1,"1",0,"uncapped-or-cpu-os-unknown")]
    public void LimiterUsesEvidence(double fps,double hz,int vsync,int target,string low,double gpu,string expected)
    { Assert.AreEqual(expected,PhoneSaberPresentationDiagnosis.Limiter(fps,hz,vsync,target,low,gpu)); }
}
#endif

class Run
{
    static int Main()
    {
        int passed=0,failed=0;
        foreach(var type in Assembly.GetExecutingAssembly().GetTypes())
        foreach(var method in type.GetMethods())
        {
            var cases=method.GetCustomAttributes(typeof(TestCaseAttribute),false);
            if(cases.Length==0 && !method.IsDefined(typeof(TestAttribute),false)) continue;
            if(cases.Length==0) cases=new object[]{null};
            foreach(var item in cases)
            {
                object[] args=item==null?new object[0]:((TestCaseAttribute)item).Arguments;
                var parameters=method.GetParameters();
                for(int i=0;i<args.Length;i++) args[i]=Convert.ChangeType(args[i],parameters[i].ParameterType);
                try {method.Invoke(Activator.CreateInstance(type),args);passed++;}
                catch(Exception e) {Console.WriteLine("FAIL "+method.Name+" "+(e.InnerException??e).Message); failed++;}
            }
        }
        Console.WriteLine("NUnit host: "+passed+" PASS / "+failed+" FAIL"); return failed==0?0:1;
    }
}
