using UnityEngine;
using UnityEngine.UI;

// 目盛り・同期モデル・入力分布を解像度に依存しない図形で描く。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class CalibrationTimingGraphic : MaskableGraphic
{
    public bool isDistribution;
    double time,error; int offset; bool playing,hasError; CalibrationResult result;
    CalibrationSample[] preview;
    float previewStarted, lastInputTime;
    int previewDelta;
    static readonly Color Cyan=new Color(.55f,.95f,.72f), Dim=new Color(.3f,.39f,.42f), Amber=new Color(1,.79f,.35f);
    public void PreviewAdjustment(CalibrationResult history,int delta)
    { preview=history?.Samples; previewDelta=delta; previewStarted=Time.unscaledTime; SetVerticesDirty(); }
    public void ClearPreview() { preview=null; SetVerticesDirty(); }
    void Update()
    {
        if (preview!=null && Time.unscaledTime-previewStarted>3) { preview=null; SetVerticesDirty(); }
        if (preview!=null || Time.unscaledTime-lastInputTime<.3f) SetVerticesDirty();
    }
    public void SetTiming(double t,int ms,bool active) { time=t;offset=ms;playing=active;SetVerticesDirty(); }
    public void SetResult(CalibrationResult r,bool valid,double ms)
    {
        if(result==r&&hasError==valid&&System.Math.Abs(error-ms)<.01) return;
        if(r!=result&&r!=null) { preview=null; lastInputTime=Time.unscaledTime; }
        result=r;hasError=valid;error=ms;SetVerticesDirty();
    }
    void Box(VertexHelper vh,float x,float y,float w,float h,Color c)
    { SongSelectPanelGraphic.Quad(vh,new Vector2(x,y),new Vector2(x,y+h),new Vector2(x+w,y+h),new Vector2(x+w,y),c); }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); var r=rectTransform.rect;
        if(isDistribution)
        {
            // 本番と同じ非対称PERFECT帯。色はセーバーの青・赤と分ける。
            float scale=r.width/500f;
            float center=r.center.x, y=r.center.y;
            Box(vh,r.xMin,y-3,r.width,6,Dim);
            Box(vh,center-(float)CalibrationProtocol.EarlyPerfectMs*scale,y-15,
                (float)(CalibrationProtocol.EarlyPerfectMs+CalibrationProtocol.LatePerfectMs)*scale,30,new Color(.16f,.38f,.26f));
            Box(vh,center-2,y-20,4,40,Cyan);
            var points=result?.Samples??preview;
            if(points!=null)
            {
                bool predicted=result==null;
                float move=predicted?Mathf.SmoothStep(0,1,(Time.unscaledTime-previewStarted)/.35f):0;
                for(int i=0;i<points.Length;i++)
                {
                    double ms=points[i].ErrorMs-(predicted?previewDelta*move:0);
                    Color c=ms < -CalibrationProtocol.EarlyPerfectMs||ms > CalibrationProtocol.LatePerfectMs?Amber:Cyan;
                    c.a=predicted?.35f:Mathf.Lerp(.25f,1,(i+1f)/points.Length);
                    float drop=!predicted&&i==points.Length-1?14*(1-Mathf.Clamp01((Time.unscaledTime-lastInputTime)/.22f)):0;
                    Dot(vh,center+Mathf.Clamp((float)ms,-245,245)*scale,y+drop,i==points.Length-1?8:5,c);
                }
            }
            return;
        }
        Box(vh,r.xMin,0,r.width,2,Dim);
        // 基準音と到達位置を右から左へ流す。白線に重なる瞬間が各イベントの時刻。
        double phase=playing?(time/CalibrationProtocol.BeatSeconds-System.Math.Floor(time/CalibrationProtocol.BeatSeconds)):0;
        Box(vh,-1,r.yMin,2,r.height,new Color(1,1,1,.8f));
        float pixelsPerSecond=r.width/2.4f;
        for(int i=-3;i<=3;i++)
        {
            float x=(float)((i-phase)*CalibrationProtocol.BeatSeconds)*pixelsPerSecond;
            float adjusted=x+offset/1000f*pixelsPerSecond;
            int beat=(int)System.Math.Floor(time/CalibrationProtocol.BeatSeconds)+i;
            if((!playing||(beat>=1&&beat<=32))&&x>r.xMin+8&&x<r.xMax-8) Box(vh,x-3,8,6,16,Color.white);
            if((!playing||(beat>=5&&beat<=32))&&adjusted>r.xMin+8&&adjusted<r.xMax-8) Box(vh,adjusted-5,-24,10,16,Cyan);
        }
    }
    void Dot(VertexHelper vh,float x,float y,float radius,Color c)
    {
        const int segments=12;
        int first=vh.currentVertCount;
        vh.AddVert(new Vector3(x,y),c,Vector2.zero);
        for(int i=0;i<=segments;i++)
        {
            float angle=i*Mathf.PI*2/segments;
            vh.AddVert(new Vector3(x+Mathf.Cos(angle)*radius,y+Mathf.Sin(angle)*radius),c,Vector2.zero);
            if(i>0) vh.AddTriangle(first,first+i,first+i+1);
        }
    }
}
