using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 判定調整の測定条件。通常プレイの判定窓・カット形状は変更しない。
public static class CalibrationProtocol
{
    // 本番と同じPERFECT幅(早め50ms / 遅め90ms)。調整画面も早め側の厳しい非対称窓に合わせる。
    public const double EarlyPerfectMs = JudgmentTierHelper.EarlyPerfectSeconds * 1000;
    public const double LatePerfectMs = JudgmentTierHelper.LatePerfectSeconds * 1000;
    public static string LiveFeedback(double errorMs) => errorMs < -EarlyPerfectMs ? "少し早め" : errorMs > LatePerfectMs ? "少し遅め" : "音に合っています";
    public const float Bpm = 100f;
    public const double BeatSeconds = .6;
    public const double FirstNoteSeconds = 3.0;
    public const int WarmupNotes = 4;
    public const int MeasuredNotes = 24;
    public const int TotalNotes = WarmupNotes + MeasuredNotes;
    public const double EndSeconds = FirstNoteSeconds + (TotalNotes - 1) * BeatSeconds + 1.5;
    public static double NoteTime(int index) => FirstNoteSeconds + index * BeatSeconds;
    public static ChartData CreateChart()
    {
        var chart = new ChartData { bpm = Bpm, offsetMs = 0f };
        for (int i = 0; i < TotalNotes; i++)
            chart.notes.Add(new NoteData { time = (float)(NoteTime(i) * 1000), x = i % 2 == 0 ? -1.65f : 1.65f,
                y = .5f, color = i % 2 == 0 ? "blue" : "red", type = "tap", direction = "none", count = 1 });
        return chart;
    }

    // クリックの形(2026-10-24 ユーザー「判定調整の音が他より小さい」)。曲は −18 LUFS にそろえて音量1で鳴らすので、
    // クリック列も音量1で約 −18.7 LUFS になるよう、振幅 .76・長さ 0.1 秒・減衰 30/秒にした
    // (旧: .38・0.035 秒・120/秒を音量 .65 で鳴らして −35 LUFS)。立ち上がりは今までどおり 1ms で、拍の位置は聞き取りやすいまま。
    public const double ClickAmplitude = .76, ClickSeconds = .1, ClickDecayPerSecond = 30;

    static void AddClick(float[] samples, int start, int sampleRate, double hz)
    {
        int length = (int)(sampleRate * ClickSeconds);
        for (int j = 0; j < length && start + j < samples.Length; j++)
        {
            double t = j / (double)sampleRate;
            double attack = Math.Min(1, j / (sampleRate * .001));
            samples[start + j] = (float)(ClickAmplitude * attack * Math.Exp(-t * ClickDecayPerSecond) *
                (Math.Sin(2 * Math.PI * hz * t) + .25 * Math.Sin(2 * Math.PI * hz * 2.1 * t)));
        }
    }

    // ライブ調整は4拍の短い音源を連続ループ。ノーツの時刻はDSP時計に合わせて先読みする。
    public static float[] LiveClickSamples(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var samples = new float[(int)Math.Round(4 * BeatSeconds * sampleRate)];
        for (int beat = 0; beat < 4; beat++)
            AddClick(samples, (int)Math.Round(beat * BeatSeconds * sampleRate), sampleRate, 1040);
        return samples;
    }

    public static NoteData LiveNote(int index) => new NoteData
    {
        time = (float)(NoteTime(index) * 1000), x = index % 2 == 0 ? -1.65f : 1.65f,
        y = .5f, color = index % 2 == 0 ? "blue" : "red", type = "tap", direction = "none", count = 1
    };

    // クリックを全てサンプル列に事前配置し、一度だけ PlayScheduled で再生する。
    // 画面更新の遅れで基準音の間隔が揺れない。出力機器自体の遅延を測るものではない。
    public static float[] ClickSamples(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var samples = new float[(int)Math.Ceiling((EndSeconds + .2) * sampleRate)];
        for (int beat = 1; beat <= 32; beat++)
            AddClick(samples, (int)Math.Round(beat * BeatSeconds * sampleRate), sampleRate,
                beat < 5 ? 740 : ((beat - 5) % 4 == 0 ? 1320 : 1040));
        return samples;
    }
}

