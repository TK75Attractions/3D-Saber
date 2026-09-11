using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 判定調整の測定条件。通常プレイの判定窓・カット形状は変更しない。
public static class CalibrationProtocol
{
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

    // クリックを全てサンプル列に事前配置し、一度だけ PlayScheduled で再生する。
    // 画面更新の遅れで基準音の間隔が揺れない。出力機器自体の遅延を測るものではない。
    public static float[] ClickSamples(int sampleRate)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var samples = new float[(int)Math.Ceiling((EndSeconds + .2) * sampleRate)];
        for (int beat = 1; beat <= 32; beat++)
        {
            int start = (int)Math.Round(beat * BeatSeconds * sampleRate);
            int length = (int)(sampleRate * .035);
            double hz = beat < 5 ? 740 : ((beat - 5) % 4 == 0 ? 1320 : 1040);
            for (int j = 0; j < length && start + j < samples.Length; j++)
            {
                double t = j / (double)sampleRate;
                double attack = Math.Min(1, j / (sampleRate * .001));
                samples[start + j] = (float)(.38 * attack * Math.Exp(-t * 120) *
                    (Math.Sin(2 * Math.PI * hz * t) + .25 * Math.Sin(2 * Math.PI * hz * 2.1 * t)));
            }
        }
        return samples;
    }
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
    public int Profile { get; private set; }
    public int OffsetMs => values[Profile];
    public int SavedOffsetMs => originals[Profile];
    public bool HasSavedProfile => previouslySaved[Profile];
    public bool IsDirty => Profile != originalProfile || values[0] != originals[0] || values[1] != originals[1];
    public string ProfileName => Profile == 0 ? "PCスピーカー" : "有線イヤホン";
    public CalibrationDraft()
    {
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
    public void RestoreSaved() => values[Profile] = originals[Profile];
    public void Commit()
    {
        PlayerPrefs.SetInt(SpeakerKey, values[0]);
        PlayerPrefs.SetInt(HeadphoneKey, values[1]);
        PlayerPrefs.SetInt(ActiveKey, Profile);
        GameSession.JudgmentOffsetMs = values[Profile];
        originalProfile = Profile;
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
        // 「中央」は ±8ms の目安であり、本番の PERFECT 幅とは別。
        r.Early = samples.Count(s => s.ErrorMs < -8); r.Late = samples.Count(s => s.ErrorMs > 8);
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
