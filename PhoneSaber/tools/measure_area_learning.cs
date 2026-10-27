using System;
using System.Linq;
using UnityEngine;
#if ACK_TESTS
using NUnit.Framework;
#endif

// 保存・送信・ログ境界の計数専用。Unityの変換精度・永続化・ネットワークを実測しない。
namespace UnityEngine
{
    public struct Vector2 { }
    public struct Rect
    {
        public float xMin, yMin, xMax, yMax;
        public float width => xMax - xMin;
        public float height => yMax - yMin;
        public static Rect MinMaxRect(float a, float b, float c, float d) => new Rect { xMin = a, yMin = b, xMax = c, yMax = d };
        public static bool operator ==(Rect a, Rect b) => a.xMin == b.xMin && a.yMin == b.yMin && a.xMax == b.xMax && a.yMax == b.yMax;
        public static bool operator !=(Rect a, Rect b) => !(a == b);
        public override bool Equals(object obj) => obj is Rect r && this == r;
        public override int GetHashCode() => HashCode.Combine(xMin, yMin, xMax, yMax);
    }
    public static class Debug { public static void LogWarning(string value) { } }
}
public class Capture { public void Cancel() { } }
public class Calibration { public object CopyCorners() => null; }
public static class PhoneSaberPositionCalibration
{
    public static bool TryFromLearnedArea(Rect b, float w, float h, out Calibration c, out string error)
    { c = new Calibration(); error = "invalid"; return w > 1 && h > 1 && b.width > .01 && b.height > .01; }
    public static bool TryCreate(Vector2[] corners, out Calibration c, out string error)
    { c = new Calibration(); error = ""; return true; }
    public static string LearnedAreaResult(Rect b, bool applied, string error) => applied ? "ok" : "failed";
}
public static class PhoneSaberPositionCalibrationStore
{
    public static int Saves;
    public static void Save(string station, object calibration, string at = null, string result = null) { Saves++; }
    public static void Reset(string station) { }
    public static void SetEnabled(string station, bool value) { }
}
public static class TutorialRangeBaseline
{
    public static int Sends;
    public static object PhoneAreaFor(object corners, float w, float h) => null;
    public static void SendPhoneArea(object area) { Sends++; }
}
public static class PhoneSaberEventLog
{
    public static int Applies, Acks, Summaries;
    public static void Record(string station, string colour, string kind, string detail)
    {
        if (kind == "learned-area-apply") Applies++;
        if (kind == "phone-ack") Acks++;
        if (kind == "phone-ack-duplicates") Summaries++;
    }
}
public static partial class PhoneSaberDiscoveryResponder
{
    public const string DefaultsCommand = "defaults=1";
    static string commandStation = "A";
    public static PhoneSaberAcknowledgementStore Acknowledgements;
    public static string BuildCommand(int id, string fields) => "id=" + id + " " + fields;
}
public partial class InputPoint
{
    public string StationLabel = "A";
    public float camWidth = 1920, camHeight = 1080;
    PhoneSaberAcknowledgementStore learnedAreaStore;
    int learnedAreaRevision = -1;
    string failedLearnedPositionCalibration;
    Calibration positionCalibration;
    bool positionCalibrationEnabled;
    public bool HasPositionCalibration => positionCalibration != null;
    public int PredictionRevision;
    public Capture PositionCapture = new Capture();
}
class Program
{
    static readonly Rect Bounds = Rect.MinMaxRect(.2f, .1f, .8f, .9f);
    static PhoneSaberAcknowledgement Ack(int id, string station = "A", string result = "learned", string detail = "samples300-area0.2_0.1_0.8_0.9") =>
        new PhoneSaberAcknowledgement(id, station, "ios", result, detail);
    static void Check(bool success, string message) { if (!success) throw new Exception(message); }
    static void Measure(bool tutorialFirst, bool wrapper, bool after)
    {
        var store = new PhoneSaberAcknowledgementStore(); PhoneSaberDiscoveryResponder.Acknowledgements = store;
        var input = new InputPoint();
        PhoneSaberPositionCalibrationStore.Saves = TutorialRangeBaseline.Sends = 0;
        PhoneSaberEventLog.Applies = PhoneSaberEventLog.Acks = PhoneSaberEventLog.Summaries = 0;
        int revisions = 0, suppressed = 0, successes = 0;
        for (int id = 1; id <= 15; id++)
        {
            store.Queue(id, "learn-area=10", id * 20);
            int queued = store.Revision;
            for (int n = 0; n < 63; n++) PhoneSaberDiscoveryResponder.ReceiveAndRecord(Ack(id, result: "learning", detail: "10s"), "phone");
            Check(store.PendingCommands(id * 20 + 1, "phone").Length == 1, "learning must keep retrying");
            PhoneSaberDiscoveryResponder.ReceiveAndRecord(Ack(id), "phone");
            revisions += store.Revision - queued;
            var counter = typeof(PhoneSaberAcknowledgementStore).GetMethod("ReadSuppressedCount");
            if (counter != null) suppressed += (int)counter.Invoke(store, new object[] { id });
            Func<bool> tutorial = () => wrapper ? TutorialController.ApplyLearnedRange(input, Bounds) : input.ApplyLearnedPositionCalibration(Bounds, "tutorial", out _);
            bool result;
            if (tutorialFirst) { result = tutorial(); input.ApplyLearnedPositionCalibration(store, "automatic"); }
            else { input.ApplyLearnedPositionCalibration(store, "automatic"); result = tutorial(); }
            if (result) successes++;
            Check(store.PendingCommands(id * 20 + 1, "phone").Length == 0, "completion must stop retries");
            Check(store.Read(id)[0].Result == "learned", "completion lost");
        }
        int expected = after || wrapper && tutorialFirst ? 15 : 30;
        Check(PhoneSaberPositionCalibrationStore.Saves == expected && TutorialRangeBaseline.Sends == expected &&
            input.PredictionRevision == expected && PhoneSaberEventLog.Applies == expected && successes == 15, "apply measurement mismatch");
        Check(revisions == (after ? 30 : 960) && PhoneSaberEventLog.Acks == (after ? 30 : 960), "ACK measurement mismatch");
        Check(suppressed == (after ? 930 : 0) && PhoneSaberEventLog.Summaries == (after ? 15 : 0), "aggregate measurement mismatch");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
            entry = wrapper ? "TutorialController" : "Rect", order = tutorialFirst ? "tutorial-auto" : "auto-tutorial", commands = 15,
            saves = PhoneSaberPositionCalibrationStore.Saves, sends = TutorialRangeBaseline.Sends, prediction = input.PredictionRevision,
            applyLogs = PhoneSaberEventLog.Applies, ackRevisions = revisions, ackLogs = PhoneSaberEventLog.Acks,
            summaryLogs = PhoneSaberEventLog.Summaries, suppressed, tutorialSuccess = successes }));
    }
    static void Scenarios()
    {
        int checks = 0;
        foreach (string station in new[] { "A", "B" })
        foreach (string phone in new[] { station, "none" })
        {
            var store = new PhoneSaberAcknowledgementStore(); PhoneSaberDiscoveryResponder.Acknowledgements = store;
            var input = new InputPoint { StationLabel = station };
            store.Queue(1, "learn-area=10", 0); store.Receive(Ack(1, phone), "phone");
            Check(input.ApplyLearnedPositionCalibration(store, "first"), "station/none must apply");
            Check(input.ApplyLearnedPositionCalibration(Bounds, "cached", out _), "cached result must succeed");
            Check(input.PredictionRevision == 1, "cached result applied twice");
            store.Queue(2, "learn-area=10", 20); store.Receive(Ack(1, phone), "phone");
            Check(!input.ApplyLearnedPositionCalibration(Bounds, "old", out _), "old command resurrected");
            store.Receive(Ack(2, phone), "phone"); Check(input.ApplyLearnedPositionCalibration(store, "new"), "new ID/same Rect must apply");
            Check(input.PredictionRevision == 2, "new command count"); checks++;
        }
        foreach (bool manual in new[] { false, true })
        foreach (bool cached in new[] { false, true })
        {
            var store = new PhoneSaberAcknowledgementStore(); PhoneSaberDiscoveryResponder.Acknowledgements = store;
            var input = new InputPoint(); store.Queue(1, "learn-area=10", 0);
            if (cached) { store.Receive(Ack(1), "phone"); Check(input.ApplyLearnedPositionCalibration(store, "first"), "initial apply"); }
            if (manual) input.SavePositionCalibration(new Vector2[4], out _); else input.ResetPositionCalibration();
            int revision = input.PredictionRevision;
            store.Receive(Ack(1), "phone");
            Check(!input.ApplyLearnedPositionCalibration(store, "late") && !input.ApplyLearnedPositionCalibration(Bounds, "late", out _), "discarded command resurrected");
            Check(input.PredictionRevision == revision, "discard changed prediction"); checks++;
        }
        foreach (string[] phones in new[] { new[] { "B" }, new[] { "none", "none" }, new[] { "A", "A" } })
        {
            var store = new PhoneSaberAcknowledgementStore(); PhoneSaberDiscoveryResponder.Acknowledgements = store;
            var input = new InputPoint(); store.Queue(1, "learn-area=10", 0);
            for (int i = 0; i < phones.Length; i++) store.Receive(Ack(1, phones[i]), "phone" + i);
            Check(!input.ApplyLearnedPositionCalibration(Bounds, "ambiguous", out _), "ambiguous/other station Rect accepted");
            Check(input.PredictionRevision == 0, "ambiguous Rect applied"); checks++;
        }
        {
            var store = new PhoneSaberAcknowledgementStore(); PhoneSaberDiscoveryResponder.Acknowledgements = store;
            var input = new InputPoint { camWidth = 1 }; store.Queue(1, "learn-area=10", 0); store.Receive(Ack(1), "phone");
            Check(!input.ApplyLearnedPositionCalibration(store, "failed"), "invalid conversion accepted");
            input.camWidth = 1920;
            Check(!input.ApplyLearnedPositionCalibration(Bounds, "retry", out _), "failed application retried");
            Check(input.PredictionRevision == 0, "failed application reset prediction"); checks++;
        }
        {
            var store = new PhoneSaberAcknowledgementStore(); PhoneSaberDiscoveryResponder.Acknowledgements = store;
            var input = new InputPoint(); store.Queue(1, "learn-area=10", 0); store.Receive(Ack(1, "none"), "none-phone");
            Check(input.ApplyLearnedPositionCalibration(store, "first"), "none initial apply");
            store.Receive(Ack(1), "explicit-phone");
            Check(!input.ApplyLearnedPositionCalibration(Bounds, "other", out _) && !input.ApplyLearnedPositionCalibration(store, "other"), "cached result reused for another sender");
            Check(input.PredictionRevision == 1, "other sender applied again"); checks++;
        }
        Console.WriteLine($"application scenarios (host): {checks} passed");
    }
#if ACK_TESTS
    static void AckTests()
    {
        int passed = 0;
        foreach (var method in typeof(PhoneSaberAcknowledgementTests).GetMethods())
        {
            var cases = method.GetCustomAttributes(typeof(TestCaseAttribute), true).Cast<TestCaseAttribute>().Select(a => a.Arguments).ToArray();
            if (cases.Length == 0 && method.IsDefined(typeof(TestAttribute), true)) cases = new[] { Array.Empty<object>() };
            foreach (var args in cases)
            {
                try { method.Invoke(new PhoneSaberAcknowledgementTests(), args); passed++; }
                catch (System.Reflection.TargetInvocationException error) { throw new Exception(method.Name, error.InnerException); }
            }
        }
        Console.WriteLine($"AcknowledgementTests (host): {passed} passed");
    }
#endif
    static void Main(string[] args)
    {
        bool after = args[0] == "after";
        foreach (bool wrapper in new[] { false, true }) foreach (bool first in new[] { false, true }) Measure(first, wrapper, after);
        if (after) Scenarios();
#if ACK_TESTS
        AckTests();
#endif
    }
}
