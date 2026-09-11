using System;

// 曲の音源時計を基準に、プレビュー範囲だけを決める。譜面や判定設定は変更しない。
public readonly struct SongPreviewWindow
{
    public readonly double Start, Duration;
    public bool IsValid => Duration > .05;
    public SongPreviewWindow(double start,double duration) { Start=start; Duration=duration; }

    public static SongPreviewWindow Resolve(StagePerformanceTimeline timeline,double audioLength,double requestedDuration=10)
    {
        if(!Finite(audioLength) || audioLength<=.05) return default;
        double duration=Math.Min(audioLength,Finite(requestedDuration)?Math.Max(.1,Math.Min(30,requestedDuration)):10);
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
        return new SongPreviewWindow(Math.Max(0,Math.Min(start,audioLength-duration)),duration);
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
