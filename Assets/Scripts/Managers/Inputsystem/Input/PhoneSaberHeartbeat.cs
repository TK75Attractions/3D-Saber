using System;
using System.IO;
using UnityEngine;

// launcher に明示指定された Player だけ、main thread の進行を知らせる。Scene / prefab の設定は不要。
public sealed class PhoneSaberHeartbeat : MonoBehaviour
{
    PhoneSaberHeartbeatWriter writer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (Application.isEditor) return;
        try
        {
            string path = PhoneSaberHeartbeatWriter.ResolvePath(Environment.GetCommandLineArgs());
            if (string.IsNullOrWhiteSpace(path)) return;
            var heartbeat = new GameObject("PhoneSaber Heartbeat").AddComponent<PhoneSaberHeartbeat>();
            heartbeat.writer = new PhoneSaberHeartbeatWriter(path);
            DontDestroyOnLoad(heartbeat.gameObject);
        }
        catch { } // 監視の準備に失敗してもゲームを止めない。
    }

    void Update() { writer?.Tick(Time.realtimeSinceStartupAsDouble); }
    void OnDestroy() { writer?.Dispose(); }
}

// stream と1 byte buffer を使い回す。正常な定常書込みでは managed allocation を発生させない。
public sealed class PhoneSaberHeartbeatWriter : IDisposable
{
    readonly string path;
    readonly byte[] buffer = { 1 };
    FileStream stream;
    double nextWrite;
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
    IntPtr handle;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileTime(IntPtr file, IntPtr creation, IntPtr access, ref long write);
#endif

    public PhoneSaberHeartbeatWriter(string path) { this.path = path; }

    public static string ResolvePath(string[] args)
    {
        for (int i = 1; i + 1 < args.Length; i++)
            if (args[i] == "-phonesaberHeartbeat") return args[i + 1];
        return null;
    }

    public void Tick(double now)
    {
        if (now < nextWrite) return;
        nextWrite = now + 1;
        try
        {
            if (stream == null)
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, 1);
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
                handle = stream.SafeFileHandle.DangerousGetHandle();
#endif
            }
            stream.Position = 0;
            stream.Write(buffer, 0, 1);
            stream.Flush();
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            // Windows は書込み handle を閉じるまで mtime が確定しない場合があるため、明示更新する。
            long stamp = DateTime.UtcNow.ToFileTimeUtc();
            SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref stamp);
#endif
        }
        catch { Dispose(); } // パス / disk / 権限の障害をゲームへ伝播させない。
    }

    public void Dispose()
    {
        try { stream?.Dispose(); }
        catch { }
        stream = null;
    }
}
