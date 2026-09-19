using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// 選曲コントローラーのUpdateから駆動。音と譜面を同じDSP時計で進める。
public sealed class SongSelectChartPreview : MonoBehaviour
{
    public const double SelectionDelaySeconds=1;
    private AudioSource source;
    private AudioClip ownedClip;
    private UnityWebRequest request;
    private Coroutine loading;
    private SongChartPreviewView view;
    private float baseVolume;
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
            return Window.Start+Math.Max(0,now-scheduledDsp);
        }
    }
    public double SelectedAt { get; private set; }
    public double StartedAt { get; private set; }
    public SongChartPreviewView View => view;

    public void Initialize(AudioSource audio) { source=audio; baseVolume=audio!=null?audio.volume:.7f; }
    public void Attach(RectTransform panel) { view?.Dispose(); view=new SongChartPreviewView(panel); }
    public void Select(string songId,string difficulty,float duration)
    {
        Cancel(); SongId=songId; Difficulty=difficulty; SelectedAt=Time.realtimeSinceStartupAsDouble;
        if(source==null || !isActiveAndEnabled || string.IsNullOrEmpty(songId)) return;
        loading=StartCoroutine(Load(songId,difficulty,duration,generation));
    }

    private IEnumerator Load(string songId,string difficulty,float duration,int token)
    {
        // 高速な曲送りでは、まだ音源を開かない。1秒の間はジャケットを維持する。
        while(Time.realtimeSinceStartupAsDouble<SelectedAt+SelectionDelaySeconds) yield return null;
        if(token!=generation) yield break;
        ChartData chart=null;
        try { chart=ChartLoader.LoadFromStreamingAssets(songId,difficulty); }
        catch(Exception e) { Debug.LogWarning("譜面プレビューを読み込めません: "+e.Message); }
        if(chart?.notes==null || chart.notes.Count==0) { loading=null; yield break; }
        string dir=Path.Combine(Application.streamingAssetsPath,"Songs",songId);
        foreach(var name in new[]{"audio.ogg","audio.wav","audio.mp3"})
        {
            string full=Path.Combine(dir,name); if(!File.Exists(full)) continue;
            AudioType type=name.EndsWith(".ogg")?AudioType.OGGVORBIS:name.EndsWith(".wav")?AudioType.WAV:AudioType.MPEG;
            request=UnityWebRequestMultimedia.GetAudioClip(new Uri(full).AbsoluteUri,type);
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio=true;
            yield return request.SendWebRequest();
            if(token!=generation) yield break;
            if(request.result!=UnityWebRequest.Result.Success) { request.Dispose(); request=null; continue; }
            ownedClip=DownloadHandlerAudioClip.GetContent(request);
            request.Dispose(); request=null;
            if(ownedClip==null) continue;
            var timeline=StagePerformanceTimeline.Load(songId);
            Window=SongPreviewWindow.Resolve(timeline,ownedClip.length,duration);
            while(view==null && token==generation) yield return null;
            if(token!=generation || !Window.IsValid) yield break;
            view.Prepare(chart,Window,timeline,Difficulty);
            source.clip=ownedClip; source.loop=false; source.pitch=1;
            startSample=Mathf.Clamp((int)Math.Round(Window.Start*ownedClip.frequency),0,ownedClip.samples-1);
            source.timeSamples=startSample;
            clockSynchronized=false; stoppedSongTime=Window.Start;
            source.volume=0;
            scheduledDsp=AudioSettings.dspTime+.05;
            source.PlayScheduled(scheduledDsp);
            source.SetScheduledEndTime(scheduledDsp+Window.Duration);
            StartedAt=Time.realtimeSinceStartupAsDouble+.05;
            IsPlaying=true; loading=null; yield break;
        }
        loading=null;
    }

    public void Tick()
    {
        if(!IsPlaying || source==null) return;
        if(AudioSettings.dspTime<scheduledDsp) return;
        double time=SongTime;
        if(!clockSynchronized) return;
        double elapsed=time-Window.Start;
        if(elapsed>=Window.Duration) { stoppedSongTime=Window.Start+Window.Duration; Finish(); return; }
        float gain=Mathf.Min(Mathf.Clamp01((float)(elapsed/.08)),Mathf.Clamp01((float)((Window.Duration-elapsed)/.25)));
        source.volume=baseVolume*gain;
        view?.Show(); view?.Tick(time);
    }

    public void Cancel()
    {
        generation++;
        if(loading!=null) { StopCoroutine(loading); loading=null; }
        if(request!=null) { request.Abort(); request.Dispose(); request=null; }
        Finish();
    }
    private void Finish()
    {
        if(IsPlaying) stoppedSongTime=SongTime;
        IsPlaying=false; clockSynchronized=false;
        if(source!=null) { source.Stop(); source.clip=null; source.volume=baseVolume; }
        if(ownedClip!=null) { Destroy(ownedClip); ownedClip=null; }
        view?.Hide();
    }
    private void OnDisable() { Cancel(); }
    private void OnDestroy() { Cancel(); view?.Dispose(); view=null; }
}