// 判定調整画面のノーツ速度(到着までの秒数)。2026-09-23 ユーザー指定: 1段目 2.0秒 / 2段目 1.0秒 / 3段目 0.5秒。
// 10-22 に3段目(はやい)を 0.8秒へ(ユーザー指定)。
// 値は GameSession.NoteApproachTime と同じ意味(小さいほど速い)。本番のプレイと小節線にも同じ値が使われる。
public static class NoteSpeedPreset
{
    public static readonly float[] Seconds = { 2f, 1f, .8f };
    public static readonly string[] Names = { "ゆっくり", "ふつう", "はやい" };
    public static int Count => Seconds.Length;
    // 保存値が段の値と一致しない場合(旧ウィジェット等)は最も近い段として表示する。
    public static int StepFor(float approach)
    {
        int best = 0;
        for (int i = 1; i < Seconds.Length; i++)
            if (Mathf.Abs(Seconds[i] - approach) < Mathf.Abs(Seconds[best] - approach)) best = i;
        return best;
    }
    public static float SecondsFor(int step) => Seconds[Mathf.Clamp(step, 0, Seconds.Length - 1)];
    public static string Caption(int step, bool selected) =>
        (selected ? "●  " : "") + (step + 1) + "  " + Names[step] + "  " + Seconds[step].ToString("0.0") + "秒";
}

// 未保存の編集を PlayerPrefs から分離。音の出力先の切替は利用者が明示して行う。
public sealed class CalibrationDraft
{
    public const string ActiveKey = "calibration.outputProfile.v1";
    public const string SpeakerKey = "calibration.speakerOffset.v1";
    public const string HeadphoneKey = "calibration.headphoneOffset.v1";
    readonly int[] values = new int[2];
    readonly int[] originals = new int[2];
    readonly bool[] previouslySaved = new bool[2];
    int originalProfile;
    // ノーツ速度も下書きに含め、「保存して戻る」で確定、「保存せずに戻る」で破棄する(判定値と同じ扱い)。
    float approach, originalApproach;
    public int Profile { get; private set; }
    public int OffsetMs => values[Profile];
    public int SavedOffsetMs => originals[Profile];
    public bool HasSavedProfile => previouslySaved[Profile];
    public float ApproachTime => approach;
    public int SpeedStep => NoteSpeedPreset.StepFor(approach);
    public bool IsDirty => Profile != originalProfile || values[0] != originals[0] || values[1] != originals[1]
        || !Mathf.Approximately(approach, originalApproach);
    public string ProfileName => Profile == 0 ? "PCスピーカー" : "有線イヤホン";
    public CalibrationDraft()
    {
        approach = originalApproach = GameSession.NoteApproachTime;
        Profile = originalProfile = Mathf.Clamp(PlayerPrefs.GetInt(ActiveKey, 0), 0, 1);
        int current = GameSession.JudgmentOffsetMs;
        values[0] = PlayerPrefs.GetInt(SpeakerKey, current);
        values[1] = PlayerPrefs.GetInt(HeadphoneKey, current);
        // 旧ウィジェット等で後から変更された現在値を優先し、既存の設定を失わない。
        values[Profile] = current;
        for (int i = 0; i < 2; i++)
        {
            values[i] = Clamp(values[i]); originals[i] = values[i];
            previouslySaved[i] = PlayerPrefs.HasKey(i == 0 ? SpeakerKey : HeadphoneKey) || i == originalProfile;
        }
    }
    public void SelectProfile(int profile) => Profile = Mathf.Clamp(profile, 0, 1);
    public void SetOffset(int ms) => values[Profile] = Clamp(ms);
    public void SetSpeedStep(int step) => approach = NoteSpeedPreset.SecondsFor(step);
    public void RestoreSaved() { values[Profile] = originals[Profile]; approach = originalApproach; }
    public void Commit()
    {
        PlayerPrefs.SetInt(SpeakerKey, values[0]);
        PlayerPrefs.SetInt(HeadphoneKey, values[1]);
        PlayerPrefs.SetInt(ActiveKey, Profile);
        GameSession.JudgmentOffsetMs = values[Profile];
        GameSession.NoteApproachTime = approach;
        originalProfile = Profile; originalApproach = approach;
        for (int i = 0; i < 2; i++) { originals[i] = values[i]; previouslySaved[i] = true; }
    }
    static int Clamp(int ms) => Mathf.Clamp(ms, GameSession.JudgmentOffsetMinMs, GameSession.JudgmentOffsetMaxMs);
    public static string FormatMs(double value) => value.ToString("+0;-0;0") + " ms";
    public static string Explain(int value) => value == 0 ? "追加の時間ずらしなし" :
        $"音の基準に対して {Math.Abs(value)} ミリ秒、ノーツと判定を{(value > 0 ? "遅らせます" : "早めます")}";
}

