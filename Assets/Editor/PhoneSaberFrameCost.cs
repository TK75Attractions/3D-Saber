using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

// 専用コピーのバッチでだけ使う。ゲームコード、Scene、Prefab、Player の構成は変更しない。
// -executeMethod PhoneSaberFrameCost.Measure -phonesaberFrameCostOutput <dir>
// 任意: -phonesaberFrameCostProfile（別の診断 run。callstack の負荷を通常計測と混ぜない）
// 受信→描画直前の時間も同じ run で測る（PlayerLoop の Update 先頭と PostLateUpdate 先頭に計測点を置く）。
[InitializeOnLoad]
public static class PhoneSaberFrameCost
{
    const string Key = "PhoneSaberFrameCost.Running";
    const double WarmupSeconds = 3, MeasureSeconds = 10, TimeoutSeconds = 90;
    const int Capacity = 65536;
    static int phase;
    static double started, warmupAt, measuredAt;
    static string output;
    static bool profile, previousProfileEditor, profileSettingsChanged;
    static int sentAtMeasure;
    static double senderMeasuredAt;
    static GamePlayManager manager;
    static SyntheticSender sender;
    static ProfilerRecorder gc, mainThread, playerLoop;
    // 受信→描画の計測。測定中は事前確保した配列へ書くだけで、割当・文字列生成をしない。
    // 時計は InputPoint の受信時刻と同じ SwingMonotonicClock（Stopwatch）。
    static readonly double[] frameIntervalMs = new double[Capacity], sampleToRenderMs = new double[Capacity];
    static readonly double[] receiveToSampleMs = new double[Capacity], receiveToRenderMs = new double[Capacity];
    static int frameCount, appliedCount, receivedRedAtMeasure;
    static bool latencyMeasuring, latencyOverflow;
    static double updateStartSeconds = double.NaN, lastAppliedStamp = double.NaN;
    static PlayerLoopSystem? originalPlayerLoop;
    struct UpdateStartMarker { }
    struct RenderStartMarker { }

    [Serializable] sealed class Metric
    {
        public string name, unit;
        public int samples;
        public double mean, median, p95, max;
    }
    [Serializable] sealed class Allocator
    {
        public string marker;
        public long bytes, calls;
    }
    [Serializable] sealed class Report
    {
        public string unity, os, cpu, scene, song, difficulty, graphicsAPI, quality, mode;
        public int vSync, targetFrameRate, maxQueuedFrames, sentRed, sentBlue, redHz, blueHz;
        public int profileFirstFrame, profileLastFrame;
        public double measuredSeconds, senderSeconds, senderHz, senderMaxLateMs;
        public bool redParsed, blueParsed, redEndpoints, blueEndpoints;
        public Metric gcBytes, mainThreadMs, playerLoopMs;
        // frameInterval: Update 先頭どうしの間隔。sampleToRender: Update 先頭（InputPoint が最新値を取り込む直前）
        // → PostLateUpdate 先頭（描画直前）。receiveToSample / receiveToRender: 赤の UDP receive() 完了 → その値を初めて
        // 取り込んだフレームの Update 先頭 / PostLateUpdate 先頭。GPU・present・表示機器の遅延は含まない。
        public Metric frameIntervalMs, sampleToRenderMs, receiveToSampleMs, receiveToRenderMs;
        public int receivedRedPackets, appliedRedPackets;
        public List<Allocator> topAllocationMarkers;
    }

