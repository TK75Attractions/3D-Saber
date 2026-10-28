using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

// スマホから印(ChartMarkPhoneServer・ChartMarkPhonePage、2026-10-28)。合言葉・要求の解析・大きさと回数の上限・キュー・
// アドレスの選び方と、ループバックでの受け答え(待ち受けは全部の IPv4、テストは 127.0.0.1 から呼ぶ)。
public class ChartMarkPhoneTests
{
    const string Token = "0123456789abcdef0123456789abcdef";
    const string Host = "Host: 192.168.137.1:5020\r\n";

    static byte[] Request(string method, string target, string headers = Host, string body = null, string type = "application/json")
    {
        byte[] payload = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
        string head = method + " " + target + " HTTP/1.1\r\n" + headers;
        if (body != null) head += "Content-Type: " + type + "\r\nContent-Length: " + payload.Length + "\r\n";
        head += "\r\n";
        return Concat(Encoding.ASCII.GetBytes(head), payload);
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    static int Parse(byte[] bytes) => ChartMarkPhoneServer.ParseRequest(bytes, 5020, Token, out _);

    static ChartMarkPhoneRequest Parsed(byte[] bytes)
    {
        Assert.AreEqual(200, ChartMarkPhoneServer.ParseRequest(bytes, 5020, Token, out var request));
        return request;
    }

    [Test]
    public void TokenIsRandomHexAndOnlyThatShapeIsAccepted()
    {
        string a = ChartMarkPhoneServer.NewToken(), b = ChartMarkPhoneServer.NewToken();
        Assert.That(a, Does.Match("^[0-9a-f]{32}$"));
        Assert.AreNotEqual(a, b);
        Assert.IsTrue(ChartMarkPhoneServer.IsValidToken(a));
        foreach (string bad in new[] { null, "", "0123", Token.ToUpperInvariant(), Token + "0", Token.Substring(1) + "g" })
            Assert.IsFalse(ChartMarkPhoneServer.IsValidToken(bad), bad);
        Assert.Throws<ArgumentException>(() => new ChartMarkPhoneServer("short"));
    }

    [Test]
    public void ParsesThePageStateAndMarkRequests()
    {
        Assert.AreEqual(ChartMarkPhoneAction.Page, Parsed(Request("GET", "/m?t=" + Token)).Action);
        Assert.AreEqual(ChartMarkPhoneAction.Page, Parsed(Request("GET", "/m?x=1&t=" + Token)).Action, "合言葉の位置は問わない");
        Assert.AreEqual(ChartMarkPhoneAction.State, Parsed(Request("GET", "/api/state?t=" + Token)).Action);
        var mark = Parsed(Request("POST", "/api/mark?t=" + Token));
        Assert.AreEqual(ChartMarkPhoneAction.Mark, mark.Action);
        Assert.AreEqual("POST", mark.Method);
        Assert.AreEqual("/api/mark", mark.Path);
        Assert.AreEqual(200, Parse(Request("POST", "/api/mark?t=" + Token, Host + "Content-Length: 0\r\n")), "fetch の空の POST");
        Assert.AreEqual(200, Parse(Request("POST", "/api/mark?t=" + Token, body: "{}")));
        Assert.AreEqual(200, Parse(Request("GET", "/m?t=" + Token, "Host: localhost:5020\r\n")));
        Assert.AreEqual(200, Parse(Request("GET", "/m?t=" + Token, "Host: 10.0.0.5:5020\r\nUser-Agent: Mozilla/5.0 (iPhone)\r\nAccept: */*\r\n")));
    }

    [Test]
    public void ParsesCommentsWithJapaneseAndEscapes()
    {
        var comment = Parsed(Request("POST", "/api/comment?t=" + Token,
            body: "{\"id\":\"20261028-103012_B_ManicSonic_R_1a2b3c4d-3\",\"text\":\"半拍おそく \\u2192 \\\"ここ\\\"\\n🎵\\ud83c\\udfb5\"}"));
        Assert.AreEqual(ChartMarkPhoneAction.Comment, comment.Action);
        Assert.AreEqual("20261028-103012_B_ManicSonic_R_1a2b3c4d-3", comment.Id);
        Assert.AreEqual("半拍おそく → \"ここ\"\n🎵🎵", comment.Text);
        var japaneseId = Parsed(Request("POST", "/api/comment?t=" + Token,
            body: "{\"id\":\"20261028-103012_PC_揺籠_P_1a2b3c4d-1\",\"text\":\"\"}", type: "application/json; charset=UTF-8"));
        Assert.AreEqual("20261028-103012_PC_揺籠_P_1a2b3c4d-1", japaneseId.Id);
        Assert.AreEqual("", japaneseId.Text, "空の一言で消せる");
        var cancel = Parsed(Request("POST", "/api/cancel?t=" + Token, body: "{ \"canceled\" : true , \"id\" : \"x-2\" }"));
        Assert.AreEqual(ChartMarkPhoneAction.Cancel, cancel.Action);
        Assert.AreEqual("x-2", cancel.Id);
        Assert.IsTrue(cancel.Canceled);
        Assert.IsFalse(Parsed(Request("POST", "/api/cancel?t=" + Token, body: "{\"id\":\"x-2\",\"canceled\":false}")).Canceled);
        Assert.AreEqual(200, Parse(Request("POST", "/api/comment?t=" + Token,
            body: "{\"id\":\"a-1\",\"text\":\"" + new string('あ', ChartMarkPhoneServer.MaximumTextChars) + "\"}")));
        Assert.AreEqual(413, Parse(Request("POST", "/api/comment?t=" + Token,
            body: "{\"id\":\"a-1\",\"text\":\"" + new string('あ', ChartMarkPhoneServer.MaximumTextChars + 1) + "\"}")));
    }

    [TestCase("/m")]
    [TestCase("/m?t=wrong")]
    [TestCase("/m?t=0123456789ABCDEF0123456789ABCDEF")]
    [TestCase("/api/state")]
    [TestCase("/api/mark?x=1")]
    [TestCase("/favicon.ico")]
    public void EveryPathRequiresTheToken(string target)
    {
        Assert.AreEqual(403, Parse(Request("GET", target)));
        Assert.AreEqual(403, Parse(Request("POST", target)));
    }

    [TestCase("192.168.137.1:5020", 200)]
    [TestCase("10.0.0.5:5020", 200)]
    [TestCase("localhost:5020", 200)]
    [TestCase("LOCALHOST:5020", 200)]
    [TestCase("192.168.137.1:5021", 403)]
    [TestCase("192.168.137.1", 403)]
    [TestCase("attacker.example:5020", 403)]
    [TestCase("192.168.137.1.attacker.example:5020", 403)]
    [TestCase("192.168.137.256:5020", 403)]
    [TestCase("0192.168.1.1:5020", 403)]
    [TestCase("[::1]:5020", 403)]
    [TestCase("", 403)]
    public void AcceptsOnlyAddressHosts(string host, int expected) =>
        Assert.AreEqual(expected, Parse(Request("GET", "/m?t=" + Token, "Host: " + host + "\r\n")));

    [Test]
    public void MissingHostIsRefused() => Assert.AreEqual(403, Parse(Request("GET", "/m?t=" + Token, "")));

    [TestCase("POST", "/m", 405)]
    [TestCase("POST", "/api/state", 405)]
    [TestCase("GET", "/api/mark", 405)]
    [TestCase("GET", "/api/comment", 405)]
    [TestCase("PUT", "/api/cancel", 405)]
    [TestCase("GET", "/", 404)]
    [TestCase("GET", "/favicon.ico", 404)]
    [TestCase("GET", "/api/other", 404)]
    public void EnforcesMethodsAndRoutes(string method, string path, int expected) =>
        Assert.AreEqual(expected, Parse(Request(method, path + "?t=" + Token)));

    [TestCase("http://192.168.137.1:5020", 200)]
    [TestCase("HTTP://192.168.137.1:5020", 200)]
    [TestCase("http://evil.example", 403)]
    [TestCase("http://192.168.137.1:5021", 403)]
    [TestCase("null", 403)]
    public void RefusesWritesFromAnotherSite(string origin, int expected) =>
        Assert.AreEqual(expected, Parse(Request("POST", "/api/mark?t=" + Token, Host + "Origin: " + origin + "\r\n")));

    [TestCase("/api/comment", "{")]
    [TestCase("/api/comment", "[1]")]
    [TestCase("/api/comment", "{\"id\":\"a-1\"}")]
    [TestCase("/api/comment", "{\"text\":\"a\"}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":5}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":null}")]
    [TestCase("/api/comment", "{\"id\":\"\",\"text\":\"a\"}")]
    [TestCase("/api/comment", "{\"id\":\"../x-1\",\"text\":\"a\"}")]
    [TestCase("/api/comment", "{\"id\":\"a/b-1\",\"text\":\"a\"}")]
    [TestCase("/api/comment", "{\"id\":\"a\\\\b-1\",\"text\":\"a\"}")]
    [TestCase("/api/comment", "{\"id\":\"C:x-1\",\"text\":\"a\"}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":\"a\",\"text\":\"b\"}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":{\"x\":1}}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":\"a\"} x")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":\"a\\x\"}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":\"tab\there\"}")]
    [TestCase("/api/comment", "{\"id\":\"a-1\",\"text\":\"\\u12\"}")]
    [TestCase("/api/cancel", "{\"id\":\"a-1\"}")]
    [TestCase("/api/cancel", "{\"id\":\"a-1\",\"canceled\":\"yes\"}")]
    [TestCase("/api/cancel", "{\"id\":\"a-1\",\"canceled\":1}")]
    [TestCase("/api/mark", "{")]
    public void RejectsBadBodies(string path, string body) => Assert.AreEqual(400, Parse(Request("POST", path + "?t=" + Token, body: body)));

