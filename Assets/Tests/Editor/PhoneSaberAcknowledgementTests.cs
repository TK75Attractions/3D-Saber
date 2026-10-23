using System;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

public class PhoneSaberAcknowledgementTests
{
    static PhoneSaberAcknowledgement Ack(int id, string result, string detail, string platform = "ios", string station = "none") =>
        new PhoneSaberAcknowledgement(id, station, platform, result, detail);

    [Test]
    public void LearnedBoundsAckAcceptsIosFormatAndRejectsMalformedRectangles()
    {
        const string prefix = "PHONESABER_ACK 1 id=71 station=A platform=ios result=learned detail=";
        const string detail = "samples300-area0.200000_0.100000_0.800000_0.900000";
        Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(prefix + detail, out var ack));
        Assert.AreEqual("300", ack.SampleCount);
        Assert.IsTrue(ack.TryLearnedBounds(out var bounds));
        Assert.AreEqual(.2f, bounds.xMin); Assert.AreEqual(.9f, bounds.yMax);
        Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(prefix + "samples300", out var legacy));
        Assert.IsFalse(legacy.TryLearnedBounds(out _));
        foreach (string bad in new[] { "samples29-area0_0_1_1", "samples300-area0_0_0_1",
            "samples300-area0_0_1_0", "samples300-area0_0_1.1_1", "samples300-area-0.1_0_1_1",
            "samples300-area0_0_NaN_1", "samples300-area0_0_Infinity_1", "samples300-area0_0_1",
            "samples300-area0_0_1_1_1", "samples300-area0_0_1_1-area0_0_1_1" })
            Assert.IsFalse(PhoneSaberAcknowledgement.TryParse(prefix + bad, out _), bad);
        Assert.IsFalse(PhoneSaberAcknowledgement.TryParse((prefix + detail).Replace("result=learned", "result=failed"), out _));
        StringAssert.DoesNotContain("area", PhoneSaberOperatorKeys.ResultText(ack, "learn-area=10"));
    }

    [Test]
    public void ResetDiscardsPendingLearningForOnlyThatStation()
    {
        var store = new PhoneSaberAcknowledgementStore();
        const string detail = "samples300-area0.2_0.1_0.8_0.9";
        store.Queue(1, "learn-area=10", 0);
        store.DiscardLearnedAreas("A");
        store.Receive(Ack(1, "learned", detail, station: "A"), "phoneA");
        store.Receive(Ack(1, "learned", detail, station: "B"), "phoneB");
        Assert.IsFalse(store.TakeLearnedArea("A", out _));
        Assert.IsTrue(store.TakeLearnedArea("B", out _));
        store.Queue(2, "learn-area=10", 20);
        store.Receive(Ack(2, "learned", detail, station: "A"), "phoneA");
        Assert.IsTrue(store.TakeLearnedArea("A", out _));
    }

    [Test]
    public void OnlyLatestLearnCommandCanApplyAndOtherCommandsCannotSupplyBounds()
    {
        const string detail = "samples300-area0.2_0.1_0.8_0.9";
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(1, "learn-area=10", 0);
        store.Queue(2, "learn-area=10", 1);
        store.Receive(Ack(1, "learned", detail, station: "A"), "192.0.2.1");
        Assert.IsFalse(store.TakeLearnedArea("A", out _));
        store.Receive(Ack(2, "learned", detail, platform: "android", station: "A"), "192.0.2.1");
        Assert.IsFalse(store.TakeLearnedArea("A", out _));
        store.Receive(Ack(2, "failed", "samples2", station: "A"), "192.0.2.1");
        Assert.IsFalse(store.TakeLearnedArea("A", out _));
        store.Queue(3, "clear-area=1", 2);
        store.Receive(Ack(3, "learned", detail, station: "A"), "192.0.2.1");
        Assert.IsFalse(store.TakeLearnedArea("A", out _));
        store.Receive(Ack(2, "learned", detail, station: "A"), "192.0.2.1");
        store.Receive(Ack(2, "learning", "10s", station: "A"), "192.0.2.1");
        Assert.IsTrue(store.TakeLearnedArea("A", out _));
        Assert.IsFalse(store.TakeLearnedArea("A", out _));
    }

    [Test]
    public void ShutterAcknowledgementsValidateAndProduceToastForBothStations()
    {
        foreach (string station in new[] { "A", "B" })
        foreach (string detail in new[] { "off", "500", "1000", "2000" })
        {
            string line = $"PHONESABER_ACK 1 id=91 station={station} platform=ios result=shutter detail={detail}";
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(line, out var ack));
            var store = new PhoneSaberAcknowledgementStore();
            store.Queue(91, "shutter=" + (detail == "off" ? "0" : detail), 0);
            Assert.IsTrue(store.Receive(ack, "127.0.0.1"));
            StringAssert.Contains("iPhone（台" + station + "）: 固定シャッター " + (detail == "off" ? "OFF" : "1/" + detail), PhoneSaberOperatorKeys.ToastText(store, 1));
        }
        foreach (string detail in new[] { "0", "on", "250", "-500", "1/1000" })
            Assert.IsFalse(PhoneSaberAcknowledgement.TryParse("PHONESABER_ACK 1 id=91 station=A platform=ios result=shutter detail=" + detail, out _));
    }

    [Test]
    public void ShutterISOVerifiesBothStationRepliesAndToast()
    {
        foreach (string station in new[] { "A", "B" })
        foreach (int value in new[] { 2000, 250, 200, 160, 125, 100, 80, 64 })
        {
            string line = $"PHONESABER_ACK 1 id=92 station={station} platform=ios result=shutter-iso detail={value}";
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(line, out var ack));
            var store = new PhoneSaberAcknowledgementStore();
            store.Queue(92, "shutter-iso=" + value, 0);
            Assert.IsTrue(store.Receive(ack, "127.0.0.1"));
            StringAssert.Contains("iPhone（台" + station + "）: 固定シャッター " + PhoneSaberOperatorKeys.ShutterISOLabel(value), PhoneSaberOperatorKeys.ToastText(store, 1));
            StringAssert.Contains("ISO " + value, PhoneSaberOperatorKeys.PendingLabel("shutter-iso=" + value));
        }
        // 実適用ISOは形式の下限により選択肢外・小数になる。
        foreach (string station in new[] { "A", "B" })
        foreach (string value in new[] { "80.5", "300", "500", "1000", "2000.0" })
        {
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse($"PHONESABER_ACK 1 id=92 station={station} platform=ios result=shutter-iso detail={value}", out var ack));
            var store = new PhoneSaberAcknowledgementStore();
            store.Queue(92, "shutter-iso=64", 0);
            Assert.IsTrue(store.Receive(ack, "127.0.0.1"));
            StringAssert.Contains("固定シャッター ISO " + value, PhoneSaberOperatorKeys.ToastText(store, 1));
        }
        foreach (string value in new[] { "0", "0.0", "off", "-250", "NaN", "Infinity", "80..5", "8e1", "80,5" })
            Assert.IsFalse(PhoneSaberAcknowledgement.TryParse("PHONESABER_ACK 1 id=92 station=A platform=ios result=shutter-iso detail=" + value, out _));
        foreach (string value in new[] { "on", "off" })
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse("PHONESABER_ACK 1 id=93 station=B platform=ios result=shutter-emitter detail=" + value, out _));
    }

    [Test]
    public void SharedPhoneExamplesParseAndUnknownFieldsAreIgnored()
    {
        foreach (string line in new[] {
            "PHONESABER_ACK 1 id=7 station=A platform=ios result=ignored detail=red1-blue1",
            "PHONESABER_ACK 1 id=7 station=B platform=ios result=ignored detail=red3-blue2",
            "PHONESABER_ACK 1 id=7 station=A platform=ios result=learning detail=3s",
            "PHONESABER_ACK 1 id=8 station=none platform=android result=nothing detail=red0-blue0",
            "PHONESABER_ACK 1 id=9 station=B platform=ios result=cleared detail=ignore",
            "PHONESABER_ACK 1 id=10 station=A platform=android result=learning detail=10s",
            "PHONESABER_ACK 1 id=10 station=A platform=android result=learned detail=samples42",
            "PHONESABER_ACK 1 id=11 station=A platform=ios result=failed detail=samples2",
            "PHONESABER_ACK 1 id=12 station=B platform=android result=cleared detail=area",
            "PHONESABER_ACK 1 id=13 station=A platform=ios result=exposure detail=ev-2",
            "PHONESABER_ACK 1 id=14 station=B platform=android result=tracklock detail=off",
            "PHONESABER_ACK 1 id=15 station=A platform=ios result=clip-saved detail=saved",
            "PHONESABER_ACK 1 id=16 station=A platform=android result=clip-off detail=off",
            "PHONESABER_ACK 1 id=17 station=B platform=ios result=autoclip detail=on",
            "PHONESABER_ACK 1 id=81 station=none platform=ios result=defaults detail=tracklock-on-ev-2",
            "PHONESABER_ACK 1 id=81 station=A platform=ios result=defaults detail=tracklock-on-ev-2-shutter-2000",
            "PHONESABER_ACK 1 id=81 station=B platform=ios result=defaults detail=tracklock-on-ev-2-shutter-2000",
        })
        {
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(line + " future=value future=next", out var parsed), line);
            var bytes = Encoding.ASCII.GetBytes(line);
            Assert.IsTrue(PhoneSaberAcknowledgement.TryParse(bytes, bytes.Length, out var binary));
            Assert.AreEqual(parsed.Detail, binary.Detail);
        }
    }

    [Test]
    public void MalformedPacketsAreIgnored()
    {
        const string good = "PHONESABER_ACK 1 id=7 station=A platform=ios result=ignored detail=red1-blue1";
        foreach (string line in new[] {
            "", good.Replace("ACK 1", "ACK 2"), good.Replace("id=7 ", ""), good.Replace("id=7", "id=x"),
            good.Replace("id=7", "id=-1"), good.Replace("id=7", "id=9999999999"), good.Replace("station=A", "station="),
            good.Replace("station=A", "station=台"), good.Replace("platform=ios", "platform=other"),
            good.Replace("result=ignored", "result=unknown"), good.Replace("detail=red1-blue1", "detail=bad"),
            good.Replace("detail=red1-blue1", "detail=red0-blue0"),
            good.Replace("detail=red1-blue1", "detail=red-1-blue1"),
            good.Replace("detail=red1-blue1", "detail=red2147483648-blue1"),
            good + " id=8", good + "\n", good + " bad", good + " future=bad=value", good + new string('x', 200)
        }) Assert.IsFalse(PhoneSaberAcknowledgement.TryParse(line, out _), line);
        Assert.IsFalse(PhoneSaberAcknowledgement.TryParse(null, 10, out _));
        Assert.IsFalse(PhoneSaberAcknowledgement.TryParse(new byte[] { 255 }, 1, out _));
        Assert.IsFalse(PhoneSaberAcknowledgement.TryParse(new byte[1], 20, out _));
    }

    [Test]
    public void StoreKeepsEachPhoneAndCompletesLearningWithoutRegressingOnRetries()
    {
        var store = new PhoneSaberAcknowledgementStore();
        Assert.IsFalse(store.Receive(Ack(7, "learning", "10s"), "10.0.0.1"));
        store.Queue(7, "learn-area=10", 20);
        Assert.IsTrue(store.Receive(Ack(7, "learning", "10s"), "10.0.0.1"));
        Assert.IsTrue(store.Receive(Ack(7, "learning", "10s", "android", "B"), "10.0.0.2"));
        Assert.IsTrue(store.Receive(Ack(7, "learned", "samples42"), "10.0.0.1"));
        store.Receive(Ack(7, "learning", "10s"), "10.0.0.1");
        Assert.AreEqual(2, store.Read(7).Length);
        Assert.AreEqual("learned", store.Read(7)[0].Result);
        string text = PhoneSaberOperatorKeys.StatusText(store, 40);
        StringAssert.Contains("iPhone: プレイ範囲を覚えました（42回分）", text);
        StringAssert.Contains("Android（台B）: プレイ範囲を覚えています（10秒、範囲いっぱいに振る）", text);
        for (int i = 8; i < 40; i++) store.Queue(i, "ignore=both", 40);
        Assert.IsFalse(store.Receive(Ack(7, "failed", "samples0"), "10.0.0.1"));
        Assert.IsEmpty(store.Read(7));
    }

    [Test]
    public void StoreIsBoundedAndCanBeReadWhileReceiving()
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(1, "ignore=both", 0);
        Parallel.For(0, 100, i => { store.Receive(Ack(1, "nothing", "red0-blue0"), "phone" + i); store.Read(1); });
        Assert.AreEqual(8, store.Read(1).Length);
    }

    [Test]
    public void AutomaticDefaultsNeverToastButRemainVisibleInStatus()
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(81, PhoneSaberDiscoveryResponder.DefaultsCommand, 10, automatic: true);
        Assert.IsEmpty(PhoneSaberOperatorKeys.ToastText(store, 11));
        Assert.IsEmpty(PhoneSaberOperatorKeys.ToastText(store, 100));
        store.Receive(Ack(81, "defaults", "tracklock-on-ev-2-shutter-2000"), "10.0.0.1");
        Assert.IsEmpty(PhoneSaberOperatorKeys.ToastText(store, 101));
        Assert.AreEqual("iPhone: 既定に戻しました（剣を追いかける ON・露出 −2・固定シャッター 1/2000 秒・ISO 2000・色付き発光体 OFF）",
            PhoneSaberOperatorKeys.StatusText(store, 101));
        store.Queue(82, PhoneSaberDiscoveryResponder.DefaultsCommand, 102);
        store.Receive(Ack(82, "defaults", "tracklock-on-ev-2"), "10.0.0.1");
        Assert.AreEqual(PhoneSaberOperatorKeys.StatusText(store, 103), PhoneSaberOperatorKeys.ToastText(store, 103));
    }

    [Test]
    public void ToastRefreshOnlyRecomputesOnRevisionOrTimeout()
    {
        var store = new PhoneSaberAcknowledgementStore();
        int initial = store.Revision;
        store.Queue(81, "ignore=red", 10);
        Assert.AreNotEqual(initial, store.Revision);
        int queued = store.Revision;
        Assert.IsTrue(PhoneSaberOperatorKeys.ShouldRefreshToast(queued, initial, 11, double.PositiveInfinity));
        Assert.IsFalse(PhoneSaberOperatorKeys.ShouldRefreshToast(queued, queued, 11, 13));
        Assert.IsFalse(PhoneSaberOperatorKeys.ShouldRefreshToast(queued, queued, 12.99, 13));
        Assert.IsTrue(PhoneSaberOperatorKeys.ShouldRefreshToast(queued, queued, 13, 13));
        Assert.IsFalse(PhoneSaberOperatorKeys.ShouldRefreshToast(queued, queued, 1000, double.PositiveInfinity));
        store.Receive(Ack(81, "nothing", "red0-blue0"), "10.0.0.1");
        Assert.AreNotEqual(queued, store.Revision);
        Assert.IsTrue(PhoneSaberOperatorKeys.ShouldRefreshToast(store.Revision, queued, 1000, double.PositiveInfinity));
        Assert.IsFalse(PhoneSaberOperatorKeys.ShouldRefreshToast(store.Revision, store.Revision, 1000, double.PositiveInfinity));
    }

    [Test]
    public void PendingTimeoutPersistsUntilNextCommandAndLateRepliesReplaceIt()
    {
        var store = new PhoneSaberAcknowledgementStore();
        Assert.AreEqual("", PhoneSaberOperatorKeys.StatusText(store, 0));
        store.Queue(7, "ignore=both", 10);
        Assert.AreEqual("スマホ: 今の赤と青をまとめて無視（送信中）", PhoneSaberOperatorKeys.StatusText(store, 12.99));
        const string timeout = "スマホから返事がありません（送信中か・同じネットワークか確認）";
        Assert.AreEqual(timeout, PhoneSaberOperatorKeys.StatusText(store, 13));
        Assert.AreEqual(timeout, PhoneSaberOperatorKeys.StatusText(store, 999));
        store.Receive(Ack(7, "nothing", "red0-blue0"), "10.0.0.1");
        Assert.AreEqual("iPhone: 今は赤も青も映っていないので無視するものがありません", PhoneSaberOperatorKeys.StatusText(store, 999));
        store.Queue(8, "learn-area=10", 1000);
        StringAssert.EndsWith("（送信中）", PhoneSaberOperatorKeys.StatusText(store, 1001));
        store.Receive(Ack(7, "ignored", "red1-blue1"), "10.0.0.1");
        StringAssert.EndsWith("（送信中）", PhoneSaberOperatorKeys.StatusText(store, 1001));
    }

    [TestCase("ignored", "red1-blue1", "赤と青を無視しました（赤1・青1）")]
    [TestCase("ignored", "red1-blue0", "赤を無視しました（赤1・青0）")]
    [TestCase("ignored", "red0-blue1", "青を無視しました（赤0・青1）")]
    [TestCase("cleared", "ignore", "無視をすべて消しました")]
    [TestCase("cleared", "area", "プレイ範囲を消しました")]
    [TestCase("learning", "3s", "無視する物を覚えています（剣を消して3秒待つ）")]
    [TestCase("ignored", "red3-blue2", "赤と青を無視しました（赤3・青2）")]
    [TestCase("ignored", "red0-blue3", "青を無視しました（赤0・青3）")]
    [TestCase("learned", "samples42", "プレイ範囲を覚えました（42回分）")]
    [TestCase("failed", "samples2", "覚えられませんでした（振りが足りません）")]
    [TestCase("exposure", "ev-2", "露出を -2 EV にしました")]
    [TestCase("tracklock", "off", "剣を追いかける OFF")]
    [TestCase("defaults", "tracklock-on-ev-2", "既定に戻しました（剣を追いかける ON・露出 −2）")]
    [TestCase("defaults", "tracklock-on-ev-2-shutter-2000", "既定に戻しました（剣を追いかける ON・露出 −2・固定シャッター 1/2000 秒・ISO 2000・色付き発光体 OFF）")]
    [TestCase("autoclip", "on", "自動クリップ ON")]
    [TestCase("clip-saved", "saved", "今の場面を保存しました")]
    [TestCase("clip-off", "off", "今の場面を保存できませんでした（クリップOFF・停止中・保存不可）")]
    public void JapaneseResultStringsArePure(string result, string detail, string expected)
    {
        Assert.AreEqual("iPhone: " + expected, PhoneSaberOperatorKeys.ResultText(Ack(7, result, detail), "ignore=both"));
    }

    [TestCase("ignore=red", "赤が")]
    [TestCase("ignore=blue", "青が")]
    public void NothingUsesTheRequestedColour(string command, string colour)
    {
        Assert.AreEqual("iPhone: 今は" + colour + "映っていないので無視するものがありません",
            PhoneSaberOperatorKeys.ResultText(Ack(7, "nothing", "red0-blue0"), command));
    }

    [TestCase("A", "ignored", "red3-blue2")]
    [TestCase("B", "nothing", "red0-blue0")]
    public void IgnoreLearningToastAndF8KeepFinalResultAfterLateStart(string station, string result, string detail)
    {
        var store = new PhoneSaberAcknowledgementStore();
        store.Queue(7, "ignore=both", 0);
        store.Receive(Ack(7, "learning", "3s", station: station), "phone");
        string expected = "iPhone（台" + station + "）: 無視する物を覚えています（剣を消して3秒待つ）";
        Assert.AreEqual(expected, PhoneSaberOperatorKeys.ToastText(store, 1));
        Assert.AreEqual(expected, PhoneSaberOperatorKeys.StatusText(store, 1));
        store.Receive(Ack(7, result, detail, station: station), "phone");
        store.Receive(Ack(7, "learning", "3s", station: station), "phone");
        Assert.AreEqual(result, store.Read(7)[0].Result);
        StringAssert.DoesNotContain("覚えています", PhoneSaberOperatorKeys.StatusText(store, 4));
        StringAssert.Contains("プレイ範囲を覚えています（10秒、範囲いっぱいに振る）",
            PhoneSaberOperatorKeys.ResultText(Ack(8, "learning", "10s"), "learn-area=10"));
    }
}
