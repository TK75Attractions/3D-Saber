#!/usr/bin/env python3
"""GUIなしでsoakの純粋部と実loopback UDPをstandalone NUnitで検証。"""
import argparse
import os
from pathlib import Path
import tempfile

from check_player_smoke import ROOT, run


UDP_STUB = r"""
// 実socketが禁止された環境専用。送信thread・周期・Disposeは本番実装のまま実行する。
namespace System.Net.Sockets {
    public class SoakSocket { public System.Net.EndPoint LocalEndPoint; }
    public class UdpClient : IDisposable {
        static readonly object Gate = new object();
        static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.Queue<byte[]>> Ports =
            new System.Collections.Generic.Dictionary<int, System.Collections.Generic.Queue<byte[]>>();
        static int nextPort = 30000;
        int port;
        public SoakSocket Client = new SoakSocket();
        public UdpClient() {}
        public UdpClient(System.Net.IPEndPoint endpoint) {
            lock (Gate) { port = endpoint.Port == 0 ? nextPort++ : endpoint.Port;
                Client.LocalEndPoint = new System.Net.IPEndPoint(endpoint.Address, port);
                Ports.Add(port, new System.Collections.Generic.Queue<byte[]>()); }
        }
        public int Available { get { lock (Gate) return Ports[port].Count; } }
        public void Send(byte[] bytes, int length, string host, int target) {
            lock (Gate) { if (host != "127.0.0.1") throw new Exception("Unexpected destination"); Ports[target].Enqueue(bytes); }
        }
        public byte[] Receive(ref System.Net.IPEndPoint from) { lock (Gate) return Ports[port].Dequeue(); }
        public void Dispose() { lock (Gate) if (port != 0) Ports.Remove(port); }
    }
}
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stub-udp', action='store_true', help='socket禁止環境で送受信だけをstub化する')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    with tempfile.TemporaryDirectory(prefix='phonesaber-soak-') as tmp:
        build = Path(tmp)
        # native Unityは起動しない。純粋モデルが参照する既存引数名だけをstub化。
        host = build / 'Host.cs'
        host.write_text('''using System;
using System.Reflection;
using NUnit.Framework;
public class PhoneSaberPlayerSmoke { public const string Argument = "-phonesaberSmoke"; }
public class Host {
    public static int Main() {
        int passed = 0, failed = 0;
        var tests = new PhoneSaberSoakTests();
        foreach (var method in typeof(PhoneSaberSoakTests).GetMethods()) {
            if (!Attribute.IsDefined(method, typeof(TestAttribute))) continue;
            try { method.Invoke(tests, null); passed++; Console.WriteLine("PASS " + method.Name); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + method.Name + " " + (e.InnerException ?? e)); }
        }
        Console.WriteLine("standalone soak NUnit: passed=" + passed + " failed=" + failed + " (Unity native not executed; transport reported separately)");
        return failed == 0 ? 0 : 1;
    }
}''')
        if args.stub_udp:
            with host.open('a') as stream:
                stream.write(UDP_STUB)
        exe = build / 'soak.exe'
        sources = [ROOT / 'Assets/Scripts/Managers/Inputsystem/Input' / (name + '.cs')
                   for name in ('PhoneSaberSoakModel', 'PhoneSaberSoakSender')]
        sources += [ROOT / 'Assets/Tests/Editor/PhoneSaberSoakTests.cs', host]
        code = run([mono / 'bin/mono', mono / 'lib/mono/4.5/csc.exe', '/nologo', '/langversion:9.0',
                    '/r:' + str(nunit), '/out:' + str(exe)] + sources)
        if code:
            return code
        print('UDP transport: ' + ('STUB (socket bind unavailable)' if args.stub_udp else 'OS loopback'))
        return run([mono / 'bin/mono', exe], env=dict(os.environ, MONO_PATH=str(nunit.parent)))


if __name__ == '__main__':
    raise SystemExit(main())
