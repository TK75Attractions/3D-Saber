using System;
using System.Collections.Generic;
using UnityEngine;

public enum ReverseCue { Swell, Cue, FlipIn, FlipOut, Shatter, Creak, Rumble, Landing, FinaleSwell, FinaleHit }

// Manic Sonic の裏譜面 R の音。逆シンバル・鏡門の予告音・裏返り・割れる・きしみ・回転のうなり・着地・最後の一打。
// 鳴らす時刻は譜面から前もって決めておき(BuildEvents)、曲の時計で少し前に予約して鳴らす(ChorusDrop の予約と同じやり方)。
// 止まったら取り消し(ResetSounds)、次のコマで今の時刻から予約し直す(途中まで鳴っていた長い音は途中から)。
// 波形と8声は開始時に作る。EditMode では出音の代わりに、実際に声へ渡した再生要求を数える。
public sealed class ReverseSfx : IDisposable
{
    public struct Event
    {
        // 鳴り始める曲の時刻(秒、オフセット込み)。
        public double At;
        public ReverseCue Cue;
        // 波形の頭を飛ばす秒(短い逆シンバルは頭を飛ばして、終わりの頂点を届く時刻にそろえる)。
        public double Skip;
        // 鳴っている長さ(秒)。
        public double Length;
    }

    public const double LookAheadSeconds = .12;
    const int CueKinds = 10;
    readonly List<Event> events;
    readonly GameObject root;
    readonly AudioSource[] voices = new AudioSource[8];
    readonly AudioClip[] clips = new AudioClip[CueKinds];
    readonly double[] clipSeconds = new double[CueKinds];
    readonly int[] played = new int[CueKinds];
    int next, nextVoice;
    double lastTime = double.NaN;
    bool seek = true, disposed;

    public IReadOnlyList<Event> Events => events;
    public int Played(ReverseCue cue) => played[(int)cue];
    public int Next => next;
    // 最後に声へ渡した要求(テスト用)。
    public Event LastPlayed { get; private set; }
    public double LastDelay { get; private set; }

    public ReverseSfx(Transform parent, List<Event> scheduled)
    {
        events = scheduled ?? new List<Event>();
        events.Sort((a, b) => a.At.CompareTo(b.At));
        double rumble = 0, finale = 0;
        foreach (var e in events)
        {
            if (e.Cue == ReverseCue.Rumble) rumble = Math.Max(rumble, e.Length);
            if (e.Cue == ReverseCue.FinaleSwell) finale = Math.Max(finale, e.Length);
        }
        // 曲中に合成して処理落ちしないよう、開始時(幕の閉じている間)に作る。同じ長さの音は作り置きを使う。
        Make(ReverseCue.Swell, "Reverse.Swell", () => FlourishSfx.ReverseCymbal());
        Make(ReverseCue.Cue, "Reverse.Cue", FlourishSfx.MirrorCue);
        Make(ReverseCue.FlipIn, "Reverse.FlipIn", FlourishSfx.MirrorFlipIn);
        Make(ReverseCue.FlipOut, "Reverse.FlipOut", FlourishSfx.MirrorFlipOut);
        Make(ReverseCue.Shatter, "Reverse.Shatter", FlourishSfx.MirrorShatter);
        Make(ReverseCue.Creak, "Reverse.Creak", FlourishSfx.WorldCreak);
        if (rumble > 0) Make(ReverseCue.Rumble, "Reverse.Rumble." + rumble.ToString("F2"), () => FlourishSfx.WorldRumble(rumble));
        Make(ReverseCue.Landing, "Reverse.Landing", FlourishSfx.WorldLanding);
        if (finale > 0) Make(ReverseCue.FinaleSwell, "Reverse.FinaleSwell." + finale.ToString("F2"), () => FlourishSfx.WorldFinaleSwell(finale));
        Make(ReverseCue.FinaleHit, "Reverse.FinaleHit", FlourishSfx.WorldFinaleHit);

        root = new GameObject("ReverseAudio");
        root.transform.SetParent(parent, false);
        for (int i = 0; i < voices.Length; i++)
        {
            voices[i] = root.AddComponent<AudioSource>();
            voices[i].playOnAwake = false;
            voices[i].spatialBlend = 0;
            voices[i].loop = false;
            voices[i].volume = 1f;
        }
    }

    void Make(ReverseCue cue, string name, Func<float[]> build)
    {
        var samples = FlourishSfx.Cached(name, build);
        clipSeconds[(int)cue] = samples.Length / (double)ProceduralSfx.Rate;
        clips[(int)cue] = ProceduralSfx.Clip(name, samples);
        clips[(int)cue].hideFlags = HideFlags.DontSave;
    }