public struct CalibrationSample
{
    public int Index;
    public double ErrorMs;
    public SaberHand Hand;
    public CalibrationSample(int index, double errorMs, SaberHand hand) { Index = index; ErrorMs = errorMs; Hand = hand; }
}

// 直近のカットだけを保持する。見逃しを0msとして混ぜず、設定変更時に一緒に消す。
// ライブの傾向は短い窓で案内し、専用測定の24ノーツ集計とは分ける。
public sealed class CalibrationHistory
{
    readonly List<CalibrationSample> samples = new List<CalibrationSample>();
    readonly Queue<bool> recentInputs = new Queue<bool>();
    public CalibrationResult Summary { get; private set; }
    public int Misses { get; private set; }
    public int Count => samples.Count;
    public void Clear() { samples.Clear(); recentInputs.Clear(); Summary = null; Misses = 0; }
    void RecordInput(bool hit) { recentInputs.Enqueue(hit); if (recentInputs.Count > 12) recentInputs.Dequeue(); }
    public void RecordMiss() { Misses++; RecordInput(false); }
    public void Add(double errorMs, SaberHand hand)
    {
        if (double.IsNaN(errorMs) || double.IsInfinity(errorMs)) return;
        RecordInput(true);
        if (samples.Count == CalibrationProtocol.MeasuredNotes) samples.RemoveAt(0);
        samples.Add(new CalibrationSample(0, errorMs, hand));
        for (int i = 0; i < samples.Count; i++)
            samples[i] = new CalibrationSample(i, samples[i].ErrorMs, samples[i].Hand);
        Summary = CalibrationResult.Analyze(samples, 0);
        Summary.CanRecommend = false;
    }
    public bool IsConsistent => Count >= 6 && Summary.Accepted >= Math.Ceiling(Count * .8) &&
        Summary.SpreadMs <= 25 && Summary.LeftCount >= 2 && Summary.RightCount >= 2 &&
        Math.Abs(Summary.LeftMs - Summary.RightMs) <= 35 &&
        recentInputs.Count(hit => !hit) * 4 <= recentInputs.Count;
    // 単発の揺れでは方向を勧めない。PERFECT内なら調整を促さない。
    public int SuggestedDirection => !IsConsistent ? 0 :
        Summary.Early >= Math.Ceiling(Count * .7) ? -1 : Summary.Late >= Math.Ceiling(Count * .7) ? 1 : 0;
    public int RecommendedDelta => SuggestedDirection != 0 && Math.Abs(Summary.MedianMs) >= 60 &&
        Summary.MedianMs - 3 * Summary.SpreadMs > -JudgmentTierHelper.EarlyBadSeconds * 1000 + 15 &&
        Summary.MedianMs + 3 * Summary.SpreadMs < JudgmentTierHelper.LateBadSeconds * 1000 - 15 &&
        !samples.Any(s => s.Hand == SaberHand.Any) ? (int)Math.Round(Summary.MedianMs, MidpointRounding.AwayFromZero) : 0;
    public string Hint
    {
        get
        {
            if (Count < 6) return "";
            if (Summary.LeftCount < 2 || Summary.RightCount < 2) return "両手で切ってみよう";
            if (Math.Abs(Summary.LeftMs - Summary.RightMs) > 35) return "左右の振りをそろえよう";
            if (!IsConsistent) return "同じリズムで続けよう";
            if (SuggestedDirection < 0) return "少し早め";
            if (SuggestedDirection > 0) return "少し遅め";
            return Summary.Center >= Math.Ceiling(Count * .7) ? "音に合っています" : "もう少し続けよう";
        }
    }
}

public sealed class CalibrationResult
{
    public int Captured, Accepted, Excluded, Missing, Early, Center, Late, LeftCount, RightCount;
    public double MedianMs, SpreadMs, LeftMs, RightMs, DriftMs;
    public int ProposedOffsetMs;
    public bool CanRecommend;
    public string Message;
    public CalibrationSample[] Samples = Array.Empty<CalibrationSample>();

