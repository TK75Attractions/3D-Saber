using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// スマホから印(ChartMarkPhone、2026-10-28)。ループバックから /api/state・/api/mark・/api/comment・/api/cancel を送り、
// 印が土台の TryMark(F5 と同じ入口)を通って印のファイルに入ること、一言と取り消しが保存されることを、本物の Game の場面で見る。
// 常駐の部品はテスト中に作られないので、ここで ChartMarkRecorder と ChartMarkPhone(決まった合言葉・空いているポート)を作る。
public class ChartMarkPhonePlayTests
{
    const string Token = "0123456789abcdef0123456789abcdef";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    string folder;
    ChartMarkPhone phone;
    ChartMarkRecorder recorder;
    Scene outside;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        foreach (var old in Object.FindObjectsByType<ChartMarkPhone>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        foreach (var old in Object.FindObjectsByType<ChartMarkRecorder>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        folder = Path.Combine(Path.GetTempPath(), "ChartMarkPhonePlayTests_" + Guid.NewGuid().ToString("N"));
        ChartMarks.FolderOverride = folder;
        ChartMarks.BeginPlay();
        var go = new GameObject("ChartMarkRecorder");
        Object.DontDestroyOnLoad(go);
        recorder = go.AddComponent<ChartMarkRecorder>();
        Assert.AreSame(recorder, ChartMarkRecorder.Instance);
        outside = default;
        // ループバックで待ち受ける(LAN に出さない・Windows のファイアウォールに問い合わせない)。
        phone = ChartMarkPhone.Create(Token, 0, 0, IPAddress.Loopback);
        Assert.NotNull(phone, "空いているポートで始める");
        Assert.AreSame(phone, ChartMarkPhone.Instance);
        Assert.That(phone.Port, Is.GreaterThan(0));
        Assert.AreEqual(IPAddress.Loopback, phone.Server.ListenAddress);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (phone != null) Object.DestroyImmediate(phone.gameObject);
        Assert.IsNull(ChartMarkPhone.Instance);
        Assert.IsNull(ChartMarkPhone.Url);
        var scene = SceneManager.GetActiveScene();
        if (scene.name == ChartMarkRecorder.GameScene)
        {
            foreach (var source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) source.Stop();
            SceneManager.SetActiveScene(SceneManager.CreateScene("ChartMarkPhoneCleanup"));
            yield return SceneManager.UnloadSceneAsync(scene);
        }
        // 曲の外のテストで作った場面も片付ける(ほかの場面を表にしてから下ろす)。
        if (outside.IsValid() && outside.isLoaded && SceneManager.sceneCount > 1)
        {
            if (SceneManager.GetActiveScene() == outside)
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var other = SceneManager.GetSceneAt(i);
                    if (other != outside && other.isLoaded)
                    {
                        SceneManager.SetActiveScene(other);
                        break;
                    }
                }
            yield return SceneManager.UnloadSceneAsync(outside);
        }
        if (recorder != null) Object.DestroyImmediate(recorder.gameObject);
        foreach (var entry in sessionValues) entry.Key.SetValue(null, entry.Value);
        sessionValues.Clear();
        ChartMarks.BeginPlay();
        ChartMarks.FolderOverride = null;
        ChartMarkPhoneLink.Url = null;
        // 画面の写真は別のスレッドで書くので、書き終わるまで少し待ってから消す。
        for (int i = 0; i < 10 && Directory.Exists(folder); i++)
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (Directory.Exists(folder)) yield return new WaitForSecondsRealtime(.2f);
        }
    }

    [UnityTest]
    public IEnumerator PhoneMarksGoThroughTheRecorderAndCommentsAreSaved()
    {
        GameSession.SelectedSongId = "揺籠";
        GameSession.SelectedSongTitle = "揺籠";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        yield return SceneManager.LoadSceneAsync(ChartMarkRecorder.GameScene);
        float deadline = Time.realtimeSinceStartup + 30f;
        while (ChartMarkRecorder.PlayState != ChartMarkRecorder.StatePlaying && Time.realtimeSinceStartup < deadline)
            yield return null;
        Assert.AreEqual(ChartMarkRecorder.StatePlaying, ChartMarkRecorder.PlayState, "曲が始まる");
        int port = phone.Port;

        var state = Call(port, "GET", "/api/state?t=" + Token);
        yield return Wait(state);
        StringAssert.StartsWith("HTTP/1.1 200", state.Result);
        StringAssert.Contains("\"state\":\"playing\"", state.Result);
        StringAssert.Contains("\"ready\":true", state.Result);
        StringAssert.Contains("\"song\":\"揺籠\"", state.Result);
        StringAssert.Contains("\"marks\":[]", state.Result, "まだ印は無い");

        const string requestBody = "{\"requestId\":\"0123456789abcdef01234567\"}";
        var mark = Call(port, "POST", "/api/mark?t=" + Token, requestBody);
        yield return Wait(mark);
        StringAssert.StartsWith("HTTP/1.1 200", mark.Result);
        StringAssert.Contains("\"ok\":true", mark.Result);
        StringAssert.Contains("\"message\":\"印1 ・ ", mark.Result);
        // 返事が届かなかったときの送り直し(同じ要求 id)は、もう一度付けずに同じ返事を返す。
        var resend = Call(port, "POST", "/api/mark?t=" + Token, requestBody);
        yield return Wait(resend);
        Assert.AreEqual(Body(mark.Result), Body(resend.Result), "同じ返事(「続けて付けるときは…」にもならない)");
        var marks = ChartMarks.Recent(10);
        Assert.AreEqual(1, marks.Count, "二重の印にならない");
        var summary = marks[0];
        Assert.IsTrue(summary.current);
        Assert.AreEqual(ChartMarks.SourcePhone, summary.source, "スマホの印として、F5 と同じこの回の印に入る");
        string id = PhoneSaberControlPanelState.Quote(summary.id);

        var listed = Call(port, "GET", "/api/state?t=" + Token);
        yield return Wait(listed);
        StringAssert.Contains("\"id\":" + id, listed.Result, "操作のあとはすぐ一覧に出る");
        StringAssert.Contains("\"source\":\"phone\"", listed.Result);
        StringAssert.Contains("\"current\":true", listed.Result);

        const string text = "スマホから: 半拍おそく";
        var comment = Call(port, "POST", "/api/comment?t=" + Token, "{\"id\":" + id + ",\"text\":\"" + text + "\"}");
        yield return Wait(comment);
        StringAssert.StartsWith("HTTP/1.1 200", comment.Result);
        StringAssert.Contains("\"ok\":true", comment.Result);
        Assert.AreEqual(text, ChartMarks.Recent(10)[0].comment);
        Assert.IsTrue(FileContains(folder, text), "一言が印のファイルに保存される");

        var cancel = Call(port, "POST", "/api/cancel?t=" + Token, "{\"id\":" + id + ",\"canceled\":true}");
        yield return Wait(cancel);
        StringAssert.Contains("\"ok\":true", cancel.Result);
        Assert.AreEqual(ChartMarks.StatusCanceled, ChartMarks.Recent(10)[0].status);
        var restore = Call(port, "POST", "/api/cancel?t=" + Token, "{\"id\":" + id + ",\"canceled\":false}");
        yield return Wait(restore);
        StringAssert.Contains("\"ok\":true", restore.Result);
        Assert.AreEqual(ChartMarks.StatusOpen, ChartMarks.Recent(10)[0].status, "取り消しは戻せる");

        var unknown = Call(port, "POST", "/api/comment?t=" + Token, "{\"id\":\"20000101-000000_PC_x_y_00000000-1\",\"text\":\"a\"}");
        yield return Wait(unknown);
        StringAssert.StartsWith("HTTP/1.1 200", unknown.Result);
        StringAssert.Contains("\"ok\":false", unknown.Result, "無い印には書かない");

        var refused = Call(port, "GET", "/api/state?t=0123456789abcdef0123456789abcdee");
        yield return Wait(refused);
        StringAssert.StartsWith("HTTP/1.1 403", refused.Result);
        Assert.AreEqual(1, ChartMarks.Recent(10).Count);
        yield return WaitForScreenshot();
    }

    [UnityTest]
    public IEnumerator MarkIsRefusedOutsideTheSong()
    {
        // 前のテストが Game の場面を残していても、曲の外(ほかの場面)にする。作った場面は TearDown で下ろす。
        outside = SceneManager.CreateScene("ChartMarkPhoneOutside" + Guid.NewGuid().ToString("N"));
        SceneManager.SetActiveScene(outside);
        yield return null;
        Assert.AreEqual(ChartMarkRecorder.StateOther, ChartMarkRecorder.PlayState);
        var state = Call(phone.Port, "GET", "/api/state?t=" + Token);
        yield return Wait(state);
        StringAssert.Contains("\"state\":\"other\"", state.Result);
        var mark = Call(phone.Port, "POST", "/api/mark?t=" + Token);
        yield return Wait(mark);
        StringAssert.StartsWith("HTTP/1.1 200", mark.Result);
        StringAssert.Contains("\"ok\":false", mark.Result, "演奏中でなければ印を付けない(画面のボタンも灰色)");
        Assert.IsEmpty(ChartMarks.Recent(10));
        var page = Call(phone.Port, "GET", "/m?t=" + Token);
        yield return Wait(page);
        StringAssert.StartsWith("HTTP/1.1 200", page.Result);
        StringAssert.Contains("<title>譜面の印</title>", page.Result, "画面はメインスレッドを待たずに返す");
    }

    IEnumerator WaitForScreenshot()
    {
        float deadline = Time.realtimeSinceStartup + 3f;
        while (Time.realtimeSinceStartup < deadline && (!Directory.Exists(folder) || Directory.GetFiles(folder, "*.jpg").Length == 0))
            yield return null;
        yield return new WaitForSecondsRealtime(.3f);
    }

    static bool FileContains(string folder, string text)
    {
        if (!Directory.Exists(folder)) return false;
        foreach (string path in Directory.GetFiles(folder, "*.json"))
        {
            // JsonUtility が日本語を \uXXXX で書いても読めるように戻してから探す。
            string plain = Regex.Replace(File.ReadAllText(path), @"\\u([0-9a-fA-F]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            if (plain.Contains(text)) return true;
        }
        return false;
    }

    // 返事の本文(ヘッダのあと)。
    static string Body(string response)
    {
        int end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? response : response.Substring(end + 4);
    }

    static Task<string> Call(int port, string method, string target, string body = null) =>
        Task.Run(() => Exchange(port, method, target, body));

    static string Exchange(int port, string method, string target, string body)
    {
        using (var client = new TcpClient())
        {
            client.ReceiveTimeout = 10000;
            client.Connect(IPAddress.Loopback, port);
            using (var stream = client.GetStream())
            {
                byte[] payload = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
                string head = method + " " + target + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n" +
                              (body == null ? "" : "Content-Type: application/json\r\nContent-Length: " + payload.Length + "\r\n") + "\r\n";
                byte[] bytes = Encoding.ASCII.GetBytes(head);
                stream.Write(bytes, 0, bytes.Length);
                stream.Write(payload, 0, payload.Length);
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
    }

    // 返事はメインスレッドがキューを処理してから来るので、コマを進めながら待つ。
    static IEnumerator Wait(Task<string> call)
    {
        float deadline = Time.realtimeSinceStartup + 10f;
        while (!call.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(call.IsCompleted, "返事が来る");
        if (call.IsFaulted) Assert.Fail(call.Exception.ToString());
    }
}
