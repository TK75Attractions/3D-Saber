using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 打ち込みの遅れの集計。中央値とばらつき(中央絶対偏差×1.4826)で、外れた打鍵に引きずられないようにする。
    /// StepMania はばらつきが30ms未満のときだけ補正を適用する。それにならう。
    /// </summary>
    public static class SaberChartLatency
    {
        public const int MinHits = 8;
        public const float MaxSpreadMs = 30f;
        // 「遅れを測る」のクリック。録音のカウントインと同じ音の経路で鳴らす。
        public const float CalibrationBpm = 100f;
        public const int LeadInBeats = 4;
        public const int MeasuredBeats = 16;
        public const float CalibrationWindowMs = 250f;
        public const int MinCalibrationHits = 10;

        public readonly struct Summary
        {
            public readonly int Count;
            public readonly float Median;
            public readonly float Spread;
            public readonly int Outliers;
            public readonly bool Reliable;

            public Summary(int count, float median, float spread, int outliers, bool reliable)
            {
                Count = count;
                Median = median;
                Spread = spread;
                Outliers = outliers;
                Reliable = reliable;
            }
        }

        public static Summary Summarize(IEnumerable<float> deviations, int minHits = MinHits)
        {
            List<float> values = deviations?.Where(v => !float.IsNaN(v) && !float.IsInfinity(v)).OrderBy(v => v).ToList()
                                 ?? new List<float>();
            if (values.Count == 0) return new Summary(0, 0f, 0f, 0, false);
            float median = Median(values);
            List<float> distances = values.Select(v => Mathf.Abs(v - median)).OrderBy(v => v).ToList();
            float spread = Median(distances) * 1.4826f;
            float limit = Mathf.Max(60f, spread * 3f);
            int outliers = distances.Count(distance => distance > limit);
            bool reliable = values.Count >= minHits && spread <= MaxSpreadMs;
            return new Summary(values.Count, median, spread, outliers, reliable);
        }

        // 並べ替え済みの値の中央値。
        private static float Median(List<float> sorted)
        {
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) * .5f;
        }

        public static float ClickTimeMs(int beat) => beat * 60000f / CalibrationBpm;

        /// <summary>
        /// クリックに合わせて叩いた時刻(クリック音の先頭からの ms)から遅れを測る。
        /// 前置きの4拍は数えず、いちばん近いクリックから ±250ms の打鍵だけを使う。
        /// </summary>
        public static Summary Measure(IEnumerable<float> tapTimesMs)
        {
            var deviations = new List<float>();
            if (tapTimesMs != null)
                foreach (float tap in tapTimesMs)
                {
                    int nearest = Mathf.RoundToInt(tap * CalibrationBpm / 60000f);
                    if (nearest < LeadInBeats || nearest >= LeadInBeats + MeasuredBeats) continue;
                    float deviation = tap - ClickTimeMs(nearest);
                    if (Mathf.Abs(deviation) <= CalibrationWindowMs) deviations.Add(deviation);
                }
            return Summarize(deviations, MinCalibrationHits);
        }
    }

    /// <summary>クリック音を作る・混ぜる。カウントイン、遅れの測定、ヒット音とメトロノームで共通に使う。</summary>
    public static class SaberChartClicks
    {
        public const float HitPitch = 1760f;
        public const float BeatPitch = 1000f;
        public const float AccentPitch = 1500f;

        public static void AddClick(float[] data, int channels, int frequency, double atSeconds, float pitchHz, float gain,
            float durationSeconds = .03f)
        {
            if (data == null || channels <= 0 || frequency <= 0 || atSeconds < 0) return;
            int start = (int)Math.Round(atSeconds * frequency);
            int length = Mathf.Max(1, Mathf.RoundToInt(durationSeconds * frequency));
            int frames = data.Length / channels;
            for (int n = 0; n < length && start + n < frames; n++)
            {
                if (start + n < 0) continue;
                float envelope = 1f - n / (float)length;
                float sample = gain * Mathf.Sin(n * 2f * Mathf.PI * pitchHz / frequency) * envelope * envelope;
                int index = (start + n) * channels;
                for (int channel = 0; channel < channels; channel++)
                    data[index + channel] = Mathf.Clamp(data[index + channel] + sample, -1f, 1f);
            }
        }

        /// <summary>前置き4拍(低い音)と測る16拍(高い音)のクリック。最後に1拍の余白を付ける。</summary>
        public static AudioClip CalibrationClip()
        {
            const int rate = 22050;
            int beats = SaberChartLatency.LeadInBeats + SaberChartLatency.MeasuredBeats + 1;
            var samples = new float[Mathf.CeilToInt(SaberChartLatency.ClickTimeMs(beats) / 1000f * rate)];
            for (int beat = 0; beat < SaberChartLatency.LeadInBeats + SaberChartLatency.MeasuredBeats; beat++)
            {
                bool lead = beat < SaberChartLatency.LeadInBeats;
                AddClick(samples, 1, rate, SaberChartLatency.ClickTimeMs(beat) / 1000.0, lead ? 700f : 1300f, lead ? .22f : .3f);
            }
            AudioClip clip = AudioClip.Create("譜面エディターの遅れ測定", samples.Length, 1, rate, false);
            clip.hideFlags = HideFlags.HideAndDontSave;
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// 曲にクリックを混ぜた一時的な音源。曲と同じ経路で鳴るので、ずれを耳で正しく判断できる。
        /// 波形を読めない圧縮設定の音源では null を返す(そのときは曲だけを鳴らす)。
        /// </summary>
        public static AudioClip Mix(AudioClip song, IEnumerable<double> hitSeconds, IEnumerable<(double seconds, bool accent)> beatSeconds)
        {
            if (song == null || song.samples <= 0 || song.channels <= 0) return null;
            var data = new float[song.samples * song.channels];
            try
            {
                if (!song.GetData(data, 0)) return null;
            }
            catch (Exception)
            {
                return null;
            }
            if (beatSeconds != null)
                foreach (var beat in beatSeconds)
                    AddClick(data, song.channels, song.frequency, beat.seconds, beat.accent ? AccentPitch : BeatPitch, beat.accent ? .32f : .22f, .02f);
            if (hitSeconds != null)
                foreach (double hit in hitSeconds)
                    AddClick(data, song.channels, song.frequency, hit, HitPitch, .38f, .025f);
            AudioClip clip = AudioClip.Create(song.name + " + クリック", song.samples, song.channels, song.frequency, false);
            clip.hideFlags = HideFlags.HideAndDontSave;
            clip.SetData(data, 0);
            return clip;
        }
    }
}
