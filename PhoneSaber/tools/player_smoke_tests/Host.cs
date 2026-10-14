// Unity native 呼出し・UDPを代替し、実smokeのログ判定と遷移の制御だけを実行する。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace UnityEngine
{
    public enum LogType { Error, Assert, Warning, Log, Exception }
    public enum FindObjectsSortMode { None }
    public enum RuntimeInitializeLoadType { BeforeSceneLoad }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) {} }
    public class MonoBehaviour
    {
        public GameObject gameObject = new GameObject();
        public bool isActiveAndEnabled = true;
        public static T FindFirstObjectByType<T>() where T : new() => new T();
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode) where T : new()
        {
            if (typeof(T)==typeof(SaberInputBridge)) return (T[])(object)new[] { new SaberInputBridge { stickIndex=1 }, new SaberInputBridge { stickIndex=2 } };
            return new[] { new T(), new T() };
        }
        public static void DontDestroyOnLoad(object o) {}
    }
    public class GameObject { public bool activeInHierarchy = true; public GameObject(string name = "") {} public T AddComponent<T>() where T : new() => new T(); }
    public class WaitForSecondsRealtime { public float seconds; public WaitForSecondsRealtime(float s) { seconds = s; } }
    public static class Time { public static float realtimeSinceStartup; public static double realtimeSinceStartupAsDouble => realtimeSinceStartup; }
    public struct Vector3
    {
        public float x;
        public float sqrMagnitude => x*x;
        public static Vector3 operator -(Vector3 a,Vector3 b) => new Vector3 { x=a.x-b.x };
    }
    public static class Resources { public static T[] FindObjectsOfTypeAll<T>() => new T[1]; }
    public class AudioSource {} public class Material {} public class Texture2D {} public class Mesh {}
    public static class Mathf { public static float Max(float a,float b) => Math.Max(a,b); }
    public static class Application
    {
        public static bool isEditor, isBatchMode = true;
        public static int exitCode;
        public static event Action<string,string,LogType> logMessageReceived, logMessageReceivedThreaded;
        public static void Quit(int code) { exitCode = code; }
    }
    public static class Debug {
        public static readonly List<string> logs = new List<string>();
        public static void Log(object s) { logs.Add(s.ToString()); }
        public static void LogError(object s) { Log(s); }
        public static void LogException(Exception e) { Log(e); }
    }
    public static class ScreenCapture { public static void CaptureScreenshot(string p) {} }
    public static class PlayerPrefs
    {
        public static readonly Dictionary<string,string> values = new Dictionary<string,string>();
        public static bool HasKey(string k) => values.ContainsKey(k);
        public static string GetString(string k,string d="") => values.TryGetValue(k,out var v) ? v : d;
        public static void SetString(string k,string v) { values[k]=v; }
        public static void DeleteKey(string k) { values.Remove(k); }
        public static void Save() {}
    }
}
namespace UnityEngine.Profiling
{
    public static class Profiler { public static long GetTotalAllocatedMemoryLong()=>100; public static long GetTotalReservedMemoryLong()=>200; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; }
    public static class SceneManager
    {
        public static readonly List<string> scenes = new List<string>();
        public static string current, refuse;
        public static void LoadScene(string s)
        {
            scenes.Add(s); if (s != refuse) current=s;
            if (s=="Result") UnityEngine.PlayerPrefs.SetString(HighScoreStore.Key(GameSession.SelectedSongId,GameSession.SelectedDifficulty),"new result");
        }
        public static Scene GetActiveScene() => new Scene { name=current };
    }
}
// 実Socketは開かない。送信先と4座標本文を検査するだけ(受信・切断の実動作はPlayer側で確認)。
namespace System.Net.Sockets
{
    public class UdpClient : IDisposable
    {
        public void Dispose() {}
        public void Send(byte[] data,int length,string host,int port)
        {
            Assert.AreEqual("127.0.0.1", host);
            Assert.AreEqual(4, System.Text.Encoding.ASCII.GetString(data,0,length).Split(',').Length);
            if (port==5005) InputPoint.current.ReceivedPacketCount++;
            else if (port==5006) InputPoint.current.ReceivedPacketCount2++;
            else Assert.Fail("unexpected port");
        }
    }
}
public class InputPoint
{
    public static InputPoint current;
    public static InputPoint Instance => current;
    public int port=5005, port2=5006, ReceivedPacketCount, ReceivedPacketCount2;
    public bool HasValidStickEndpoints=true, HasValidStickEndpoints2=true;
}
public class SaberInputBridge : UnityEngine.MonoBehaviour {
    public int stickIndex; public bool HasBlade => visible; public static bool visible=true;
    public bool ReceivingCamera = true;
    public UnityEngine.Vector3 WorldEndA => new UnityEngine.Vector3 { x=UnityEngine.Time.realtimeSinceStartup };
    public UnityEngine.Vector3 WorldEndB => WorldEndA;
    public SaberBladeVisual BladeVisual = new SaberBladeVisual();
}
public class SaberBladeVisual : UnityEngine.MonoBehaviour { public int VertexCount => SaberInputBridge.visible ? 4 : 0; }
public class ScoreManager
{
    public static int hits=1;
    public int HitCount => hits;
    public int Score=100, MissCount=2, MaxCombo=1, ComboBonus=100, PerfectCount=1, GreatCount, GoodCount, BadCount;
}
public class CalibrationController
{
    public static bool live=true;
    public bool IsLive => live;
    public void DiscardAndExit() { GameSession.IsCalibrationMode=false; UnityEngine.SceneManagement.SceneManager.LoadScene("SongSelect"); }
}
public class SongSelectController {
    public string[] difficultyNames = { "Easy", "Normal" }; public int SongCount => 2;
    public bool IsLocked(int i)=>i==1; public string SongIdAt(int i)=>i==0 ? "ElDorado" : "locked";
    public static bool HasPlayableChart(string s,string[] d)=>d[0]=="Normal";
    public void Select(int i) { GameSession.SelectedSongId=SongIdAt(i); }
    public void SetDifficulty(int i) { GameSession.SelectedDifficulty=difficultyNames[i]; }
    public void StartGame() { UnityEngine.SceneManagement.SceneManager.LoadScene("Game"); }
    public void ReturnToTitle() { UnityEngine.SceneManagement.SceneManager.LoadScene("Title"); }
}
public class ResultController { public void OnBackButton() { UnityEngine.SceneManagement.SceneManager.LoadScene("Title"); } }
public static class HighScoreStore { public static string Key(string s,string d) => "hiscore_"+s+"_"+d; }
public static class GameSession
{
    public static string SelectedSongId, SelectedSongTitle, SelectedDifficulty;
    public static bool IsCalibrationMode, TutorialPending;
    public static int FinalScore, FinalHit, FinalMiss, FinalMaxCombo, FinalComboBonus, FinalPerfect, FinalGreat, FinalGood, FinalBad;
}
public static class PhoneSaberDisplaySync { public static double RefreshHzOverride; }

