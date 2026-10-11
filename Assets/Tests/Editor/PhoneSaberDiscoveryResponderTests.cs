using System;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 5007 を占有せず、bind 失敗後の socket 解放を確認する。
public class PhoneSaberDiscoveryResponderTests
{
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
