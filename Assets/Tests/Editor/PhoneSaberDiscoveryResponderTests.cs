using System;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 5007 を占有せず、bind 失敗後の socket 解放を確認する。
public class PhoneSaberDiscoveryResponderTests
{
    [Test]
    public void RetiredLoopExitsWhileReplacementIsRunning()
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        using (var responder = new PhoneSaberDiscoveryResponder())
        using (var retired = new UdpClient(0))
        using (var phone = new UdpClient(0))
        {
            Thread oldThread = null;
            try
            {
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
                UdpClient replacement = null;
                Assert.IsTrue(responder.Start(5005, 5006,
                    () => replacement = new UdpClient(AddressFamily.InterNetwork), "A", 0));
                retired.Close();
                // Stop の Join が時間切れになり、旧 Loop の再開前に Start が running を true に戻した状態。
                var loop = typeof(PhoneSaberDiscoveryResponder).GetMethod("Loop", BindingFlags.NonPublic | BindingFlags.Instance);
                byte[] reply = Encoding.ASCII.GetBytes("replacement");
                oldThread = new Thread(() => loop.Invoke(responder, new object[] { retired, reply })) { IsBackground = true };
                oldThread.Start();
                Assert.IsTrue(oldThread.Join(1000), "閉じた旧 socket の例外を繰り返して CPU を占有しない");
                Assert.IsTrue(responder.IsRunning, "旧 Loop の終了で新しい受信機を止めない");
                phone.Client.ReceiveTimeout = 2000;
                byte[] request = Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.Request);
                phone.Send(request, request.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,
                    ((System.Net.IPEndPoint)replacement.Client.LocalEndPoint).Port));
                var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                Assert.AreEqual(PhoneSaberDiscoveryResponder.BuildReply(5005, 5006, Environment.MachineName, "A"),
                    Encoding.ASCII.GetString(phone.Receive(ref remote)));
            }
            finally
            {
                responder.Stop();
                oldThread?.Join(2000);
                PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart;
            }
        }
    }

    // OS の socket を開かず、初期化途中の失敗と Dispose の呼び出しを確認する。
    sealed class TrackingClient : UdpClient
    {
        public bool Closed;
        protected override void Dispose(bool disposing) { Closed = true; }
    }

    [Test]
    public void FailedStartupClosesAcquiredClient()
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        // コンストラクタを通さず Client=null とし、socket 設定の段階で失敗させる。
        var client = (TrackingClient)FormatterServices.GetUninitializedObject(typeof(TrackingClient));
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            using (var responder = new PhoneSaberDiscoveryResponder())
            {
                LogAssert.Expect(LogType.Warning, new Regex("Android discovery responder failed:"));
                Assert.IsFalse(responder.Start(5005, 5006, () => client, "A"));
                Assert.IsFalse(responder.IsRunning);
                Assert.IsTrue(client.Closed, "初期化途中で失敗しても、取得済み client を解放する");
            }
        }
        finally
        {
            client.Close();
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart;
        }
    }

    [Test]
    public void FailedBindClosesSocketOnEveryAttempt()
    {
        bool savedAutoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        try
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            using (var responder = new PhoneSaberDiscoveryResponder())
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    using (var client = new UdpClient(0))
                    {
                        Socket socket = client.Client;
                        LogAssert.Expect(LogType.Warning, new Regex("Android discovery responder failed:"));
                        Assert.IsFalse(responder.Start(5005, 5006, () => client, "A"));
                        Assert.IsFalse(responder.IsRunning);
                        Assert.Throws<ObjectDisposedException>(() => socket.Poll(0, SelectMode.SelectRead),
                            "bind に失敗した socket も、その場で閉じて次の起動へ残さない");
                    }
                }
            }
        }
        finally { PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedAutoStart; }
    }
}