#if SOAK
// 実UDP/threadはcheck_player_soakが別に検証する。ここでは実coroutineの終了・復元・判定を検証。
public static class SongAchievementStore { public static string Key(string s,string d)=>"achievement_"+s+"_"+d; }
public static class DailyRankingStore { public static string Key(string s,string d)=>"ranking_"+s+"_"+d; }
public static class ScreenTransition { public static bool IsBusy; }
public sealed class PhoneSaberSoakSender : IDisposable
{
    public static int active;
    public PhoneSaberSoakSender(int r,int b,double seconds,Action<Exception> failed) {
        active++; InputPoint.current.ReceivedPacketCount+=120; InputPoint.current.ReceivedPacketCount2+=120;
    }
    public void Dispose() { active--; }
}
public class PhoneSaberEventLog
{
    public static PhoneSaberEventLog Current=new PhoneSaberEventLog(); public string LastError;
    public void Flush() {} public static void Record(string s,string c,string e,string d) {}
    public static string Format(DateTimeOffset t,string s,string c,string e,string d)=>"event="+e+" "+d;
}
public class SoakFlowTests
{
    static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Pump(IEnumerator routine,bool result)
    {
        while(routine.MoveNext()) {
            if(routine.Current is IEnumerator child) Pump(child,result);
            else UnityEngine.Time.realtimeSinceStartup+=routine.Current is UnityEngine.WaitForSecondsRealtime wait ? wait.seconds : .05f;
            if(result && UnityEngine.SceneManagement.SceneManager.current=="Game" && UnityEngine.Time.realtimeSinceStartup>=5) {
                foreach(string key in Keys()) UnityEngine.PlayerPrefs.SetString(key,"test result");
                UnityEngine.SceneManagement.SceneManager.LoadScene("Result");
            }
        }
    }
    static string[] Keys()=>new[] { HighScoreStore.Key("ElDorado","Normal"),
        SongAchievementStore.Key("ElDorado","Normal"),DailyRankingStore.Key("ElDorado","Normal") };
    static void Run(bool result=false,bool input=true,bool blade=true,string initial="Title")
    {
        UnityEngine.Time.realtimeSinceStartup=0; UnityEngine.Debug.logs.Clear();
        UnityEngine.SceneManagement.SceneManager.scenes.Clear(); UnityEngine.SceneManagement.SceneManager.current=initial;
        UnityEngine.SceneManagement.SceneManager.refuse=null; InputPoint.current=input ? new InputPoint() : null;
        SaberInputBridge.visible=blade; PhoneSaberSoakSender.active=0;
        var soak=new PhoneSaberPlayerSoak();
        typeof(PhoneSaberPlayerSoak).GetField("minutes",Private).SetValue(soak,.1);
        typeof(PhoneSaberPlayerSoak).GetField("gameSeconds",Private).SetValue(soak,8.0);
        Pump((IEnumerator)typeof(PhoneSaberPlayerSoak).GetMethod("Start",Private).Invoke(soak,null),result);
    }
    [TestCase(false)] [TestCase(true)]
    public void CompletesStartedLoopAndRestoresExistingAndAbsentScores(bool naturalResult)
    {
        UnityEngine.PlayerPrefs.values.Clear(); var keys=Keys();
        UnityEngine.PlayerPrefs.SetString(keys[0],"visitor score"); UnityEngine.PlayerPrefs.SetString(keys[2],"visitor ranking");
        Run(naturalResult);
        Assert.AreEqual(0,UnityEngine.Application.exitCode);
        Assert.AreEqual(0,PhoneSaberSoakSender.active);
        Assert.AreEqual("visitor score",UnityEngine.PlayerPrefs.GetString(keys[0]));
        Assert.IsFalse(UnityEngine.PlayerPrefs.HasKey(keys[1]));
        Assert.AreEqual("visitor ranking",UnityEngine.PlayerPrefs.GetString(keys[2]));
        Assert.That(UnityEngine.Debug.logs,Has.Some.Contains("song=ElDorado difficulty=Normal"));
        Assert.That(UnityEngine.Debug.logs,Has.Some.Contains("done loops=1 exceptions=0 leak=insufficient"));
        Assert.That(UnityEngine.Debug.logs,Has.Some.Contains("exit="+(naturalResult ? "result-return" : "scene-load")));
        Assert.AreEqual("Title",UnityEngine.SceneManagement.SceneManager.current);
    }
    [TestCase(false,true,"Title")] [TestCase(true,false,"Title")] [TestCase(true,true,"Game")]
    public void MissingInputBladeOrWrongInitialSceneFails(bool input,bool blade,string initial)
    {
        Run(input:input,blade:blade,initial:initial);
        Assert.AreEqual(2,UnityEngine.Application.exitCode); Assert.AreEqual(0,PhoneSaberSoakSender.active);
    }
}
#endif

