using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// 選曲コントローラーのUpdateから駆動。音と譜面を同じDSP時計で進める。
public sealed class SongSelectChartPreview : MonoBehaviour
{
    public const double SelectionDelaySeconds=1;
    public enum PreviewState { Idle, Waiting, Loading, Playing, Complete, Unavailable, Failed }
    const string MutedKey = "songSelectPreviewMuted";
    public PreviewState State { get; private set; }
    public bool Muted { get; private set; }
    string playingStatusText;
    double shownStatusSeconds, shownStatusDuration;
    bool shownStatusMuted;
    public string StatusText
    {
        get
        {
            switch (State)
            {
                case PreviewState.Waiting: return "試聴を準備中";
                case PreviewState.Loading: return "音源を読み込み中";
                case PreviewState.Playing:
                    double seconds = Math.Max(0, SongTime - Window.Start);
                    double shownSeconds = Math.Ceiling(seconds);
                    // Duration の表示は秒単位。時計の読取は毎回行い、表示変更時だけ文字列を作る。
                    if (playingStatusText == null || shownStatusSeconds != shownSeconds
                        || shownStatusDuration != Window.Duration || shownStatusMuted != Muted)
                    {
                        shownStatusSeconds = shownSeconds;
                        shownStatusDuration = Window.Duration;
                        shownStatusMuted = Muted;
                        playingStatusText = (Muted ? "消音で試聴中 " : "試聴中 ")
                            + SongChartInsights.Duration(seconds) + " / " + SongChartInsights.Duration(Window.Duration);
                    }
                    return playingStatusText;
                case PreviewState.Complete: return "試聴終了・もう一度聴けます";
                case PreviewState.Unavailable: return "この難易度には試聴できる譜面がありません";
                case PreviewState.Failed: return "音源を再生できません・再試聴で再試行";
                default: return "曲を選ぶと試聴できます";
            }
        }
    }
    private AudioSource source;
    private AudioClip ownedClip;
    private string loadedAudioIdentity;
    private UnityWebRequest request;
    private Coroutine loading;
    private SongChartPreviewView view;
    private float baseVolume;
    private float loudnessGain = 1;
    private double scheduledDsp;
    private int startSample;
    private bool clockSynchronized;
    private double stoppedSongTime;
    private int generation;
    public string SongId { get; private set; }
    public string Difficulty { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsLoading => loading!=null;
    public SongPreviewWindow Window { get; private set; }
    public double SongTime
    {
        get
        {
            if(!IsPlaying || source==null || ownedClip==null) return stoppedSongTime;
            double now=AudioSettings.dspTime;
            if(now<scheduledDsp) return Window.Start;
            if(!clockSynchronized)
            {
                int sample=source.timeSamples;
                // シーク要求時の位置のままなら、まだ実音声が進んでいない。
                if(!source.isPlaying || sample<=startSample) return Window.Start;
                scheduledDsp=now-(sample/(double)ownedClip.frequency-Window.Start);
                source.SetScheduledEndTime(scheduledDsp+Window.Duration);
                clockSynchronized=true;
            }
            double projected=Window.Start+Math.Max(0,now-scheduledDsp);
            if(!source.isPlaying) return projected;
            // 開始時の一回だけの同期では、圧縮音声のデコード位置が遅れた後もDSPが先行し続ける。
            // DSPの連続性を使いつつ、実際の音声カーソルとの差を1バッファ以内に留める。
            double decoded=source.timeSamples/(double)ownedClip.frequency;
            AudioSettings.GetDSPBufferSize(out int frames,out _);
            double buffer=frames/(double)Math.Max(1,AudioSettings.outputSampleRate);
            return Math.Max(Window.Start,Math.Clamp(projected,decoded-buffer,decoded+buffer));
        }
    }
    // 実績用。DSPの推定時計でなく、実際に進んだ音声カーソルを返す。
    public bool TryGetAudiblePosition(out int sourceGeneration, out double position)
    {
        sourceGeneration = generation;
        position = source != null && ownedClip != null && ownedClip.frequency > 0
            ? source.timeSamples / (double)ownedClip.frequency : 0;
        return IsPlaying && State == PreviewState.Playing && source != null && ownedClip != null
            && source.clip == ownedClip && source.isPlaying && !Muted && !source.mute && source.volume > 0
            && !AudioListener.pause && AudioListener.volume > 0 && AudioSettings.dspTime >= scheduledDsp;
    }

    public double SelectedAt { get; private set; }
    // 1秒の待ちを数え始める時刻。曲送りをため続けている間は HoldOff で後ろへずらす。
    public double SettledAt { get; private set; }
    public double StartedAt { get; private set; }
    public SongChartPreviewView View => view;

    public void Initialize(AudioSource audio)
    {
        source=audio; baseVolume=audio!=null?audio.volume:.7f;
        Muted=PlayerPrefs.GetInt(MutedKey,0)!=0;
        if(source!=null) source.mute=Muted;
        // 試聴も本編と同じだけ上乗せする(MusicBoost、2026-10-30)。
        MusicBoost.AttachTo(source);
    }
    public void SetMuted(bool muted)
    {
        Muted=muted;
        if(source!=null) source.mute=muted;
        PlayerPrefs.SetInt(MutedKey,muted?1:0); PlayerPrefs.Save();
    }
    public void Attach(RectTransform panel) { view?.Dispose(); view=new SongChartPreviewView(panel); }
    public void Select(string songId,string difficulty,float duration)
    {
        Cancel(); SongId=songId; Difficulty=difficulty; selectedDuration=duration; SelectedAt=SettledAt=Time.realtimeSinceStartupAsDouble;
        if(source==null || !isActiveAndEnabled || string.IsNullOrEmpty(songId)) return;
        State=PreviewState.Waiting;
        loading=StartCoroutine(Load(songId,difficulty,duration,generation));
    }

    // 難易度変更だけの入口。音源・範囲が同じなら、音とDSP時計には触れない。
    public bool TryChangeDifficulty(string songId,string difficulty,float duration)
    {
        if(!isActiveAndEnabled || !IsPlaying || State!=PreviewState.Playing
            || source==null || ownedClip==null || source.clip!=ownedClip || !source.isPlaying
            || !string.Equals(SongId,songId,StringComparison.Ordinal)
            || RushESecretState.IsSecretDifficulty(Difficulty)
            || RushESecretState.IsSecretDifficulty(difficulty)) return false;

        ChartData chart;
        StagePerformanceTimeline timeline;
        SongPreviewWindow nextWindow;
        try
        {
            chart=ChartLoader.LoadFromStreamingAssets(songId,difficulty);
            if(chart?.notes==null || chart.notes.Count==0) return false;
            timeline=StagePerformanceTimeline.Load(songId);
            nextWindow=ResolveWindow(songId,timeline,ownedClip.length,duration);
            string dir=Path.Combine(Application.streamingAssetsPath,"Songs",songId);
            if(string.IsNullOrEmpty(loadedAudioIdentity) || loadedAudioIdentity!=AudioIdentity(dir)) return false;
        }
        catch(Exception) { return false; }
        if(!nextWindow.IsValid || nextWindow.Start!=Window.Start || nextWindow.Duration!=Window.Duration) return false;
        double now=SongTime;
        if(now>=Window.Start+Window.Duration) return false;

        Difficulty=difficulty; selectedDuration=duration;
        if(view!=null)
        {
            view.Prepare(chart,Window,timeline,Difficulty);
            view.Tick(now); view.Show();
        }
        return true;
    }

    // デコード失敗で原音へ戻った場合も含め、候補の順序・内容の更新・音量補正が同じかを確かめる。
    static string AudioIdentity(string directory,string difficulty=null)
    {
        try
        {
            var identity=new System.Text.StringBuilder();
            foreach(var playback in SongLoudness.CandidatesInFolder(directory,difficulty))
            {
                var file=new FileInfo(playback.Path);
                identity.Append(file.FullName).Append('\n').Append(file.Length).Append(':')
                    .Append(file.LastWriteTimeUtc.Ticks).Append(':')
                    .Append(playback.Gain.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            return identity.ToString();
        }
        catch(IOException) { return null; }
        catch(UnauthorizedAccessException) { return null; }
    }

    // 難易度を切り替えても同じ時計で終端を求める。Normal、互換譜面、Easy、Hardの順。
    // 専用の音源を鳴らす難易度(ElDorado の G)は、その音源に合わせたその難易度の譜面の拍を先に使う。
    internal static SongPreviewWindow ResolveWindow(string songId,StagePerformanceTimeline timeline,double audioLength,double duration=10,string variantDifficulty=null)
    {
        string dir=Path.Combine(Application.streamingAssetsPath,"Songs",songId);
        SongSelectBeatTimeline beats=null;
        var names=new List<string>();
        if(!string.IsNullOrWhiteSpace(variantDifficulty) && ChartLoader.ValidPathSegment(variantDifficulty.Trim()))
            names.Add("chart_"+variantDifficulty.Trim().ToLowerInvariant()+".json");
        names.AddRange(new[]{"chart_normal.json","chart.json","chart_easy.json","chart_hard.json"});
        foreach(string name in names)
        {
            string path=Path.Combine(dir,name);
            if(!File.Exists(path)) continue;
            var chart=ChartLoader.LoadFromFile(path);
            if(chart?.notes==null || chart.notes.Count==0) continue;
            // editorTempoMapを優先し、校歌のような旧譜面はノーツの拍・時刻を使う。
            // 揺籠の旧データは付点四分音符が1拍。既定の4拍で12/8の1小節になる。
            beats=new SongSelectBeatTimeline(new[]{chart});
            break;
        }
        return SongPreviewWindow.Resolve(timeline,audioLength,duration,beats);
    }

    // まだ待っている試聴の開始を、今から1秒後以降へ延ばす。読み込み中・再生中の試聴は止めない。
    public void HoldOff()
    {
        if(!IsPlaying && request==null) SettledAt=Math.Max(SettledAt,Time.realtimeSinceStartupAsDouble);
    }

    private IEnumerator Load(string songId,string difficulty,float duration,int token)
    {
        // 高速な曲送りでは、まだ音源を開かない。1秒の間はジャケットを維持する。
        while(Time.realtimeSinceStartupAsDouble<SettledAt+SelectionDelaySeconds) yield return null;
        if(token!=generation) yield break;
        ChartData chart=null;
        try { chart=ChartLoader.LoadFromStreamingAssets(songId,difficulty); }
        catch(Exception e) { Debug.LogWarning("譜面プレビューを読み込めません: "+e.Message); }
        if(chart?.notes==null || chart.notes.Count==0) { State=PreviewState.Unavailable; loading=null; yield break; }
        State=PreviewState.Loading;
        string dir=Path.Combine(Application.streamingAssetsPath,"Songs",songId);
        // 専用の音源がある難易度(ElDorado の G)は、その音源・演出・試聴の区間で鳴らす。
        string variant=SongLoudness.VariantSuffix(dir,difficulty).Length>0 ? difficulty : null;
        string audioIdentity=AudioIdentity(dir,variant);
        foreach(var playback in SongLoudness.CandidatesInFolder(dir,variant))
        {
            string full=playback.Path;
            string extension=Path.GetExtension(full).ToLowerInvariant();
            AudioType type=extension==".ogg"?AudioType.OGGVORBIS:extension==".wav"?AudioType.WAV:AudioType.MPEG;
            request=UnityWebRequestMultimedia.GetAudioClip(new Uri(full).AbsoluteUri,type);
            request.timeout=10;
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio=true;
            yield return request.SendWebRequest();
            if(token!=generation) yield break;
            if(request.result!=UnityWebRequest.Result.Success) { request.Dispose(); request=null; continue; }
            ownedClip=DownloadHandlerAudioClip.GetContent(request);
            request.Dispose(); request=null;
            if(ownedClip==null) continue;
            loudnessGain=playback.Gain;
            var timeline=StagePerformanceTimeline.Load(songId,difficulty);
            Window=ResolveWindow(songId,timeline,ownedClip.length,duration,variant);
            if(token!=generation) yield break;
            if(!Window.IsValid)
            {
                Destroy(ownedClip); ownedClip=null; continue;
            }
            // 音だけのディスク選曲でも同じDSP時計を使う。旧譜面表示は任意。
            view?.Prepare(chart,Window,timeline,Difficulty);
            loadedAudioIdentity=audioIdentity;
            source.clip=ownedClip; source.loop=false; source.pitch=1;
            startSample=Mathf.Clamp((int)Math.Round(Window.Start*ownedClip.frequency),0,ownedClip.samples-1);
            source.timeSamples=startSample;
            clockSynchronized=false; stoppedSongTime=Window.Start;
            source.volume=0;
            scheduledDsp=AudioSettings.dspTime+.05;
            source.PlayScheduled(scheduledDsp);
            source.SetScheduledEndTime(scheduledDsp+Window.Duration);
            StartedAt=Time.realtimeSinceStartupAsDouble+.05;
            IsPlaying=true; State=PreviewState.Playing; loading=null; yield break;
        }
        State=PreviewState.Failed; loading=null;
    }

    public void Tick()
    {
        if(!IsPlaying || source==null) return;
        if(AudioSettings.dspTime<scheduledDsp) return;
        double time=SongTime;
        if(!clockSynchronized)
        {
            // オーディオデバイスが開始できない場合も「試聴中」のまま固めない。
            if(Time.realtimeSinceStartupAsDouble-StartedAt>3) { State=PreviewState.Failed; Finish(); }
            return;
        }
        double elapsed=time-Window.Start;
        if(elapsed>=Window.Duration) { stoppedSongTime=Window.Start+Window.Duration; State=PreviewState.Complete; Finish(); return; }
        if(!source.isPlaying && Time.realtimeSinceStartupAsDouble-StartedAt>.5)
        { State=PreviewState.Failed; Finish(); return; }
        float gain=Mathf.Min(Mathf.Clamp01((float)(elapsed/.08)),Mathf.Clamp01((float)((Window.Duration-elapsed)/.25)));
        source.volume=baseVolume*gain*loudnessGain;
        view?.Show(); view?.Tick(time);
    }

    public void Cancel()
    {
        // 止めた試聴は、音の立て直しのあとにも鳴らし直さない(立て直しの間にスタートした・RUSH E を解いた、など)。
        replayAfterAudioChange=false;
        generation++;
        if(loading!=null) { StopCoroutine(loading); loading=null; }
        if(request!=null) { request.Abort(); request.Dispose(); request=null; }
        Finish(); State=PreviewState.Idle;
    }
    private void Finish()
    {
        if(IsPlaying) stoppedSongTime=SongTime;
        IsPlaying=false; clockSynchronized=false; loudnessGain=1; loadedAudioIdentity=null;
        if(source!=null) { source.Stop(); source.clip=null; source.volume=baseVolume; }
        if(ownedClip!=null) { Destroy(ownedClip); ownedClip=null; }
        view?.Hide();
    }
    // 音の出力先が変わると、試聴の音も止まって中身が消える(AudioOutputRecovery)。
    // 試聴中・準備中だったら、いったん止めて(「再生できません」にしない)、立て直したあとに同じ曲の試聴をやり直す。
    private float selectedDuration;
    private bool replayAfterAudioChange;
    private void OnEnable()
    {
        AudioOutputRecovery.OutputChanged+=PauseForAudioChange;
        AudioOutputRecovery.Restored+=ReplayAfterAudioChange;
    }
    void PauseForAudioChange()
    {
        bool replay=replayAfterAudioChange || IsPlaying || loading!=null;
        if(replay) Cancel();
        replayAfterAudioChange=replay;
    }
    void ReplayAfterAudioChange()
    {
        if(!replayAfterAudioChange) return;
        replayAfterAudioChange=false;
        if(!string.IsNullOrEmpty(SongId)) Select(SongId,Difficulty,selectedDuration);
    }
    private void OnDisable()
    {
        AudioOutputRecovery.OutputChanged-=PauseForAudioChange;
        AudioOutputRecovery.Restored-=ReplayAfterAudioChange;
        replayAfterAudioChange=false;
        Cancel();
    }
    private void OnDestroy() { Cancel(); view?.Dispose(); view=null; }
}