    // 譜面から鳴らす時刻を決める(純関数。offset は NoteSpawner.TotalOffsetSeconds)。拍はテンポ表で秒に直す。
    public static List<Event> BuildEvents(ChartData chart, double offset)
    {
        var result = new List<Event>();
        var data = chart?.reverse;
        if (!ReverseRules.Enabled(data)) return result;
        var tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
        double Seconds(double ms) => ms / 1000.0 + offset;

        // 逆シンバル: swell のノーツ(同時の組は1つ)。長さは2拍(遅い所は 1 秒まで)、届く時刻でいちばん大きい。
        double lastSwell = double.NegativeInfinity;
        if (chart.notes != null)
            foreach (var note in chart.notes)
            {
                if (note == null || !note.swell || Math.Abs(note.time - lastSwell) <= 10) continue;
                lastSwell = note.time;
                double beat = tempo.BeatAt(note.time);
                double beatSeconds = (tempo.TimeAt(beat) - tempo.TimeAt(beat - 1)) / 1000.0;
                double length = ReverseRules.SwellSeconds(beatSeconds);
                result.Add(new Event { At = Seconds(note.time) - length, Cue = ReverseCue.Swell,
                    Skip = FlourishSfx.ReverseCymbalSeconds - length, Length = length });
            }

        // 鏡門: 鏡にする拍の前の3拍に予告音、裏返る瞬間にシャン(鏡)・フォッ(素通し)、割れるときにパリーン。門を通る瞬間には鳴らさない
        // (届く 1.5 拍前で曲の拍からずれ、切る音より早い別のリズムに聞こえるため)。
        foreach (var gate in data.gates ?? new List<ReverseGateData>())
        {
            if (gate == null) continue;
            foreach (var span in gate.on ?? new List<ReverseSpanData>())
            {
                if (span == null) continue;
                foreach (double cue in ReverseRules.CueTimesMs(tempo, span.time))
                    result.Add(new Event { At = Seconds(cue), Cue = ReverseCue.Cue, Length = .09 });
                result.Add(new Event { At = Seconds(span.time), Cue = ReverseCue.FlipIn, Length = .45 });
                if (span.endTime < gate.endTime - .5f)
                    result.Add(new Event { At = Seconds(span.endTime), Cue = ReverseCue.FlipOut, Length = .26 });
            }
            result.Add(new Event { At = Seconds(gate.endTime), Cue = ReverseCue.Shatter, Length = .6 });
        }

        // 天地返し: きしみ、逆さへ回る間のうなりと回り終わりのドン、元へ戻る一回転の逆シンバルと最後の一打。
        foreach (var creak in data.creaks ?? new List<ReverseCreakData>())
            if (creak != null) result.Add(new Event { At = Seconds(creak.time), Cue = ReverseCue.Creak, Length = .32 });
        float previous = 0f;
        foreach (var turn in data.turns ?? new List<ReverseTurnData>())
        {
            if (turn == null) continue;
            double start = Seconds(turn.time), end = Seconds(turn.endTime);
            if (ReverseRules.UpsideDown(turn.degrees) || !ReverseRules.UpsideDown(previous))
            {
                if (end - start >= .5)
                    result.Add(new Event { At = start, Cue = ReverseCue.Rumble, Length = Math.Min(6, end - start) });
                result.Add(new Event { At = end, Cue = ReverseCue.Landing, Length = .45 });
            }
            else
            {
                double hit = Seconds(ReverseRules.FinalHitMs(turn, tempo));
                double swell = Math.Max(.5, Math.Min(3, hit - start));
                result.Add(new Event { At = hit - swell, Cue = ReverseCue.FinaleSwell, Length = swell });
                result.Add(new Event { At = hit, Cue = ReverseCue.FinaleHit, Length = 1.9 });
            }
            previous = turn.degrees;
        }
        result.Sort((a, b) => a.At.CompareTo(b.At));
        return result;
    }

    // 毎コマ(GamePlayManager の曲の時計)。時計が戻った・大きく飛んだ・止めたあとは、今の時刻から予約し直す。
    public void Tick(double songTime)
    {
        if (disposed || !ReverseRules.Finite(songTime)) return;
        if (seek || !ReverseRules.Finite(lastTime) || songTime < lastTime - .05 || songTime - lastTime > .5)
        {
            StopVoices();
            next = 0;
            seek = false;
        }
        lastTime = songTime;
        bool reduced = DisplaySettings.ReducedEffects;
        while (next < events.Count && events[next].At - songTime <= LookAheadSeconds)
        {
            var e = events[next++];
            // もう鳴り終わっている音は鳴らさない。F3 ではきしみを出さない。
            if (e.At + e.Length <= songTime + .03) continue;
            if (reduced && e.Cue == ReverseCue.Creak) continue;
            Play(e, songTime);
        }
    }

    void Play(Event e, double songTime)
    {
        double delay = e.At - songTime;
        double skip = e.Skip + Math.Max(0, -delay);
        if (skip >= clipSeconds[(int)e.Cue] - .02 || clips[(int)e.Cue] == null) return;
        played[(int)e.Cue]++;
        LastPlayed = e;
        LastDelay = delay;
        if (!Application.isPlaying) return;
        var source = voices[nextVoice];
        nextVoice = (nextVoice + 1) % voices.Length;
        source.Stop();
        // 作った音は鳴らす前に中身を確かめる(音の出力先が変わると消えるため)。
        var clip = ProceduralSfx.Revive(ref clips[(int)e.Cue]);
        source.clip = clip;
        source.volume = 1f;
        source.timeSamples = Mathf.Clamp((int)(skip * clip.frequency), 0, Mathf.Max(0, clip.samples - 1));
        if (delay > 0) source.PlayScheduled(AudioSettings.dspTime + delay);
        else source.Play();
    }

    // 曲が止まった・音の出力先が変わったとき。予約と鳴っている音を止め、次の Tick で今の時刻から予約し直す。
    public void ResetSounds()
    {
        StopVoices();
        seek = true;
    }

    void StopVoices()
    {
        foreach (var voice in voices) if (voice != null) voice.Stop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopVoices();
        if (root != null)
        {
            root.SetActive(false);
            UISkinKit.SafeDestroy(root);
        }
        foreach (var clip in clips) UISkinKit.SafeDestroy(clip);
    }
}