public class SmokeTests
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Invoke(PhoneSaberPlayerSmoke smoke,string method,params object[] args) => typeof(PhoneSaberPlayerSmoke).GetMethod(method,Private).Invoke(smoke,args);
    static int Failures(PhoneSaberPlayerSmoke smoke)
    {
        int sum=0;
        foreach (string name in new[] { "exceptions", "errors" })
            sum += (int)(typeof(PhoneSaberPlayerSmoke).GetField(name,Private)?.GetValue(smoke) ?? 0);
        return sum;
    }
    [TestCase(UnityEngine.LogType.Error)]
    [TestCase(UnityEngine.LogType.Assert)]
    [TestCase(UnityEngine.LogType.Exception)]
    public void FailureLogsAreCounted(UnityEngine.LogType type)
    {
        var smoke=new PhoneSaberPlayerSmoke(); Invoke(smoke,"OnLog","failure","",type);
        Assert.AreEqual(1,Failures(smoke));
    }
    [TestCase(UnityEngine.LogType.Warning)]
    [TestCase(UnityEngine.LogType.Log)]
    public void NormalLogsDoNotFail(UnityEngine.LogType type)
    {
        var smoke=new PhoneSaberPlayerSmoke(); Invoke(smoke,"OnLog","bridge unavailable","",type);
        Assert.AreEqual(0,Failures(smoke));
    }
    static void Pump(IEnumerator routine)
    {
        while (routine.MoveNext())
        {
            if (routine.Current is IEnumerator child) Pump(child);
            else UnityEngine.Time.realtimeSinceStartup += routine.Current is UnityEngine.WaitForSecondsRealtime wait ? wait.seconds : .05f;
        }
    }
    static void Run(bool input=true,bool visible=true,int hits=1,string refuse=null,bool live=true,string initial="Title")
    {
        UnityEngine.Time.realtimeSinceStartup=0; UnityEngine.Debug.logs.Clear(); UnityEngine.SceneManagement.SceneManager.scenes.Clear();
        UnityEngine.SceneManagement.SceneManager.current=initial; UnityEngine.SceneManagement.SceneManager.scenes.Add(initial);
        UnityEngine.SceneManagement.SceneManager.refuse=refuse;
        InputPoint.current=input ? new InputPoint() : null; SaberInputBridge.visible=visible; ScoreManager.hits=hits; CalibrationController.live=live;
        var smoke=new PhoneSaberPlayerSmoke();
        typeof(PhoneSaberPlayerSmoke).GetField("secondsPerScene",Private).SetValue(smoke,PhoneSaberPlayerSmoke.DefaultSeconds);
        Pump((IEnumerator)typeof(PhoneSaberPlayerSmoke).GetMethod("Start",Private).Invoke(smoke,null));
    }
    [Test]
    public void VisitsResultCalibrationAndReturnsWithinMinute()
    {
        Run();
        CollectionAssert.AreEqual(new[] { "Title","SongSelect","Game","Result","Game","SongSelect","Title" },UnityEngine.SceneManagement.SceneManager.scenes);
        Assert.Less(UnityEngine.Time.realtimeSinceStartup,60);
        Assert.AreEqual(0,UnityEngine.Application.exitCode);
    }
    [TestCase(false,true,1,null,true)]
    [TestCase(true,false,1,null,true)]
    [TestCase(true,true,0,null,true)]
    [TestCase(true,true,1,"Result",true)]
    [TestCase(true,true,1,null,false)]
    public void MissingInputBladeCutSceneOrCalibrationFails(bool input,bool blade,int hits,string refuse,bool live)
    {
        Run(input,blade,hits,refuse,live); Assert.AreEqual(2,UnityEngine.Application.exitCode);
    }
    [Test]
    public void WrongInitialBuildSceneFailsEvenAfterForcedTitleLoad()
    {
        Run(initial:"Game"); Assert.AreEqual(2,UnityEngine.Application.exitCode);
    }
    [TestCase(false)]
    [TestCase(true)]
    public void RestoresOnlyTheIsolatedResultPreference(bool existing)
    {
        UnityEngine.PlayerPrefs.values.Clear();
        UnityEngine.PlayerPrefs.SetString("hiscore_ElDorado_normal","visitor score");
        string key=HighScoreStore.Key("PhoneSaberSmoke","normal");
        if (existing) UnityEngine.PlayerPrefs.SetString(key,"previous smoke");
        Run();
        Assert.AreEqual("visitor score",UnityEngine.PlayerPrefs.GetString("hiscore_ElDorado_normal",""));
        Assert.AreEqual(existing,UnityEngine.PlayerPrefs.HasKey(key));
        if (existing) Assert.AreEqual("previous smoke",UnityEngine.PlayerPrefs.GetString(key,""));
    }
}
public static class Program
{
    public static int Main()
    {
        int passed=0,failed=0;
        foreach (var method in
#if SOAK
            typeof(SoakFlowTests).GetMethods())
#else
            typeof(SmokeTests).GetMethods())
#endif
        {
            var cases=method.GetCustomAttributes(typeof(TestCaseAttribute),false);
            if (cases.Length==0 && !method.IsDefined(typeof(TestAttribute),false)) continue;
            var args=new List<object[]>();
            if (cases.Length==0) args.Add(Array.Empty<object>());
            else foreach (TestCaseAttribute c in cases) args.Add(c.Arguments);
            foreach (var a in args)
            {
                try { method.Invoke(Activator.CreateInstance(method.DeclaringType),a); passed++; }
                catch (TargetInvocationException e) { failed++; Console.WriteLine("FAIL "+method.Name+": "+e.InnerException.Message); }
            }
        }
        Console.WriteLine("standalone NUnit: passed="+passed+" failed="+failed+" (native Unity/UDP/cuts not executed)");
        return failed==0 ? 0 : 1;
    }
}
