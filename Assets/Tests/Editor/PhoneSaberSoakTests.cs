using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;

public class PhoneSaberSoakTests
{
    [Test]
    public void ExplicitPlayerModeKeepsSmokePriorityAndRejectsNonFiniteNumbers()
    {
        Assert.AreEqual(0, PhoneSaberSoakModel.Minutes(new[] { "player" }));
        Assert.AreEqual(0, PhoneSaberSoakModel.Minutes(new[] { "player", "-phonesaberSoak", "5", "-phonesaberSmoke" }));
        Assert.AreEqual(2.5, PhoneSaberSoakModel.Minutes(new[] { "player", "-phonesaberSoak", "2.5" }));
        foreach (string value in new[] { "NaN", "Infinity", "-1", "0", "1441", "broken" })
            Assert.AreEqual(60, PhoneSaberSoakModel.Minutes(new[] { "player", "-phonesaberSoak", value }));
        Assert.AreEqual(40, PhoneSaberSoakModel.GameSeconds(new[] { "player" }));
        Assert.AreEqual(8, PhoneSaberSoakModel.GameSeconds(new[] { "player", "-phonesaberSoakGameSeconds", "8" }));
        Assert.AreEqual(40, PhoneSaberSoakModel.GameSeconds(new[] { "player", "-phonesaberSoakGameSeconds", "Infinity" }));
    }

    [Test]
    public void WaveformHasPauseOneColourDropoutSilenceAndRecovery()
    {
        Assert.AreEqual(PhoneSaberSoakModel.Payload(5.1, 40, false), PhoneSaberSoakModel.Payload(6.9, 40, false));
        Assert.IsNotNull(PhoneSaberSoakModel.Payload(10, 40, false));
        Assert.IsNull(PhoneSaberSoakModel.Payload(10, 40, true));
        foreach (bool blue in new[] { false, true })
        {
            Assert.IsNull(PhoneSaberSoakModel.Payload(14, 40, blue));
            Assert.IsNotNull(PhoneSaberSoakModel.Payload(17, 40, blue));
            Assert.AreNotEqual(PhoneSaberSoakModel.Payload(0, 40, blue), PhoneSaberSoakModel.Payload(.5, 40, blue));
            Assert.IsNull(PhoneSaberSoakModel.Payload(5.6, 8, blue));
        }
    }

    [Test]
    public void UdpFormatIsFourFiniteInBoundsCoordinatesInAnyCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            for (int i = 0; i < 2400; i++)
                foreach (bool blue in new[] { false, true })
                {
                    string payload = PhoneSaberSoakModel.Payload(i / 60.0, 40, blue);
                    if (payload == null) continue;
                    string[] fields = payload.Split(',');
                    Assert.AreEqual(4, fields.Length);
                    for (int j = 0; j < 4; j++)
                    {
                        double value = double.Parse(fields[j], CultureInfo.InvariantCulture);
                        Assert.That(value, Is.InRange(0, j % 2 == 0 ? 1920 : 1080));
                    }
                }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public void LateSenderNeverBurstsOldPackets()
    {
        Assert.AreEqual(1.0 / 60, PhoneSaberSoakModel.NextPacket(0, .001), 1e-9);
        Assert.AreEqual(.1 + 1.0 / 60, PhoneSaberSoakModel.NextPacket(0, .1), 1e-9);
    }

    [Test]
    public void GrowthIgnoresWarmupAndNeedsEnoughSamples()
    {
        Assert.AreEqual("insufficient", PhoneSaberSoakModel.Growth(Enumerable.Range(0, 10).Select(i => (double)i * 100).ToArray(), 10));
        Assert.AreEqual("stable", PhoneSaberSoakModel.Growth(new double[] { 0, 1000, 2000, 3000, 3000, 3000, 3000, 3000, 3000, 3000, 3000 }, 10));
        Assert.AreEqual("growth", PhoneSaberSoakModel.Growth(Enumerable.Range(0, 11).Select(i => (double)i * 100).ToArray(), 10));
    }

    [Test]
    public void GrowthRejectsNoiseCacheStepDeclineAndSmallRelativeGrowth()
    {
        foreach (double[] values in new[] {
            new double[] { 0, 0, 0, 100, 105, 98, 103, 99, 106, 101, 102 },
            new double[] { 0, 0, 0, 100, 100, 100, 100, 1000, 1000, 1000, 1000 },
            Enumerable.Range(0, 11).Select(i => 2000.0 - i * 100).ToArray(),
            Enumerable.Range(0, 11).Select(i => 10000.0 + i).ToArray() })
            Assert.AreEqual("stable", PhoneSaberSoakModel.Growth(values, 10));
        Assert.AreEqual("invalid", PhoneSaberSoakModel.Growth(new double[] { 0, 0, 0, 1, 2, 3, 4, double.NaN, 6, 7, 8 }, 10));
    }

    [Test]
    public void ThreadedErrorsAreCountedDeduplicatedTruncatedAndDrained()
    {
        var errors = new PhoneSaberSoakErrors();
        System.Threading.Tasks.Parallel.For(0, 1000, i => errors.Add("same exception\nstack", true));
        errors.Add(new string('x', 500), false);
        string first = errors.Drain();
        Assert.That(first, Does.Contain("exceptions=1000 errors=1"));
        Assert.AreEqual(1, first.Split(new[] { "same exception" }, StringSplitOptions.None).Length - 1);
        Assert.That(first, Does.Not.Contain("stack"));
        Assert.That(first.Length, Is.LessThan(250));
        Assert.AreEqual("exceptions=0 errors=0 messages=\"\" messages-omitted=0", errors.Drain());
        Assert.AreEqual(1000, errors.Exceptions); Assert.AreEqual(1, errors.Errors);
        for (int i = 0; i < 100; i++) errors.Add(i + new string('x', 120), true);
        string capped = errors.Drain();
        Assert.That(capped.Length, Is.LessThan(1100));
        Assert.That(capped, Does.Contain("messages-omitted=92"));
    }

    [Test]
    public void SenderRunsIndependentlyOfFramesAndDisposesWithoutFurtherPackets()
    {
        // Unityの描画を進めず、OSのloopback UDPを実際に受信する。
        using (var red = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var blue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            Exception error = null;
            var sender = new PhoneSaberSoakSender(((IPEndPoint)red.Client.LocalEndPoint).Port,
                ((IPEndPoint)blue.Client.LocalEndPoint).Port, 40, e => error = e);
            System.Threading.Thread.Sleep(550);
            sender.Dispose();
            var from = new IPEndPoint(IPAddress.Any, 0);
            int r = 0, b = 0;
            while (red.Available > 0) { Assert.AreEqual(4, Encoding.ASCII.GetString(red.Receive(ref from)).Split(',').Length); r++; }
            while (blue.Available > 0) { blue.Receive(ref from); b++; }
            Assert.IsNull(error); Assert.That(r, Is.InRange(20, 50)); Assert.AreEqual(r, b);
            System.Threading.Thread.Sleep(100);
            Assert.AreEqual(0, red.Available); Assert.AreEqual(0, blue.Available);
        }
    }
}
