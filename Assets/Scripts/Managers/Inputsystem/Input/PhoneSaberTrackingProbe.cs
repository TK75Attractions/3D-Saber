using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// 追従の実測（無人）。画面の赤い棒を決まった軌道で動かし、スマホのカメラ経由で戻ってきた座標を
// 時刻つきで保存する。棒の本当の位置が毎フレーム分かるので、あとから「遅れ」と「先読み補正の誤差」を
// 実データで計算できる（PhoneSaber/tools/tracking_report.py）。
// -phonesaberTrackingProbe <秒> を付けて起動したときだけ働く。Editor と通常起動では何もしない。
// 結果は persistentDataPath/PhoneSaber/tracking-<UTC>.csv。終わったらアプリを終了する。
public sealed class PhoneSaberTrackingProbe : MonoBehaviour
{
    public const string Argument = "-phonesaberTrackingProbe";
    public const float DefaultSeconds = 60f;
    public const double StartDelaySeconds = 8, SegmentSeconds = 8;
    // 区間ごとの往復の速さ（Hz）。0 は停止。振りの速さに近い範囲と、静止時の揺れを見る区間を含む。
    static readonly double[] Frequencies = { 0.5, 1.0, 0.0, 1.5, 2.0, 0.75, 0.0, 2.5 };
    public const double Amplitude = 0.3;
    double duration, startedAt = -1, lastPacket = double.NegativeInfinity;
    readonly StringBuilder rows = new StringBuilder(1 << 16);
    int frames, packets;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CreateAtStartup()
    {
        if (Application.isEditor) return;
        float seconds = ResolveSeconds(Environment.GetCommandLineArgs());
        if (seconds <= 0f) return;
        var probe = new GameObject("PhoneSaber Tracking Probe").AddComponent<PhoneSaberTrackingProbe>();
        probe.duration = seconds;
        PhoneSaberDisplaySync.Measuring = true; // 終わればアプリごと終了する
        DontDestroyOnLoad(probe.gameObject);
    }

    // 引数が無ければ 0（働かない）。秒数が無い・読めない・範囲外なら既定値。
    public static float ResolveSeconds(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] != Argument) continue;
            if (i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float seconds) && seconds >= 10f && seconds <= 600f) return seconds;
            return DefaultSeconds;
        }
        return 0f;
    }

    // 棒の中心（画面幅に対する 0..1）。t は開始からの秒。区間の境目で位置も速さも飛ばない（位相を積み上げる）。
    public static double TargetX(double t)
    {
        if (t < 0) t = 0;
        double phase = 0;
        int whole = (int)Math.Floor(t / SegmentSeconds);
        for (int i = 0; i < whole; i++) phase += 2 * Math.PI * Frequencies[i % Frequencies.Length] * SegmentSeconds;
        phase += 2 * Math.PI * Frequencies[whole % Frequencies.Length] * (t - whole * SegmentSeconds);
        return 0.5 + Amplitude * Math.Sin(phase);
    }

    static double Now => SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp);

    void Update()
    {
        double now = Now;
        if (startedAt < 0)
        {
            if (Time.realtimeSinceStartupAsDouble < StartDelaySeconds) return;
            startedAt = now;
            rows.Append("# ").Append(PhoneSaberPerfLog.DisplayConfiguration()).Append('\n');
            rows.Append("# F,time,targetX  R,receiveTime,ax,ay,bx,by (normalized)  amplitude=")
                .Append(Amplitude.ToString(CultureInfo.InvariantCulture)).Append(" segment-s=")
                .Append(SegmentSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        var input = InputPoint.Instance;
        if (input != null && input.HasValidStickEndpoints && input.LastReceivedMonotonicTime > lastPacket)
        {
            lastPacket = input.LastReceivedMonotonicTime;
            packets++;
            rows.Append("R,").Append((lastPacket - startedAt).ToString("0.000000", CultureInfo.InvariantCulture)).Append(',')
                .Append(input.LocalStickA.x.ToString("0.00000", CultureInfo.InvariantCulture)).Append(',')
                .Append(input.LocalStickA.y.ToString("0.00000", CultureInfo.InvariantCulture)).Append(',')
                .Append(input.LocalStickB.x.ToString("0.00000", CultureInfo.InvariantCulture)).Append(',')
                .Append(input.LocalStickB.y.ToString("0.00000", CultureInfo.InvariantCulture)).Append('\n');
        }
        if (now - startedAt >= duration) Finish();
    }

    void OnGUI()
    {
        if (startedAt < 0 || Event.current.type != EventType.Repaint) return;
        double t = Now - startedAt;
        double x = TargetX(t);
        frames++;
        rows.Append("F,").Append(t.ToString("0.000000", CultureInfo.InvariantCulture)).Append(',')
            .Append(x.ToString("0.00000", CultureInfo.InvariantCulture)).Append('\n');
        int previousDepth = GUI.depth;
        Color previousColour = GUI.color;
        GUI.depth = -20000;
        float w = Screen.width, h = Screen.height;
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
        GUI.color = Color.red;
        float barWidth = w * 0.06f;
        GUI.DrawTexture(new Rect((float)x * w - barWidth * 0.5f, h * 0.2f, barWidth, h * 0.6f), Texture2D.whiteTexture);
        GUI.depth = previousDepth;
        GUI.color = previousColour;
    }

    void Finish()
    {
        enabled = false;
        string path = "";
        try
        {
            string folder = Path.Combine(Application.persistentDataPath, "PhoneSaber");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "tracking-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
            File.WriteAllText(path, rows.ToString());
        }
        catch (Exception e) { Debug.Log("[PhoneSaberTracking] write failed: " + e.Message); path = ""; }
        PhoneSaberEventLog.Record(PhoneSaberStation.Read(), "RED", "tracking-probe",
            "frames=" + frames + " packets=" + packets + " seconds=" + duration.ToString("0", CultureInfo.InvariantCulture) +
            " file=" + (path.Length > 0 ? Path.GetFileName(path) : "none"));
        PhoneSaberEventLog.Current?.Flush();
        Application.Quit();
    }
}