    [Test]
    public void BodiesMustBeJsonUtf8WithTheRightLength()
    {
        Assert.AreEqual(400, Parse(Request("POST", "/api/comment?t=" + Token, body: "")), "一言は本文が要る");
        Assert.AreEqual(400, Parse(Request("POST", "/api/cancel?t=" + Token)), "取り消しは本文が要る");
        Assert.AreEqual(415, Parse(Request("POST", "/api/comment?t=" + Token, body: "{\"id\":\"a-1\",\"text\":\"a\"}", type: "text/plain")));
        Assert.AreEqual(415, Parse(Request("POST", "/api/comment?t=" + Token, body: "{\"id\":\"a-1\",\"text\":\"a\"}", type: "application/jsonx")));
        byte[] head = Encoding.ASCII.GetBytes("POST /api/comment?t=" + Token + " HTTP/1.1\r\n" + Host +
                                              "Content-Type: application/json\r\nContent-Length: 3\r\n\r\n");
        Assert.AreEqual(400, Parse(Concat(head, new byte[] { 0x7b, 0xff, 0x7d })), "UTF-8 でない本文");
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("POST /api/mark?t=" + Token + " HTTP/1.1\r\n" + Host +
                                                           "Content-Length: 10\r\n\r\n{}")), "長さが合わない");
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("POST /api/mark?t=" + Token + " HTTP/1.1\r\n" + Host +
                                                           "Content-Type: application/json\r\n\r\n{}")), "長さの無い本文");
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("POST /api/mark?t=" + Token + " HTTP/1.1\r\n" + Host +
                                                           "Content-Length: +2\r\n\r\n{}")));
        Assert.AreEqual(400, Parse(Request("POST", "/api/mark?t=" + Token, Host + "Transfer-Encoding: chunked\r\n")));
        Assert.AreEqual(400, Parse(Request("GET", "/api/state?t=" + Token, body: "{}")), "GET に本文は付けない");
    }

    [Test]
    public void EnforcesSizeAndFramingLimits()
    {
        Assert.AreEqual(413, Parse(new byte[ChartMarkPhoneServer.MaximumRequestBytes + 1]));
        Assert.AreEqual(413, Parse(Request("GET", "/m?t=" + Token + "&padding=" + new string('a', ChartMarkPhoneServer.MaximumTargetBytes))));
        Assert.AreEqual(400, Parse(null));
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("GET /m?t=" + Token + " HTTP/1.1\r\n" + Host)), "ヘッダの終わりが無い");
        Assert.AreEqual(400, Parse(Encoding.ASCII.GetBytes("GET /m?t=" + Token + " HTTP/1.0\r\n" + Host + "\r\n")));
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token, Host + Host)), "同じヘッダが2つ");
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token, Host + " folded\r\n")));
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token, Host + "X-Test: a\nb\r\n")), "裸の LF");
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token, Host + "Bad Name: x\r\n")));
        Assert.AreEqual(400, Parse(Concat(Encoding.ASCII.GetBytes("GET /m?t=" + Token + " HTTP/1.1\r\n" + Host + "X-Name: "),
            new byte[] { 0xe3, 0x81, 0x82, 13, 10, 13, 10 })), "ヘッダは ASCII だけ");
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token + "&t=" + Token)), "合言葉が2つ");
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token + "&x=%GG")));
        Assert.AreEqual(400, Parse(Request("GET", "/m?t=" + Token + "&x=%1")));
        Assert.AreEqual(403, ChartMarkPhoneServer.ParseRequest(Request("GET", "/m?t=" + Token), 5020, "", out _));
    }

    [Test]
    public void FlatJsonReadsOnlyFlatObjects()
    {
        Assert.IsTrue(ChartMarkPhoneServer.TryParseFlatJson("{}", out var empty));
        Assert.AreEqual(0, empty.Count);
        Assert.IsTrue(ChartMarkPhoneServer.TryParseFlatJson(" { \"a\" : \"x\" , \"b\" : true , \"c\" : null , \"d\" : -1.5e2 , \"e\" : \"\\u00e9\\/\" } ", out var fields));
        Assert.AreEqual("x", fields["a"]);
        Assert.AreEqual(true, fields["b"]);
        Assert.IsNull(fields["c"]);
        Assert.AreEqual(-150.0, fields["d"]);
        Assert.AreEqual("é/", fields["e"]);
        foreach (string bad in new[] { "", " ", "{", "}", "{\"a\":01}", "{\"a\":1.}", "{\"a\":-}", "{\"a\":tru}", "{\"a\":[1]}",
                     "{\"a\":1,}", "{,}", "{\"a\" 1}", "{a:1}", "{\"a\":1}{}", "\"a\"", "{\"a\":\"\\u00e\"}" })
            Assert.IsFalse(ChartMarkPhoneServer.TryParseFlatJson(bad, out _), bad);
    }

    [Test]
    public void RateLimiterAllowsTenPerSecondForEachPhone()
    {
        var limiter = new ChartMarkPhoneRateLimiter(10, 1000);
        for (int i = 0; i < 10; i++) Assert.IsTrue(limiter.Allow("192.168.137.20", i * 10));
        Assert.IsFalse(limiter.Allow("192.168.137.20", 100), "1 秒に 11 回目は断る");
        Assert.IsTrue(limiter.Allow("192.168.137.21", 100), "ほかのスマホは別に数える");
        Assert.IsFalse(limiter.Allow("192.168.137.20", 999));
        Assert.IsTrue(limiter.Allow("192.168.137.20", 1000), "いちばん古い要求から 1 秒たてば戻る");
        Assert.IsFalse(limiter.Allow("192.168.137.20", 1001), "断った分は数えないが、窓の中の 10 回は数える");
        Assert.IsTrue(limiter.Allow("192.168.137.20", 1010));
        for (int i = 0; i < 300; i++) Assert.IsTrue(limiter.Allow("10.0.0." + i, 5000 + i), "相手が多くても古い記録を捨てて続ける");
    }

    [Test]
    public void AJobRunsOnceAndAnAbandonedJobIsSkipped()
    {
        var request = new ChartMarkPhoneRequest("POST", "/api/mark", ChartMarkPhoneAction.Mark);
        var job = new ChartMarkPhoneJob(request);
        Assert.IsFalse(job.Wait(1));
        Assert.IsTrue(job.TryBegin());
        Assert.IsFalse(job.TryAbandon(), "実行が始まったら取りやめない");
        Assert.IsFalse(job.TryBegin(), "二度は実行しない");
        job.Complete(200, "{\"ok\":true}");
        Assert.IsTrue(job.Wait(1));
        Assert.IsTrue(job.IsDone);
        Assert.AreEqual(200, job.Status);
        Assert.AreEqual("{\"ok\":true}", job.Json);
        var late = new ChartMarkPhoneJob(request);
        Assert.IsTrue(late.TryAbandon());
        Assert.IsFalse(late.TryBegin(), "待ちきれずに取りやめた印は、あとから付けない");
    }

    [Test]
    public void ResultJsonEscapesTheMessage()
    {
        Assert.AreEqual("{\"ok\":true,\"message\":\"印3 ・ 18小節目\"}", ChartMarkPhoneServer.Result(true, "印3 ・ 18小節目"));
        Assert.AreEqual("{\"ok\":false,\"message\":\"a\\\"b\\nc\"}", ChartMarkPhoneServer.Result(false, "a\"b\nc"));
        Assert.AreEqual("{\"ok\":false,\"message\":\"\"}", ChartMarkPhoneServer.Result(false, null));
    }

    [Test]
    public void PrefersTheHotspotThenTheLanWithARoute()
    {
        var ranked = ChartMarkPhoneServer.RankAddresses(new[]
        {
            new ChartMarkPhoneAddress("169.254.10.2", false),
            new ChartMarkPhoneAddress("172.28.144.1", false),
            new ChartMarkPhoneAddress("192.168.1.23", true),
            new ChartMarkPhoneAddress("100.101.2.3", true, true),
            new ChartMarkPhoneAddress("192.168.137.1", false),
            new ChartMarkPhoneAddress("127.0.0.1", false),
            new ChartMarkPhoneAddress("192.168.1.23", true),
            new ChartMarkPhoneAddress("224.0.0.251", false),
            new ChartMarkPhoneAddress("fe80::1", true),
            new ChartMarkPhoneAddress(null, true),
        });
        CollectionAssert.AreEqual(new[] { "192.168.137.1", "192.168.1.23", "172.28.144.1", "100.101.2.3" }, ranked);
        CollectionAssert.AreEqual(new[] { "192.168.1.23", "192.168.56.1" }, ChartMarkPhoneServer.RankAddresses(new[]
        {
            new ChartMarkPhoneAddress("192.168.56.1", false), new ChartMarkPhoneAddress("192.168.1.23", true)
        }), "ホットスポットが無ければ、既定の経路がある LAN");
        CollectionAssert.AreEqual(new[] { "10.0.0.2", "10.0.0.3" }, ChartMarkPhoneServer.RankAddresses(new[]
        {
            new ChartMarkPhoneAddress("10.0.0.2", true), new ChartMarkPhoneAddress("10.0.0.3", true)
        }), "同じ点なら見つかった順");
        Assert.IsEmpty(ChartMarkPhoneServer.RankAddresses(null));
        Assert.AreEqual("http://192.168.137.1:5020/m?t=" + Token, ChartMarkPhoneServer.PhoneUrl("192.168.137.1", 5020, Token));
        Assert.IsNull(ChartMarkPhoneServer.PhoneUrl(null, 5020, Token));
    }

    [Test]
    public void ListsThisPcsAddressesWithoutThrowing()
    {
        foreach (var address in ChartMarkPhoneServer.LocalAddresses())
            Assert.IsTrue(ChartMarkPhoneServer.IsDottedIPv4(address.Address), address.Address);
    }

    [Test]
    public void PageReadsStateEverySecondAndWritesOnlyText()
    {
        string html = ChartMarkPhonePage.Html;
        StringAssert.Contains("<html lang='ja'>", html);
        StringAssert.Contains("name='viewport'", html);
        foreach (string path in new[] { ChartMarkPhoneServer.StatePath, ChartMarkPhoneServer.MarkPath,
                     ChartMarkPhoneServer.CommentPath, ChartMarkPhoneServer.CancelPath })
            StringAssert.Contains("'" + path + "'", html);
        StringAssert.Contains("pollTimer=setTimeout(poll,failures?2000:1000)", html, "1 秒ごとに状態を読む");
        StringAssert.Contains("state.state==='playing'", html, "印は演奏中だけ");
        StringAssert.Contains("navigator.vibrate", html);
        StringAssert.Contains("later(c,1000)", html, "打ってから 1 秒あけて保存");
        StringAssert.Contains("MAXLEN=" + ChartMarkPhonePage.CommentMaxLength, html);
        StringAssert.Contains("PAST=" + ChartMarkPhonePage.PastMarks, html);
        StringAssert.DoesNotContain("innerHTML", html);
        StringAssert.DoesNotContain("http://", html);
        StringAssert.DoesNotContain("https://", html);
        StringAssert.DoesNotContain(" src=", html);
        StringAssert.Contains("requestId:markId", html, "印には要求 id を付ける");
        StringAssert.Contains("if(!markId)markId=newId()", html, "結果が分かるまでは同じ id で送り直す(押し直しで二つ目を付けない)");
        StringAssert.Contains("if(c.area.value!==text)c.area.value=text", html, "打っていない欄は、選んでいるだけでも最新の一言にそろえる");
        StringAssert.Contains("crypto.getRandomValues", html, "http でも使える乱数(randomUUID は https だけ)");
        StringAssert.Contains("if(e.status!==0&&e.status!==503)throw e;d=await request('/api/mark',body,true);", html,
            "返事が来ない・503 のときだけ、同じ id で 1 回送り直す");
        StringAssert.Contains("if(d.pending)", html, "実行が始まってからの時間切れは「失敗」と出さない");
        StringAssert.Contains("s.open==null?'—'", html, "演奏中に数をまだ読んでいなければ —");
    }

    [TestCase("127.0.0.1", true)]
    [TestCase("127.20.30.40", true)]
    [TestCase("10.1.2.3", true)]
    [TestCase("172.16.0.1", true)]
    [TestCase("172.31.255.254", true)]
    [TestCase("192.168.137.20", true)]
    [TestCase("169.254.10.20", true)]
    [TestCase("::ffff:192.168.1.5", true)]
    [TestCase("172.15.255.255", false)]
    [TestCase("172.32.0.1", false)]
    [TestCase("192.169.0.1", false)]
    [TestCase("100.101.2.3", false)]
    [TestCase("8.8.8.8", false)]
    [TestCase("::ffff:8.8.8.8", false)]
    [TestCase("::1", false)]
    public void OnlyLanAddressesMayConnect(string address, bool expected) =>
        Assert.AreEqual(expected, ChartMarkPhoneServer.IsLanAddress(IPAddress.Parse(address)));

    [Test]
    public void ListensOnEveryIPv4ByDefaultAndOnLoopbackWhenTold()
    {
        Assert.IsFalse(ChartMarkPhoneServer.IsLanAddress(null));
        using (var server = new ChartMarkPhoneServer(Token))
        {
            Assert.AreEqual(IPAddress.Any, server.ListenAddress, "本番は全部の IPv4 で待ち受ける");
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            Assert.AreEqual(IPAddress.Loopback, server.ListenAddress, "テストはループバック(ファイアウォールに問い合わせない)");
        }
    }

    [Test]
    public void MarkRequestIdIsReadAndChecked()
    {
        Assert.AreEqual("a1B2-c3_D4e5f6", Parsed(Request("POST", "/api/mark?t=" + Token, body: "{\"requestId\":\"a1B2-c3_D4e5f6\"}")).RequestId);
        Assert.IsNull(Parsed(Request("POST", "/api/mark?t=" + Token)).RequestId, "無くてもよい");
        foreach (string bad in new[] { "\"short\"", "\"" + new string('a', 65) + "\"", "\"a b c d e f\"", "\"あいうえおかきく\"", "12345678", "null", "true" })
            Assert.AreEqual(400, Parse(Request("POST", "/api/mark?t=" + Token, body: "{\"requestId\":" + bad + "}")), bad);
        Assert.IsNull(Parsed(Request("POST", "/api/comment?t=" + Token, body: "{\"id\":\"a-1\",\"text\":\"x\",\"requestId\":\"zzzzzzzz\"}")).RequestId,
            "印のほかでは使わない");
    }

    [Test]
    public void RepliesAreRememberedForTheLast32Marks()
    {
        var replies = new ChartMarkPhoneReplies();
        Assert.IsFalse(replies.TryGet(null, out _));
        replies.Remember(null, "x");
        Assert.AreEqual(0, replies.Count, "要求 id の無い印は覚えない");
        for (int i = 0; i < ChartMarkPhoneReplies.Capacity + 1; i++) replies.Remember("request-" + i, "reply-" + i);
        Assert.AreEqual(ChartMarkPhoneReplies.Capacity, replies.Count);
        Assert.IsFalse(replies.TryGet("request-0", out _), "いちばん古いものから忘れる");
        Assert.IsTrue(replies.TryGet("request-1", out string reply));
        Assert.AreEqual("reply-1", reply);
        replies.Remember("request-1", "other");
        Assert.IsTrue(replies.TryGet("request-1", out reply));
        Assert.AreEqual("reply-1", reply, "最初の返事のまま");
    }

    // ---- ループバック(本物のソケット。ポートは空いているものを使い、終わりに止める) ----

    static string Exchange(int port, string method, string target, string body = null, string headers = null)
    {
        using (var client = new TcpClient())
        {
            client.ReceiveTimeout = 8000;
            client.Connect(IPAddress.Loopback, port);
            using (var stream = client.GetStream())
            {
                byte[] bytes = Request(method, target, (headers ?? "") + "Host: 127.0.0.1:" + port + "\r\n", body);
                stream.Write(bytes, 0, bytes.Length);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
    }

    static ChartMarkPhoneJob WaitForJob(ChartMarkPhoneServer server)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 5000)
        {
            if (server.TryDequeue(out var job)) return job;
            Thread.Sleep(5);
        }
        Assert.Fail("要求がキューに入らない");
        return null;
    }

    [Test]
    public void LoopbackServesThePageAndAnswersJobsFromTheGameThread()
    {
        using (var server = new ChartMarkPhoneServer(Token))
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            Assert.That(server.Port, Is.GreaterThan(0));
            string page = Exchange(server.Port, "GET", "/m?t=" + Token);
            StringAssert.StartsWith("HTTP/1.1 200", page);
            StringAssert.Contains("Content-Type: text/html; charset=utf-8", page);
            StringAssert.Contains("Connection: close", page);
            StringAssert.Contains("Content-Security-Policy: default-src 'none'", page);
            StringAssert.Contains("<title>譜面の印</title>", page);
            StringAssert.StartsWith("HTTP/1.1 403", Exchange(server.Port, "GET", "/m?t=wrong"));
            string refused = Exchange(server.Port, "GET", "/api/state");
            StringAssert.StartsWith("HTTP/1.1 403", refused);
            StringAssert.Contains("application/json", refused);
            StringAssert.Contains("\"ok\":false", refused);

            var call = Task.Run(() => Exchange(server.Port, "POST", "/api/comment?t=" + Token, "{\"id\":\"a-1\",\"text\":\"半拍おそく\"}"));
            var job = WaitForJob(server);
            Assert.AreEqual(ChartMarkPhoneAction.Comment, job.Request.Action);
            Assert.AreEqual("a-1", job.Request.Id);
            Assert.AreEqual("半拍おそく", job.Request.Text);
            Assert.IsTrue(job.TryBegin());
            job.Complete(200, ChartMarkPhoneServer.Result(true, "保存しました"));
            Assert.IsTrue(call.Wait(5000));
            StringAssert.StartsWith("HTTP/1.1 200", call.Result);
            StringAssert.Contains("Content-Type: application/json; charset=utf-8", call.Result);
            StringAssert.EndsWith("{\"ok\":true,\"message\":\"保存しました\"}", call.Result);
            Assert.IsFalse(server.TryDequeue(out _));
        }
    }

    [Test]
    public void SilentConnectionsDoNotHoldUpOtherRequests()
    {
        using (var server = new ChartMarkPhoneServer(Token))
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            using (var idle = new TcpClient())
            {
                // スマホのブラウザが先回りで開いて、何も送らない接続。
                idle.Connect(IPAddress.Loopback, server.Port);
                var timer = Stopwatch.StartNew();
                StringAssert.StartsWith("HTTP/1.1 200", Exchange(server.Port, "GET", "/m?t=" + Token));
                Assert.Less(timer.ElapsedMilliseconds, 2000, "黙った接続を待たずに返す");
            }
        }
    }

    [Test]
    public void AnUnansweredJobTimesOutAndIsNotRunLater()
    {
        using (var server = new ChartMarkPhoneServer(Token) { ReplyWaitMilliseconds = 200 })
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            var timer = Stopwatch.StartNew();
            string response = Exchange(server.Port, "POST", "/api/mark?t=" + Token);
            StringAssert.StartsWith("HTTP/1.1 503", response);
            StringAssert.Contains("\"ok\":false", response);
            Assert.Less(timer.ElapsedMilliseconds, 3000);
            Assert.IsTrue(server.TryDequeue(out var job));
            Assert.IsFalse(job.TryBegin(), "スマホに失敗と返した印は、あとから付けない");
        }
    }

    [Test]
    public void AJobStartedButNotFinishedSaysTheMarkMayHaveBeenMade()
    {
        using (var server = new ChartMarkPhoneServer(Token) { ReplyWaitMilliseconds = 1000 })
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            var call = Task.Run(() => Exchange(server.Port, "POST", "/api/mark?t=" + Token, "{\"requestId\":\"0123456789abcdef01234567\"}"));
            var job = WaitForJob(server);
            Assert.AreEqual("0123456789abcdef01234567", job.Request.RequestId);
            Assert.IsTrue(job.TryBegin(), "メインスレッドが実行を始めた(が、終わらない)");
            Assert.IsTrue(call.Wait(8000));
            StringAssert.StartsWith("HTTP/1.1 202", call.Result);
            StringAssert.Contains("\"pending\":true", call.Result);
            StringAssert.Contains("印は付いたかもしれないので一覧を見てください", call.Result);
            StringAssert.DoesNotContain("もう一度", call.Result, "押し直し(二重の印)をすすめない");
            job.Complete(200, ChartMarkPhoneServer.Result(true, "印1 ・ 3小節目"));
        }
    }

    [Test]
    public void ConnectionsFromOutsideTheLanAreClosedBeforeReading()
    {
        using (var server = new ChartMarkPhoneServer(Token) { AllowRemote = address => false })
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            string response;
            try { response = Exchange(server.Port, "GET", "/m?t=" + Token); }
            catch (IOException) { response = ""; }
            catch (SocketException) { response = ""; }
            Assert.IsFalse(response.StartsWith("HTTP/", StringComparison.Ordinal), "LAN の外には何も返さない: " + response);
            Assert.AreEqual(0, server.QueuedJobs);
        }
    }

    [Test]
    public void TooManyRequestsFromOnePhoneAreRefused()
    {
        using (var server = new ChartMarkPhoneServer(Token) { RequestsPerSecond = 3 })
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            for (int i = 0; i < 3; i++) StringAssert.StartsWith("HTTP/1.1 200", Exchange(server.Port, "GET", "/m?t=" + Token));
            string refused = Exchange(server.Port, "GET", "/api/state?t=" + Token);
            StringAssert.StartsWith("HTTP/1.1 429", refused);
            StringAssert.Contains("Retry-After: 1", refused);
            StringAssert.Contains("\"ok\":false", refused);
            Assert.AreEqual(0, server.QueuedJobs, "断った要求はゲームに回さない");
        }
    }

    [Test]
    public void OversizedRequestGetsAnAnswerInsteadOfAReset()
    {
        using (var server = new ChartMarkPhoneServer(Token))
        {
            Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
            string response = Exchange(server.Port, "GET", "/m?t=" + Token, null,
                "X-Padding: " + new string('a', ChartMarkPhoneServer.MaximumRequestBytes) + "\r\n");
            StringAssert.StartsWith("HTTP/1.1 413", response);
        }
    }

    [Test]
    public void DisposeAnswersWaitingRequestsAndStopsEveryThread()
    {
        var server = new ChartMarkPhoneServer(Token);
        Assert.IsTrue(server.Start(0, 0, IPAddress.Loopback), server.Error);
        int port = server.Port;
        var call = Task.Run(() =>
        {
            try { return Exchange(port, "GET", "/api/state?t=" + Token); }
            catch (Exception e) { return e.GetType().Name; }
        });
        var timer = Stopwatch.StartNew();
        while (server.QueuedJobs == 0 && timer.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        Assert.AreEqual(1, server.QueuedJobs);
        timer.Restart();
        server.Dispose();
        Assert.Less(timer.ElapsedMilliseconds, 3000, "受信スレッドを待って止める(Thread.Abort は使わない)");
        Assert.IsFalse(server.Running);
        Assert.AreEqual(0, server.ActiveConnections);
        Assert.AreEqual(0, server.QueuedJobs);
        Assert.IsTrue(call.Wait(5000), "待っていた要求も終わる");
        Assert.Throws<SocketException>(() =>
        {
            using (var client = new TcpClient()) client.Connect(IPAddress.Loopback, port);
        }, "止めたら受け付けない");
        server.Dispose();
    }

    // ---- 状態の JSON(土台の ChartMarkSummary から作る) ----

    [Test]
    public void StateJsonListsThisPlayAndTheRecentPastWithEscapedText()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            // 小数点がカンマの地域でも、JSON の数は点で書く。
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var marks = new List<ChartMarkSummary>
            {
                new ChartMarkSummary
                {
                    id = "s-2", song = "ManicSonic", title = "Manic \"Sonic\"", difficulty = "R", station = "B", number = 2,
                    songSeconds = 12.3456, bar = 18, comment = "改行\nあり", status = ChartMarks.StatusOpen,
                    source = ChartMarks.SourcePhone, current = true, savedAt = "2026-10-28T10:30:12+09:00"
                },
                null,
            };
            for (int i = 0; i < ChartMarkPhonePage.PastMarks + 5; i++)
                marks.Add(new ChartMarkSummary
                {
                    id = "p-" + i, song = "揺籠", title = "揺籠", difficulty = "P", station = "A", number = 1, songSeconds = double.NaN,
                    bar = 0, comment = "", status = ChartMarks.StatusCanceled, source = ChartMarks.SourceKey, current = false, savedAt = ""
                });
            string json = ChartMarkPhone.StateJson("ManicSonic", "Manic \"Sonic\"", "R", ChartMarkRecorder.StatePlaying, true, "B", 7, marks);
            StringAssert.StartsWith("{\"song\":\"ManicSonic\",\"title\":\"Manic \\\"Sonic\\\"\",\"difficulty\":\"R\",\"state\":\"playing\"," +
                                    "\"ready\":true,\"station\":\"B\",\"open\":7,\"marks\":[", json);
            StringAssert.Contains("{\"id\":\"s-2\",\"song\":\"ManicSonic\",\"title\":\"Manic \\\"Sonic\\\"\",\"difficulty\":\"R\"," +
                                  "\"station\":\"B\",\"number\":2,\"songSeconds\":12.346,\"bar\":18,\"comment\":\"改行\\nあり\"," +
                                  "\"status\":\"open\",\"source\":\"phone\",\"current\":true,\"savedAt\":\"2026-10-28T10:30:12+09:00\"}", json);
            StringAssert.Contains("\"songSeconds\":null", json, "数でない時刻は null");
            StringAssert.Contains("\"id\":\"p-" + (ChartMarkPhonePage.PastMarks - 1) + "\"", json);
            StringAssert.DoesNotContain("\"id\":\"p-" + ChartMarkPhonePage.PastMarks + "\"", json, "前の回は最近の 20 件まで");
            StringAssert.DoesNotContain(",,", json, "空の行は飛ばす");
            StringAssert.EndsWith("}]}", json);
        }
        finally { Thread.CurrentThread.CurrentCulture = culture; }
        Assert.AreEqual("{\"song\":\"\",\"title\":\"\",\"difficulty\":\"\",\"state\":\"other\",\"ready\":false,\"station\":\"PC\",\"open\":0,\"marks\":[]}",
            ChartMarkPhone.StateJson(null, null, null, ChartMarkRecorder.StateOther, false, "PC", 0, null));
        StringAssert.Contains("\"open\":null", ChartMarkPhone.StateJson("x", "x", "N", ChartMarkRecorder.StatePlaying, true, "PC", -1, null),
            "数をまだ読んでいなければ null");
        Assert.AreEqual(ChartMarks.MaxMarksPerPlay + ChartMarkPhonePage.PastMarks, ChartMarkPhone.MaxMarks, "この回の印は全部、前の回は 20 件");
        Assert.AreEqual(ChartMarks.CommentLimit, ChartMarkPhonePage.CommentMaxLength, "スマホの欄の長さは一言の上限と同じ");
    }

    // 演奏中はフォルダーを読まない。この回の印はメモリ(ChartMarks.Current)から、ほかは曲の前に読んだ一覧から。
    [Test]
    public void WhilePlayingTheListComesFromMemoryAndTheEarlierRead()
    {
        var current = new ChartMarkFile { songId = "ManicSonic", songTitle = "Manic Sonic", difficulty = "R", station = "" };
        current.marks.Add(new ChartMarkEntry
        {
            number = 1, id = "now-1", songTime = 3.5, bar = 2, comment = "a", status = ChartMarks.StatusOpen,
            source = ChartMarks.SourcePhone, markedAt = "2026-10-28T12:00:00+09:00"
        });
        current.marks.Add(new ChartMarkEntry { number = 2, id = "now-2", status = ChartMarks.StatusCanceled, source = ChartMarks.SourceKey });
        current.marks.Add(new ChartMarkEntry { number = 3, id = "now-3", status = ChartMarks.StatusFixed });
        current.marks.Add(null);
        var before = new List<ChartMarkSummary>
        {
            new ChartMarkSummary { id = "prev-1", current = true, status = ChartMarks.StatusOpen, title = "前の曲" },
            new ChartMarkSummary { id = "now-1", current = false },
            null,
            new ChartMarkSummary { id = "old-1", current = false, status = ChartMarks.StatusCanceled },
        };
        var list = ChartMarkPhone.MergePlaying(current, before, out int currentOpen);
        CollectionAssert.AreEqual(new[] { "now-2", "now-1", "prev-1", "old-1" }, list.ConvertAll(m => m.id), "この回(新しい順)のあとに前の回、同じ印は一度だけ");
        Assert.AreEqual(1, currentOpen, "取り消した印と直した印は数えない");
        Assert.IsTrue(list[0].current && list[1].current);
        Assert.IsFalse(list[2].current, "曲の前に「この回」だった印は、前の回に直す");
        Assert.AreEqual("前の曲", list[2].title);
        Assert.AreEqual("PC", list[1].station);
        Assert.AreEqual("Manic Sonic", list[1].title);
        Assert.AreEqual("R", list[1].difficulty);
        Assert.AreEqual(3.5, list[1].songSeconds);
        Assert.AreEqual("2026-10-28T12:00:00+09:00", list[1].savedAt);
        Assert.AreEqual(ChartMarks.SourcePhone, list[1].source);
        Assert.AreEqual(ChartMarks.StatusCanceled, list[0].status);
        Assert.IsEmpty(ChartMarkPhone.MergePlaying(null, null, out int none));
        Assert.AreEqual(0, none);
    }
}