    static PhoneSaberFrameCost()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void Measure()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用コピーの batchmode のみ使用できます");
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-phonesaberFrameCostOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("-phonesaberFrameCostOutput が必要です");
        // 他の Player/Editor へ合成入力を送らないよう、Game を開く前に既定ポートを確認する。
        using (var red = new UdpClient(new IPEndPoint(IPAddress.Any, 5005)))
        using (var blue = new UdpClient(new IPEndPoint(IPAddress.Any, 5006))) { }
        output = Path.GetFullPath(args[at + 1]);
        Directory.CreateDirectory(output);
        SessionState.SetString(Key + ".Output", output);
        SessionState.SetBool(Key + ".Profile", Array.IndexOf(args, "-phonesaberFrameCostProfile") >= 0);
        int qualityAt = Array.IndexOf(args, "-phonesaberFrameCostQuality");
        int quality = -1;
        if (qualityAt >= 0 && (qualityAt + 1 >= args.Length ||
            !int.TryParse(args[qualityAt + 1], out quality) || quality < 0 || quality >= QualitySettings.names.Length))
            throw new ArgumentException("-phonesaberFrameCostQuality は有効な quality index が必要です");
        SessionState.SetInt(Key + ".Quality", quality);
        int fpsAt = Array.IndexOf(args, "-phonesaberFrameCostFps");
        int fps = 120;
        if (fpsAt >= 0 && (fpsAt + 1 >= args.Length || !int.TryParse(args[fpsAt + 1], out fps) || fps < 1 || fps > 1000))
            throw new ArgumentException("-phonesaberFrameCostFps は1〜1000が必要です");
        SessionState.SetInt(Key + ".FPS", fps);
        SessionState.SetBool(Key, true);
        EditorSceneManager.OpenScene("Assets/Scenes/Game.unity", OpenSceneMode.Single);
        PrepareInput();
        EditorApplication.isPlaying = true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void PrepareInput()
    {
        if (!SessionState.GetBool(Key, false)) return;
        int quality = SessionState.GetInt(Key + ".Quality", -1);
        if (quality >= 0) QualitySettings.SetQualityLevel(quality, true);
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
        // 合成座標だけを計測する。実機 BLE の接続はこの専用 run で開始しない。
        // Resources asset のメモリ上の値だけ変更し、SaveAssets / scene 保存は呼ばない。
        var settings = Resources.Load<ImuBleServiceSettings>("ImuBleServiceSettings");
        if (settings != null)
        {
            var serialized = new SerializedObject(settings);
            serialized.FindProperty("autoStartBleBridge").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Key, false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            phase = 0;
            started = EditorApplication.timeSinceStartup;
            output = SessionState.GetString(Key + ".Output", "");
            profile = SessionState.GetBool(Key + ".Profile", false);
        }
        else if (state == PlayModeStateChange.ExitingPlayMode) Fail("計測完了前に Play Mode が終了しました");
    }

