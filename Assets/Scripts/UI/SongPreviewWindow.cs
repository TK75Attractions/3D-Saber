using System;

// 曲の音源時計を基準に、プレビュー範囲だけを決める。譜面や判定設定は変更しない。
public readonly struct SongPreviewWindow
{
    public readonly double Start, Duration;
    public bool IsValid => Duration > .05;
    public SongPreviewWindow(double start,double duration) { Start=start; Duration=duration; }

    public static SongPreviewWindow Resolve(StagePerformanceTimeline timeline,double audioLength,double requestedDuration=10,SongSelectBeatTimeline beats=null)
    {
        if(!Finite(audioLength) || audioLength<=.05) return default;
        double requested=Finite(requestedDuration)?Math.Max(.1,Math.Min(30,requestedDuration)):10;
        double duration=Math.Min(audioLength,requested);
        double start=timeline!=null?timeline.previewStartSeconds:-1;
        if(!Finite(start) || start<0)
        {
            start=0; float strongest=-1;
            if(timeline?.sections!=null)
                foreach(var s in timeline.sections)
                {
                    if(s==null || !Finite(s.startSeconds) || !Finite(s.endSeconds) || !Finite(s.intensity) || s.startSeconds<0 || s.endSeconds-s.startSeconds<duration) continue;
                    if(s.intensity>=strongest) { strongest=s.intensity; start=s.startSeconds; }
                }
        }
        // 開始位置と曲末での補正は従来どおり。既定の10秒だけ、終端を小節へ寄せる。
        var window=new SongPreviewWindow(Math.Max(0,Math.Min(start,audioLength-duration)),duration);
        if(beats==null || requested!=10) return window;
        double target=window.Start+10, end=window.Start+window.Duration, nearest=double.PositiveInfinity;
        foreach(double candidate in beats.BarStartTimes(window.Start+8,Math.Min(window.Start+12,audioLength)))
        {
            double distance=Math.Abs(candidate-target);
            // 昇順に調べ、同距離なら早い小節を残す。候補がなければ従来の終端。
            if(distance<nearest-1e-9) { nearest=distance; end=candidate; }
        }
        return new SongPreviewWindow(window.Start,end-window.Start);
    }

    public static double NoteTime(ChartData chart,NoteData note) => note.TimeSeconds+(chart!=null?chart.offsetMs/1000.0:0);
    public static double Linger(NoteData note) => note.count>1 ? (note.lengthMs>0?note.lengthMs/1000.0:(note.count-1)*.7) : 0;
    public static bool Intersects(ChartData chart,NoteData note,SongPreviewWindow window,double approach)
    {
        if(note==null || !window.IsValid) return false;
        double hit=NoteTime(chart,note);
        return Finite(hit) && hit+Linger(note)>=window.Start && hit<window.Start+window.Duration+approach;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
