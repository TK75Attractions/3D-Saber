using UnityEngine;
using UnityEngine.UI;

// 目盛り・同期モデル・入力分布を解像度に依存しない図形で描く。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class CalibrationTimingGraphic : MaskableGraphic
{
    public bool isDistribution;
    double time,error; int offset; bool playing,hasError; CalibrationResult result;
    static readonly Color Cyan=new Color(.43f,.93f,.87f), Dim=new Color(.19f,.31f,.37f);
    public void SetTiming(double t,int ms,bool active) { time=t;offset=ms;playing=active;SetVerticesDirty(); }
    public void SetResult(CalibrationResult r,bool valid,double ms)
    { if(result==r&&hasError==valid&&System.Math.Abs(error-ms)<.01) return; result=r;hasError=valid;error=ms;SetVerticesDirty(); }
    void Box(VertexHelper vh,float x,float y,float w,float h,Color c)
    { SongSelectPanelGraphic.Quad(vh,new Vector2(x,y),new Vector2(x,y+h),new Vector2(x+w,y+h),new Vector2(x+w,y),c); }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); var r=rectTransform.rect;
        if(isDistribution)
        {
            // 非対称の実際の PERFECT 幅。「中央 ±8ms」の目安とは別。
            float scale=r.width/500f;
            Box(vh,-(float)JudgmentTierHelper.EarlyPerfectSeconds*1000*scale,r.yMin+4,
                (float)(JudgmentTierHelper.EarlyPerfectSeconds+JudgmentTierHelper.LatePerfectSeconds)*1000*scale,r.height-8,new Color(.1f,.29f,.28f));
            for(int i=-5;i<=5;i++) Box(vh,i*r.width/10f-1,r.yMin+4,1,r.height-8,Dim);
            Box(vh,-1,r.yMin,2,r.height,Cyan);
            if(result!=null)
            {
                int[] bins=new int[25]; foreach(var s in result.Samples) bins[Mathf.Clamp((int)((s.ErrorMs+250)/20),0,24)]++;
                int max=1;foreach(int n in bins)max=Mathf.Max(max,n);
                for(int i=0;i<25;i++) if(bins[i]>0) Box(vh,r.xMin+i*r.width/25+2,r.yMin+6,r.width/25-4,(r.height-12)*bins[i]/max,
                    i<12?new Color(.4f,.72f,1):i>12?new Color(1,.59f,.48f):Cyan);
            }
            else if(hasError) Box(vh,Mathf.Clamp((float)error,-245,245)*scale-3,r.yMin+2,6,r.height-4,Color.white);
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
}
