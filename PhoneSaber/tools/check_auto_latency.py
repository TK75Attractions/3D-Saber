#!/usr/bin/env python3
"""GUIを開かず、Swift純粋部分・C#実応答Loop・共有fixtureをhost実行する。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
IOS = ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender'
TESTS = ROOT / 'PhoneSaber/ios/PhoneSaberSenderTests'
FIXTURE = ROOT / 'PhoneSaber/tools/fixtures/protocol_metrics.tsv'


def run(command, **kwargs):
    subprocess.run(list(map(str, command)), cwd=ROOT, check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', help='旧EventLogで切捨て回帰FAIL、旧応答Loopで未知datagram無返信を確認')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    compiler = [mono / 'bin/mono', mono / 'lib/mono/4.5/csc.exe', '/nologo', '/langversion:9.0', '/r:' + str(nunit)]
    with tempfile.TemporaryDirectory(prefix='phonesaber-auto-latency-') as temporary:
        tmp = Path(temporary)
        stubs = tmp / 'Host.cs'
        stubs.write_text('''using System;
using System.Diagnostics;
namespace UnityEngine {
 public static class Debug { public static void Log(object x){} public static void LogWarning(object x){} }
 public static class PlayerPrefs { public static string GetString(string k,string d)=>d; public static void SetString(string k,string v){} public static void Save(){} }
}
public static class PhoneSaberP2PBridgeProcess { public static bool AutoStartEnabled=true; }
public static class SwingMonotonicClock { public static long Timestamp => Stopwatch.GetTimestamp(); public static double ToSeconds(long ticks) => ticks/(double)Stopwatch.Frequency; }
''')
        runner = tmp / 'Run.cs'
        runner.write_text('''using System; using System.Reflection; using System.Diagnostics; using NUnit.Framework;
class Run { static int Main(string[] args) {
 if(args.Length>0 && args[0]=="serve") {
  using(var responder=new PhoneSaberDiscoveryResponder())
  using(var receiver=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0))) {
   receiver.Client.ReceiveTimeout=500;
   typeof(PhoneSaberDiscoveryResponder).GetField("running",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(responder,true);
   var reply=System.Text.Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.BuildReply(5005,5006,"host","A"));
   var loop=typeof(PhoneSaberDiscoveryResponder).GetMethod("Loop",BindingFlags.Instance|BindingFlags.NonPublic);
   var thread=new System.Threading.Thread(()=>loop.Invoke(responder,new object[]{receiver,reply}));thread.IsBackground=true;thread.Start();
   Console.WriteLine(((System.Net.IPEndPoint)receiver.Client.LocalEndPoint).Port);Console.Out.Flush();
   Console.ReadLine();
   var m=responder.Metrics.Read("127.0.0.1",SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));
   responder.Stop();receiver.Close();thread.Join(2000);
   Console.WriteLine("Swift -> actual C# discovery Loop metrics="+(m!=null ? "PASS":"FAIL"));return m!=null?0:1;
  }
 }
 int passed=0,failed=0;
 foreach(var type in Assembly.GetExecutingAssembly().GetTypes()) foreach(var method in type.GetMethods()) {
  if(!method.IsDefined(typeof(TestAttribute),false) || (args.Length>0 && method.Name!=args[0])) continue;
  try { method.Invoke(Activator.CreateInstance(type),new object[0]); passed++; }
  catch(Exception e) { Console.WriteLine("FAIL "+method.Name+" "+(e.InnerException??e).Message); failed++; }
 }
 Console.WriteLine("Auto latency NUnit host: "+passed+" PASS / "+failed+" FAIL");
 var store=new PhoneSaberMetricsStore(); var b=PhoneSaberLatencyEstimateTests.Valid; var sw=Stopwatch.StartNew();
 for(int i=0;i<10000;i++) store.Receive(b,b.Length,"host",i);
 Console.WriteLine("C# parse+store host mean-us="+(sw.Elapsed.TotalMilliseconds*1000/10000).ToString("F2"));
 return failed==0 && passed>0 ? 0:1;
}}
''')
        sources = [stubs, runner, INPUT / 'PhoneSaberLatencyEstimate.cs', INPUT / 'PhoneSaberInputStats.cs',
                   INPUT / 'PhoneSaberDiscoveryResponder.cs', ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyEstimateTests.cs']
        env = dict(os.environ, MONO_PATH=str(nunit.parent))
        if args.before_ref:
            before = tmp / 'BeforeEventLog.cs'
            before.write_bytes(subprocess.check_output(['git', 'show', args.before_ref + ':Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberEventLog.cs'], cwd=ROOT))
            binary = tmp / 'before.exe'
            run(compiler + ['/out:' + str(binary)] + sources + [before])
            result = subprocess.run([str(mono / 'bin/mono'), str(binary), 'FullEstimateSurvivesActualEventLogFormatter'], cwd=ROOT, env=env)
            if result.returncode != 1:
                raise RuntimeError('旧ログの切捨てFAILが再現しません')
            print('Before truncation regression FAIL reproduced (expected)', flush=True)
        if args.before_ref:
            old_discovery = tmp / 'OldDiscovery.cs'
            old_discovery.write_bytes(subprocess.check_output(['git', 'show', args.before_ref + ':Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberDiscoveryResponder.cs'], cwd=ROOT))
            old_runner = tmp / 'OldRun.cs'
            old_runner.write_text(r'''using System;using System.Reflection;using System.Net;using System.Net.Sockets;using System.Text;using System.Threading;
class Run {static int Main() {
 using(var r=new PhoneSaberDiscoveryResponder()) using(var s=new UdpClient(new IPEndPoint(IPAddress.Loopback,0))) using(var c=new UdpClient(0)) {
 s.Client.ReceiveTimeout=500;c.Client.ReceiveTimeout=200;
 typeof(PhoneSaberDiscoveryResponder).GetField("running",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(r,true);
 var loop=typeof(PhoneSaberDiscoveryResponder).GetMethod("Loop",BindingFlags.Instance|BindingFlags.NonPublic);
 byte[] reply=Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.BuildReply(5005,5006,"host","A"));
 var t=new Thread(()=>loop.Invoke(r,new object[]{s,reply}));t.IsBackground=true;t.Start();
 try {
 var target=(IPEndPoint)s.Client.LocalEndPoint;var remote=new IPEndPoint(IPAddress.Any,0);
 var metrics=Encoding.ASCII.GetBytes(System.IO.File.ReadAllLines("PhoneSaber/tools/fixtures/protocol_metrics.tsv")[1].Split('\t')[1]);
 c.Send(metrics,metrics.Length,target);
 try {c.Receive(ref remote);throw new Exception("Old PC replied to metrics");} catch(SocketException) {}
 var req=Encoding.ASCII.GetBytes(PhoneSaberDiscoveryResponder.Request);c.Send(req,req.Length,target);
 if(Encoding.ASCII.GetString(c.Receive(ref remote))!=Encoding.ASCII.GetString(reply)) return 1;
 Console.WriteLine("Old PC: unknown metrics ignored; discovery unchanged PASS");return 0;
 } finally {r.Stop();s.Close();t.Join(2000);}
 }
}}
''')
            old_binary = tmp / 'old-discovery.exe'
            run(compiler + ['/out:' + str(old_binary), stubs, old_runner, old_discovery])
            run([mono / 'bin/mono', old_binary], env=env)
        binary = tmp / 'after.exe'
        run(compiler + ['/out:' + str(binary)] + sources + [INPUT / 'PhoneSaberEventLog.cs'])
        run([mono / 'bin/mono', binary], env=env)
        developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip()) / 'Platforms/MacOSX.platform/Developer'
        frameworks = developer / 'Library/Frameworks'
        main = tmp / 'main.swift'
        main.write_text(r'''import XCTest
import Foundation
import Darwin
if CommandLine.arguments.count > 1 {
 let probe=LANLivenessProbe(port: UInt16(CommandLine.arguments[1])!)
 probe.start(host: "127.0.0.1")
 Thread.sleep(forTimeInterval: 1.2)
 let snapshot=probe.metricsRoundTrip(at: ProcessInfo.processInfo.systemUptime)
 precondition(probe.isAlive && snapshot.count>0 && snapshot.median != nil)
 let line=try! String(contentsOfFile: CommandLine.arguments[2],encoding:.utf8).split(separator:"\n")[1].split(separator:"\t")[1]
 let start=ProcessInfo.processInfo.systemUptime
 probe.sendMetrics(String(line))
 print(String(format:"Swift sendMetrics enqueue host-us=%.2f RTT-ms=%.2f n=%d",(ProcessInfo.processInfo.systemUptime-start)*1000000,snapshot.median!,snapshot.count))
 Thread.sleep(forTimeInterval: 0.1)
 probe.stop();exit(0)
}
let suite = MetricsDatagramTests.defaultTestSuite
suite.run()
guard let result = suite.testRun, result.executionCount > 0, result.hasSucceeded else { exit(1) }
var m = MetricsDatagram(); m.median = 37; m.p95 = 44; m.samples = 300
let started = ProcessInfo.processInfo.systemUptime
for _ in 0..<10000 { precondition(m.line() != nil) }
print(String(format: "Swift formatter host mean-us=%.2f", (ProcessInfo.processInfo.systemUptime-started)*1000000/10000))
''')
        frame = (IOS / 'FrameProcessor.swift').read_text()
        clock = frame.split('enum HostMonotonicClock {', 1)[1].split('\nfinal class FrameProcessor', 1)[0]
        (tmp / 'Clock.swift').write_text('import Foundation\nimport CoreMedia\nenum HostMonotonicClock {' + clock)
        swift = tmp / 'swift-tests'
        run(['xcrun', 'swiftc', '-O', '-D', 'METRICS_STANDALONE', IOS / 'MetricsDatagram.swift',
             TESTS / 'MetricsDatagramTests.swift', IOS / 'UDPSender.swift', IOS / 'P2PSender.swift', IOS / 'P2PProtocol.swift', tmp / 'Clock.swift', main, '-module-cache-path', tmp / 'cache',
             '-I', developer / 'usr/lib', '-L', developer / 'usr/lib', '-F', frameworks,
             '-Xlinker', '-rpath', '-Xlinker', frameworks,
             '-Xlinker', '-rpath', '-Xlinker', developer / 'usr/lib', '-o', swift])
        run([swift])
        with subprocess.Popen([str(mono / 'bin/mono'), str(binary), 'serve'], cwd=ROOT, env=env,
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True) as server:
            try:
                port = server.stdout.readline().strip()
                run([swift, port, FIXTURE])
                stdout, _ = server.communicate('done\n', timeout=5)
                print(stdout, end='')
                if server.returncode:
                    raise RuntimeError('Swift -> C# loopback failed')
            finally:
                if server.poll() is None:
                    server.kill()
                    server.wait()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