    // 中央値と MAD を用いる控えめな提案。未入力を 0ms に置き換えず、判定窓による
    // 取りこぼし・左右差・前半後半差・処理落ちがある測定では自動提案を出さない。
    public static CalibrationResult Analyze(IEnumerable<CalibrationSample> source, int baseOffsetMs,
        bool interrupted = false, int slowFrames = 0)
    {
        var r = new CalibrationResult { ProposedOffsetMs = baseOffsetMs };
        var samples = source.Where(s => s.Index >= 0 && s.Index < CalibrationProtocol.MeasuredNotes &&
            !double.IsNaN(s.ErrorMs) && !double.IsInfinity(s.ErrorMs)).GroupBy(s => s.Index).Select(g => g.First())
            .OrderBy(s => s.Index).ToArray();
        r.Samples = samples; r.Captured = samples.Length; r.Missing = CalibrationProtocol.MeasuredNotes - r.Captured;
        if (samples.Length == 0) { r.Message = "測定できませんでした。2本のセーバーの接続と、切る位置を確認してください。"; return r; }
        double median = Median(samples.Select(s => s.ErrorMs));
        double mad = Median(samples.Select(s => Math.Abs(s.ErrorMs - median)));
        double threshold = Math.Max(15, 3 * 1.4826 * mad);
        var kept = samples.Where(s => Math.Abs(s.ErrorMs - median) <= threshold).ToArray();
        r.Accepted = kept.Length; r.Excluded = samples.Length - kept.Length;
        r.MedianMs = Median(kept.Select(s => s.ErrorMs));
        r.SpreadMs = 1.4826 * Median(kept.Select(s => Math.Abs(s.ErrorMs - r.MedianMs)));
        var left = kept.Where(s => s.Hand == SaberHand.Left).ToArray();
        var right = kept.Where(s => s.Hand == SaberHand.Right).ToArray();
        r.LeftCount = left.Length; r.RightCount = right.Length;
        r.LeftMs = Median(left.Select(s => s.ErrorMs)); r.RightMs = Median(right.Select(s => s.ErrorMs));
        r.DriftMs = Math.Abs(Median(kept.Where(s => s.Index < 12).Select(s => s.ErrorMs)) -
            Median(kept.Where(s => s.Index >= 12).Select(s => s.ErrorMs)));
        r.Early = samples.Count(s => s.ErrorMs < -CalibrationProtocol.EarlyPerfectMs);
        r.Late = samples.Count(s => s.ErrorMs > CalibrationProtocol.LatePerfectMs);
        r.Center = samples.Length - r.Early - r.Late;
        if (interrupted) r.Message = "測定中に接続・音声設定・画面の状態が変わりました。環境を固定して測り直してください。";
        else if (slowFrames > 0) r.Message = "測定中に画面の大きな処理落ちがありました。ほかのアプリを閉じて測り直してください。";
        else if (samples.Any(s => s.Hand == SaberHand.Any)) r.Message = "マウス等の入力が含まれています。実機セーバー2本で測り直してください。";
        else if (r.Accepted < 22 || r.LeftCount < 10 || r.RightCount < 10)
            r.Message = "有効なカットが不足しています。音に合わせて左右のノーツを切ってください。大きくずれる場合は先に手動調整を。";
        else if (Math.Abs(r.LeftMs - r.RightMs) > 35) r.Message = "左右で切るタイミングに差があります。全体の値を変える前に、左右の持ち方と追跡を確認してください。";
        else if (r.SpreadMs > 25 || r.DriftMs > 25) r.Message = "リズムにばらつきがあります。力を抜いて一定の振り幅でもう一度。今の値は変えません。";
        else if (r.MedianMs - 3 * r.SpreadMs <= -JudgmentTierHelper.EarlyBadSeconds * 1000 + 15 ||
                 r.MedianMs + 3 * r.SpreadMs >= JudgmentTierHelper.LateBadSeconds * 1000 - 15)
            r.Message = "判定できる範囲の端に偏っています。手動で少し調整してから測り直してください。";
        else
        {
            int delta = Math.Abs(r.MedianMs) <= 8 ? 0 : (int)Math.Round(r.MedianMs, MidpointRounding.AwayFromZero);
            int proposed = baseOffsetMs + delta;
            if (proposed < GameSession.JudgmentOffsetMinMs || proposed > GameSession.JudgmentOffsetMaxMs)
                r.Message = "調整範囲を超えます。音の出力先や機器の設定を確認してください。";
            else
            {
                r.CanRecommend = true; r.ProposedOffsetMs = proposed;
                r.Message = delta == 0 ? "今の値でほぼ中央です。無理に変える必要はありません。" :
                    $"{Math.Abs(delta)} ms {(delta > 0 ? "遅らせる" : "早める")}案です。試し切りして、合うと感じた場合だけ保存してください。";
            }
        }
        return r;
    }
    static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        return a.Length == 0 ? 0 : (a[(a.Length - 1) / 2] + a[a.Length / 2]) * .5;
    }
}