    static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying) return;
        try
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - started > TimeoutSeconds) throw new TimeoutException("Game の計測が90秒以内に完了しませんでした");
            if (phase == 0)
            {
                manager = UnityEngine.Object.FindFirstObjectByType<GamePlayManager>();
                var input = InputPoint.Instance;
                if (manager == null || !manager.songPlayer.IsPlaying || input == null ||
                    !input.ReceiverAlive || !input.ReceiverAlive2) return;
                if (input.port != 5005 || input.port2 != 5006) throw new InvalidOperationException("既定UDPポートのGameが必要です");
                // batch Editor の無制限ループによる sender の飢餓を避ける計測条件。
                Application.targetFrameRate = SessionState.GetInt(Key + ".FPS", 120);
                sender = new SyntheticSender();
                sender.Start();
                warmupAt = now;
                phase = 1;
            }
            else if (phase == 1 && now - warmupAt >= WarmupSeconds)
            {
                if (profile)
                {
                    previousProfileEditor = ProfilerDriver.profileEditor;
                    profileSettingsChanged = true;
                    ProfilerDriver.profileEditor = false;
                    Profiler.logFile = Path.Combine(output, "allocation-profile.raw");
                    Profiler.enableBinaryLog = true;
                    Profiler.enableAllocationCallstacks = true;
                    Profiler.enabled = true;
                }
                const ProfilerRecorderOptions options = ProfilerRecorderOptions.Default;
                gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", Capacity, options);
                mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", Capacity, options);
                playerLoop = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PlayerLoop", Capacity, options);
                if (!gc.Valid || !mainThread.Valid || !playerLoop.Valid)
                    throw new InvalidOperationException("必要な ProfilerRecorder marker がありません");
                sentAtMeasure = sender.SentPairs;
                senderMeasuredAt = sender.ElapsedSeconds;
                StartLatency(InputPoint.Instance);
                measuredAt = EditorApplication.timeSinceStartup;
                phase = 2;
            }
            else if (phase == 2 && now - measuredAt >= MeasureSeconds)
            {
                gc.Stop(); mainThread.Stop(); playerLoop.Stop();
                StopLatency();
                int sent = sender.SentPairs - sentAtMeasure;
                double senderSeconds = sender.ElapsedSeconds - senderMeasuredAt;
                double senderHz = sent / senderSeconds;
                // 書き出し・集計・文字列生成・stats snapshot は Recorder の停止後に行う。
                Profiler.enabled = false;
                Profiler.enableBinaryLog = false;
                Profiler.enableAllocationCallstacks = false;
                if (gc.WrappedAround || mainThread.WrappedAround || playerLoop.WrappedAround || latencyOverflow)
                    throw new InvalidOperationException("Recorder capacity を超えたため比較できません");
                if (senderHz < 58 || senderHz > 62)
                    throw new InvalidOperationException($"60Hz条件を満たしません: {senderHz:F2}Hz ({sent} pairs / {senderSeconds:F3}s)");
                if (profile && !ProfilerDriver.LoadProfile(Path.Combine(output, "allocation-profile.raw"), false))
                    throw new InvalidOperationException("binary allocation profile を読めませんでした");
                int firstProfileFrame = profile ? ProfilerDriver.firstFrameIndex : -1;
                int lastProfileFrame = profile ? ProfilerDriver.lastFrameIndex : -1;
                var input = InputPoint.Instance;
                if (input == null || manager == null || !manager.songPlayer.IsPlaying)
                    throw new InvalidOperationException("計測中にGame/曲が終了しました");
                var red = input.ReadInputStats();
                var blue = input.ReadInputStats(true);
                if (!red.HasPacket || !blue.HasPacket || !red.PayloadParsed || !blue.PayloadParsed ||
                    !input.HasValidStickEndpoints || !input.HasValidStickEndpoints2 || sender.Error != null)
                    throw new InvalidOperationException("合成UDPの受信/解析に失敗しました: " + sender.Error);
                var report = new Report
                {
                    unity = Application.unityVersion, os = SystemInfo.operatingSystem, cpu = SystemInfo.processorType,
                    scene = "Game", song = "ElDorado", difficulty = "normal",
                    graphicsAPI = SystemInfo.graphicsDeviceType.ToString(), quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    mode = profile ? "Editor Play Mode + allocation callstacks (diagnostic overhead)" : "Editor Play Mode counters",
                    vSync = QualitySettings.vSyncCount, targetFrameRate = Application.targetFrameRate,
                    maxQueuedFrames = QualitySettings.maxQueuedFrames, measuredSeconds = now - measuredAt,
                    sentRed = sent, sentBlue = sent, senderSeconds = senderSeconds,
                    senderMaxLateMs = sender.MaxLateMs, redHz = red.PacketsPerSecond, blueHz = blue.PacketsPerSecond,
                    redParsed = red.PayloadParsed, blueParsed = blue.PayloadParsed,
                    redEndpoints = input.HasValidStickEndpoints, blueEndpoints = input.HasValidStickEndpoints2,
                    gcBytes = Export(gc, "gc-bytes", "bytes", 1),
                    mainThreadMs = Export(mainThread, "main-thread", "ms", 1e-6),
                    playerLoopMs = Export(playerLoop, "player-loop", "ms", 1e-6),
                    frameIntervalMs = Export(frameIntervalMs, frameCount - 1, "frame-interval", "ms"),
                    sampleToRenderMs = Export(sampleToRenderMs, frameCount, "sample-to-render", "ms"),
                    receiveToSampleMs = Export(receiveToSampleMs, appliedCount, "receive-to-sample", "ms"),
                    receiveToRenderMs = Export(receiveToRenderMs, appliedCount, "receive-to-render", "ms"),
                    receivedRedPackets = input.ReceivedPacketCount - receivedRedAtMeasure,
                    appliedRedPackets = appliedCount,
                    profileFirstFrame = profile ? Math.Max(firstProfileFrame, ProfilerDriver.firstFrameIndex) : -1,
                    profileLastFrame = profile ? lastProfileFrame : -1,
                    topAllocationMarkers = profile ? AllocationMarkers(firstProfileFrame, lastProfileFrame) : new List<Allocator>()
                };
                report.senderHz = report.sentRed / report.senderSeconds;
                File.WriteAllText(Path.Combine(output, "summary.json"), JsonUtility.ToJson(report, true) + "\n");
                Debug.Log($"[PhoneSaberFrameCost] PASS GC median={report.gcBytes.median:F0}B p95={report.gcBytes.p95:F0}B " +
                    $"main-thread median={report.mainThreadMs.median:F3}ms p95={report.mainThreadMs.p95:F3}ms sender={report.senderHz:F2}Hz " +
                    $"receive-to-render median={report.receiveToRenderMs.median:F3}ms p95={report.receiveToRenderMs.p95:F3}ms " +
                    $"sample-to-render median={report.sampleToRenderMs.median:F3}ms output={output}");
                Cleanup();
                EditorApplication.Exit(0);
            }
        }
        catch (Exception exception) { Fail(exception.ToString()); }
    }

    static Metric Export(ProfilerRecorder recorder, string name, string unit, double scale)
    {
        var samples = recorder.ToArray();
        var values = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) values[i] = samples[i].Value * scale;
        return Export(values, values.Length, name, unit);
    }

    static Metric Export(double[] source, int count, string name, string unit)
    {
        if (count < 10) throw new InvalidOperationException(name + " のサンプルが不足しています");
        var values = new double[count];
        Array.Copy(source, values, count);
        double sum = 0;
        using (var writer = new StreamWriter(Path.Combine(output, name + ".csv")))
        {
            writer.WriteLine("sample_index,value_" + unit);
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
                writer.WriteLine(i.ToString(CultureInfo.InvariantCulture) + "," + values[i].ToString("R", CultureInfo.InvariantCulture));
            }
        }
        Array.Sort(values);
        int mid = values.Length / 2;
        return new Metric { name = name, unit = unit, samples = values.Length, mean = sum / values.Length,
            median = values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid],
            p95 = values[(int)Math.Ceiling(values.Length * .95) - 1], max = values[values.Length - 1] };
    }

    // Update の先頭（ScriptRunBehaviourUpdate より前）に計測点を入れる。InputPoint(-2000) が最初の script。
    static void StartLatency(InputPoint input)
    {
        frameCount = 0; appliedCount = 0; latencyOverflow = false;
        updateStartSeconds = double.NaN;
        lastAppliedStamp = input.LastReceivedMonotonicTime;
        receivedRedAtMeasure = input.ReceivedPacketCount;
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        originalPlayerLoop = loop;
        var systems = (PlayerLoopSystem[])loop.subSystemList.Clone();
        bool inserted = false, insertedRender = false;
        for (int i = 0; i < systems.Length; i++)
        {
            bool update = systems[i].type == typeof(UnityEngine.PlayerLoop.Update);
            bool postLate = systems[i].type == typeof(UnityEngine.PlayerLoop.PostLateUpdate);
            if (!update && !postLate) continue;
            var children = new List<PlayerLoopSystem>(systems[i].subSystemList ?? new PlayerLoopSystem[0]);
            // PostLateUpdate の先頭 = 全 script の LateUpdate 後、描画コマンド発行の前。
            // (Application.onBeforeRender は batchmode の Play では呼ばれなかった)
            children.Insert(0, update
                ? new PlayerLoopSystem { type = typeof(UpdateStartMarker), updateDelegate = OnUpdateStart }
                : new PlayerLoopSystem { type = typeof(RenderStartMarker), updateDelegate = OnBeforeRender });
            systems[i].subSystemList = children.ToArray();
            if (update) inserted = true; else insertedRender = true;
        }
        if (!inserted || !insertedRender) throw new InvalidOperationException("PlayerLoop に Update / PostLateUpdate がありません");
        loop.subSystemList = systems;
        PlayerLoop.SetPlayerLoop(loop);
        latencyMeasuring = true;
    }

    static void StopLatency()
    {
        latencyMeasuring = false;
        if (originalPlayerLoop.HasValue) PlayerLoop.SetPlayerLoop(originalPlayerLoop.Value);
        originalPlayerLoop = null;
    }

    static void OnUpdateStart()
    {
        if (!latencyMeasuring) return;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (!double.IsNaN(updateStartSeconds) && frameCount > 0)
        {
            if (frameCount - 1 < Capacity) frameIntervalMs[frameCount - 1] = (now - updateStartSeconds) * 1000;
        }
        updateStartSeconds = now;
    }

    static void OnBeforeRender()
    {
        if (!latencyMeasuring || double.IsNaN(updateStartSeconds)) return;
        double now = SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);
        if (frameCount >= Capacity) { latencyOverflow = true; return; }
        sampleToRenderMs[frameCount++] = (now - updateStartSeconds) * 1000;
        var input = InputPoint.Instance;
        if (input == null) return;
        double stamp = input.LastReceivedMonotonicTime;
        // 新しい赤の受信値をこのフレームで初めて取り込んだときだけ数える（間引かれた packet は含めない）。
        if (stamp == lastAppliedStamp || double.IsInfinity(stamp)) return;
        lastAppliedStamp = stamp;
        if (appliedCount >= Capacity) { latencyOverflow = true; return; }
        receiveToSampleMs[appliedCount] = (updateStartSeconds - stamp) * 1000;
        receiveToRenderMs[appliedCount++] = (now - stamp) * 1000;
    }

    static List<Allocator> AllocationMarkers(int first, int last)
    {
        var totals = new List<Allocator>();
        // Editor の保持フレームだけを解析する。全10秒の順位とは限らないことを範囲で示す。
        for (int frame = Math.Max(first, ProfilerDriver.firstFrameIndex); frame <= last; frame++)
        {
            for (int thread = 0; ; thread++)
            {
                using (RawFrameDataView view = ProfilerDriver.GetRawFrameDataView(frame, thread))
                {
                    if (!view.valid) break;
                    var parents = new List<int>();
                    for (int sample = 0; sample < view.sampleCount; sample++)
                    {
                        while (parents.Count > 0 && parents[parents.Count - 1] +
                            view.GetSampleChildrenCountRecursive(parents[parents.Count - 1]) < sample)
                            parents.RemoveAt(parents.Count - 1);
                        if (view.GetSampleName(sample) == "GC.Alloc" && view.GetSampleMetadataCount(sample) > 0)
                        {
                            string marker = view.threadName + ": " +
                                (parents.Count > 0 ? view.GetSampleName(parents[parents.Count - 1]) : "(root)");
                            Allocator item = totals.Find(x => x.marker == marker);
                            if (item == null) { item = new Allocator { marker = marker }; totals.Add(item); }
                            item.bytes += view.GetSampleMetadataAsLong(sample, 0);
                            item.calls++;
                        }
                        if (view.GetSampleChildrenCount(sample) > 0) parents.Add(sample);
                    }
                }
            }
        }
        totals.Sort((a, b) => { int order = b.bytes.CompareTo(a.bytes); return order != 0 ? order : string.CompareOrdinal(a.marker, b.marker); });
        if (totals.Count > 20) totals.RemoveRange(20, totals.Count - 20);
        return totals;
    }

    static void Fail(string reason)
    {
        if (!SessionState.GetBool(Key, false)) return;
        Debug.LogError("[PhoneSaberFrameCost] FAIL " + reason);
        Cleanup();
        EditorApplication.Exit(1);
    }

    static void Cleanup()
    {
        SessionState.SetBool(Key, false);
        StopLatency();
        sender?.Dispose(); sender = null;
        gc.Dispose(); mainThread.Dispose(); playerLoop.Dispose();
        Profiler.enabled = false; Profiler.enableBinaryLog = false; Profiler.enableAllocationCallstacks = false;
        if (profileSettingsChanged) ProfilerDriver.profileEditor = previousProfileEditor;
        profileSettingsChanged = false;
    }

    sealed class SyntheticSender : IDisposable
    {
        readonly byte[][] red = new byte[120][], blue = new byte[120][];
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Stopwatch clock = new Stopwatch();
        readonly Thread thread;
        int sentPairs;
        double maxLateMs;
        public string Error { get; private set; }
        public int SentPairs => Volatile.Read(ref sentPairs);
        public double ElapsedSeconds => clock.Elapsed.TotalSeconds;
        public double MaxLateMs => Volatile.Read(ref maxLateMs);

        public SyntheticSender()
        {
            // 2秒周期の動く棒。payload は既存の x1,y1,x2,y2（pixel、ASCII）形式。
            // 文字列・bytes の生成は計測前だけ。sender 自身のフレーム割当を抑える。
            for (int i = 0; i < red.Length; i++)
            {
                int x = 640 + (int)(240 * Math.Sin(i * Math.PI / 60));
                red[i] = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0},260,{1},460", x - 70, x + 70));
                blue[i] = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0},460,{1},260", 1280 - x - 70, 1280 - x + 70));
            }
            thread = new Thread(Send) { IsBackground = true, Name = "PhoneSaber synthetic 60Hz", Priority = System.Threading.ThreadPriority.AboveNormal };
        }

        public void Start() { clock.Start(); thread.Start(); }
        void Send()
        {
            try
            {
                using (var socket = new UdpClient(AddressFamily.InterNetwork))
                {
                    var redAddress = new IPEndPoint(IPAddress.Loopback, 5005);
                    var blueAddress = new IPEndPoint(IPAddress.Loopback, 5006);
                    double next = clock.Elapsed.TotalSeconds;
                    int i = 0;
                    while (!stop.WaitOne(0))
                    {
                        double now = clock.Elapsed.TotalSeconds;
                        if (now < next) { if (stop.WaitOne(1)) break; continue; }
                        double lateMs = (now - next) * 1000;
                        if (lateMs > maxLateMs) Volatile.Write(ref maxLateMs, lateMs);
                        socket.Send(red[i], red[i].Length, redAddress);
                        socket.Send(blue[i], blue[i].Length, blueAddress);
                        Interlocked.Increment(ref sentPairs);
                        i = (i + 1) % red.Length;
                        next += 1.0 / 60;
                        // 負荷で遅れた場合も追い付き burst を送らない。実測Hz/遅延をreportへ残す。
                        if (now - next > 1.0 / 60) next = now + 1.0 / 60;
                    }
                }
            }
            catch (Exception exception) { Error = exception.ToString(); }
        }
        public void Dispose() { stop.Set(); thread.Join(2000); clock.Stop(); stop.Dispose(); }
    }
}
