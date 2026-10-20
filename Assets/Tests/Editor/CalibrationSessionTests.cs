using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

public class CalibrationSessionTests
{
    readonly string[] keys={"judgmentOffsetMs",CalibrationDraft.ActiveKey,CalibrationDraft.SpeakerKey,CalibrationDraft.HeadphoneKey};
    int[] saved; bool[] existed;
    [SetUp] public void Setup()
    {
        saved=keys.Select(k=>PlayerPrefs.GetInt(k)).ToArray(); existed=keys.Select(PlayerPrefs.HasKey).ToArray();
        foreach(var k in keys)PlayerPrefs.DeleteKey(k);
    }
    [TearDown] public void Cleanup()
    {
        for(int i=0;i<keys.Length;i++) { if(existed[i])PlayerPrefs.SetInt(keys[i],saved[i]);else PlayerPrefs.DeleteKey(keys[i]); }
        PlayerPrefs.Save();
    }
    static CalibrationSample[] Samples(double error) => Enumerable.Range(0,24).Select(i=>
        new CalibrationSample(i,error+(i%3-1)*2,i%2==0?SaberHand.Left:SaberHand.Right)).ToArray();
    [Test] public void DraftPreservesLegacyValueWithoutWriting()
    {
        GameSession.JudgmentOffsetMs=73;var d=new CalibrationDraft();
        Assert.AreEqual(73,d.OffsetMs);Assert.False(d.IsDirty);Assert.False(PlayerPrefs.HasKey(CalibrationDraft.SpeakerKey));
        d.SetOffset(91);Assert.AreEqual(73,GameSession.JudgmentOffsetMs);Assert.True(d.IsDirty);
    }
    [Test] public void DraftRestoreDoesNotResetToFactoryDefault()
    { GameSession.JudgmentOffsetMs=-23;var d=new CalibrationDraft();d.SetOffset(300);d.RestoreSaved();Assert.AreEqual(-23,d.OffsetMs);Assert.False(d.IsDirty); }
    [Test] public void ProfilesRetainIndependentDraftsAndCommitActiveToGame()
    {
        GameSession.JudgmentOffsetMs=60;var d=new CalibrationDraft();d.SetOffset(75);d.SelectProfile(1);Assert.AreEqual(60,d.OffsetMs);
        d.SetOffset(18);d.SelectProfile(0);Assert.AreEqual(75,d.OffsetMs);d.SelectProfile(1);d.Commit();
        Assert.AreEqual(18,GameSession.JudgmentOffsetMs);Assert.False(d.IsDirty);
        var next=new CalibrationDraft();Assert.AreEqual(1,next.Profile);next.SelectProfile(0);Assert.AreEqual(75,next.OffsetMs);
    }
    [Test] public void LegacyChangeAfterProfileSaveWins()
    { var d=new CalibrationDraft();d.Commit();GameSession.JudgmentOffsetMs=82;Assert.AreEqual(82,new CalibrationDraft().OffsetMs); }
    [Test] public void ProfileSwitchWithoutCommitDoesNotChangeGame()
    { GameSession.JudgmentOffsetMs=60;PlayerPrefs.SetInt(CalibrationDraft.HeadphoneKey,9);var d=new CalibrationDraft();d.SelectProfile(1);Assert.AreEqual(9,d.OffsetMs);Assert.AreEqual(60,GameSession.JudgmentOffsetMs); }
    [TestCase(9999,1000)] [TestCase(-9999,-1000)] public void DraftClamps(int v,int expected)
    { var d=new CalibrationDraft();d.SetOffset(v);Assert.AreEqual(expected,d.OffsetMs); }
    [TestCase(60,"遅らせ")] [TestCase(-60,"早め")] [TestCase(0,"なし")]
    public void ExplainsDirection(int value,string word) { StringAssert.Contains(word,CalibrationDraft.Explain(value)); }
    [TestCase(30,90)] [TestCase(-30,30)] [TestCase(7,60)] [TestCase(-7,60)]
    public void RecommendationUsesActualGameSignAndDeadband(double error,int expected)
    { var r=CalibrationResult.Analyze(Samples(error),60);Assert.True(r.CanRecommend,r.Message);Assert.AreEqual(expected,r.ProposedOffsetMs); }
    [Test] public void MissingIsNeverZeroError()
    { var r=CalibrationResult.Analyze(Array.Empty<CalibrationSample>(),60);Assert.False(r.CanRecommend);Assert.AreEqual(24,r.Missing);Assert.AreEqual(60,r.ProposedOffsetMs); }
    [Test] public void InsufficientCoverageDoesNotRecommend()
    { var r=CalibrationResult.Analyze(Samples(30).Take(15),60);Assert.False(r.CanRecommend);Assert.AreEqual(9,r.Missing); }
    [Test] public void RejectsMixedMouseInput()
    { var s=Samples(20);s[0].Hand=SaberHand.Any;var r=CalibrationResult.Analyze(s,60);Assert.False(r.CanRecommend);StringAssert.Contains("マウス",r.Message); }
    [Test] public void LargeLeftRightDifferenceDoesNotRecommend()
    { var s=Samples(0);for(int i=0;i<24;i++)s[i].ErrorMs=i%2==0?-20:30;var r=CalibrationResult.Analyze(s,60);Assert.False(r.CanRecommend);StringAssert.Contains("左右",r.Message); }
    [Test] public void SessionDriftDoesNotRecommend()
    { var s=Samples(0);for(int i=0;i<24;i++)s[i].ErrorMs=i<12?-20:20;var r=CalibrationResult.Analyze(s,60);Assert.False(r.CanRecommend);Assert.AreEqual(40,r.DriftMs); }
    [Test] public void TwoOutliersDoNotPullMedian()
    { var s=Samples(25);s[0].ErrorMs=150;s[1].ErrorMs=-90;var r=CalibrationResult.Analyze(s,60);Assert.True(r.CanRecommend,r.Message);Assert.AreEqual(2,r.Excluded);Assert.AreEqual(85,r.ProposedOffsetMs); }
    [Test] public void TooManyOutliersDoesNotRecommend()
    { var s=Samples(25);s[0].ErrorMs=150;s[1].ErrorMs=-90;s[2].ErrorMs=180;Assert.False(CalibrationResult.Analyze(s,60).CanRecommend); }
    [TestCase(-95)] [TestCase(175)] public void EdgeCensoredSamplesDoNotRecommend(double error)
    { Assert.False(CalibrationResult.Analyze(Samples(error),60).CanRecommend); }
    [Test] public void InterruptedOrHitchPreventsProposal()
    { Assert.False(CalibrationResult.Analyze(Samples(20),60,true).CanRecommend);Assert.False(CalibrationResult.Analyze(Samples(20),60,false,1).CanRecommend); }
    [Test] public void DuplicateAndNonFiniteSamplesAreNotExtraSuccesses()
    {
        var s=new List<CalibrationSample>(Samples(20));s.Add(s[0]);s.Add(new CalibrationSample(30,0,SaberHand.Left));
        s[1]=new CalibrationSample(1,double.NaN,SaberHand.Right);var r=CalibrationResult.Analyze(s,60);
        Assert.AreEqual(23,r.Captured);Assert.AreEqual(1,r.Missing);
    }
    [Test] public void OutOfRangeProposalIsNotSilentlyClamped()
    { var r=CalibrationResult.Analyze(Samples(30),990);Assert.False(r.CanRecommend);Assert.AreEqual(990,r.ProposedOffsetMs); }
    [Test] public void ChartHasWarmupThenBalancedAlternatingHands()
    {
        var c=CalibrationProtocol.CreateChart();Assert.AreEqual(28,c.notes.Count);
        for(int i=0;i<28;i++) { Assert.AreEqual(CalibrationProtocol.NoteTime(i),c.notes[i].TimeSeconds,.00001);Assert.AreEqual(i%2==0?"blue":"red",c.notes[i].color);Assert.AreEqual(1,c.notes[i].count); }
    }
    [TestCase(44100)] [TestCase(48000)] public void ClickTrackMatchesEveryReferenceBeat(int rate)
    {
        var s=CalibrationProtocol.ClickSamples(rate);Assert.Greater(s.Length,CalibrationProtocol.EndSeconds*rate);
        for(int i=1;i<=32;i++)
        {
            int start=(int)Math.Round(i*.6*rate);Assert.AreEqual(0,s[start-1]);
            Assert.Greater(s.Skip(start).Take((int)(rate*.02)).Max(v=>Math.Abs(v)),.1f);
            Assert.AreEqual(0,s[start+(int)(rate*.06)]);
        }
        Assert.Less(s.Max(v=>Math.Abs(v)),1f);
    }
    [Test] public void TimingGraphicOwnsRendererAndProducesVisibleMesh()
    {
        var go=new GameObject("CalibrationGraphic",typeof(RectTransform));
        try
        {
            var graphic=go.AddComponent<CalibrationTimingGraphic>();Assert.NotNull(go.GetComponent<CanvasRenderer>());
            go.GetComponent<RectTransform>().sizeDelta=new Vector2(830,70);graphic.SetTiming(0,60,false);
            using(var mesh=new UnityEngine.UI.VertexHelper())
            {
                typeof(CalibrationTimingGraphic).GetMethod("OnPopulateMesh",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.DeclaredOnly)
                    .Invoke(graphic,new object[]{mesh});
                Assert.Greater(mesh.currentVertCount,20);
            }
        }
        finally {UnityEngine.Object.DestroyImmediate(go);}
    }
}

public class CalibrationTimingTrailTests
{
    CalibrationTimingGraphic graphic;
    [SetUp] public void Setup()
    {
        var go=new GameObject("TimingTrail",typeof(RectTransform));
        graphic=go.AddComponent<CalibrationTimingGraphic>(); graphic.isDistribution=true;
        graphic.rectTransform.sizeDelta=new Vector2(500,70);
    }
    [TearDown] public void Cleanup() { UnityEngine.Object.DestroyImmediate(graphic.gameObject); }
    static CalibrationResult History(double error) => CalibrationResult.Analyze(new[]{
        new CalibrationSample(0,error,SaberHand.Left),new CalibrationSample(1,error+5,SaberHand.Right)},0);

    [TestCase(-10,-10,.175f)] [TestCase(-10,-10,.5f)]
    [TestCase(10,10,.175f)] [TestCase(10,10,.5f)]
    [TestCase(-10,10,.175f)] [TestCase(-10,10,.5f)]
    public void RepeatedAdjustmentPreservesPointsAndAccumulatesMovement(int first,int second,float elapsed)
    {
        var history=History(-70); graphic.SetResult(history,true,-65);
        graphic.PreviewAdjustment(history,first); graphic.SetResult(null,false,0);
        SetPreviewAge(elapsed);
        var before=PointPositions(); Assert.AreEqual(2,before.Length);
        graphic.PreviewAdjustment(null,second);
        CollectionAssert.AreEqual(before,PointPositions(),"移動中の連打でも点を元の位置に飛ばさない");
        SetPreviewAge(.5f);
        CollectionAssert.AreEqual(new[]{-70f-first-second,-65f-first-second},PointPositions());
        Assert.AreEqual(-70,history.Samples[0].ErrorMs,"表示の移動で測定値を書き換えない");
    }

    [Test] public void NewMeasurementStartsItsOwnTrailAndAdjustment()
    {
        graphic.PreviewAdjustment(History(-70),-10); SetPreviewAge(.5f);
        var fresh=History(30); graphic.SetResult(fresh,true,35);
        CollectionAssert.AreEqual(new[]{30f,35f},PointPositions());
        graphic.PreviewAdjustment(fresh,10); graphic.SetResult(null,false,0); SetPreviewAge(.5f);
        CollectionAssert.AreEqual(new[]{20f,25f},PointPositions(),"前の調整量を新しい測定に重ねない");
        graphic.ClearPreview(); graphic.PreviewAdjustment(null,-10);
        Assert.IsEmpty(PointPositions(),"消去した軌跡を再表示しない");
    }

    [Test] public void RepeatedAdjustmentRenewsPreviewAndExpiryDoesNotRestoreOldPoints()
    {
        graphic.PreviewAdjustment(History(-70),-10); SetPreviewAge(2.9f);
        graphic.PreviewAdjustment(null,-10); TickGraphic();
        Assert.AreEqual(2,PointPositions().Length);
        SetPreviewAge(3.1f); TickGraphic();
        graphic.PreviewAdjustment(null,-10);
        Assert.IsEmpty(PointPositions());
    }

    void SetPreviewAge(float seconds) => typeof(CalibrationTimingGraphic)
        .GetField("previewStarted",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)
        .SetValue(graphic,Time.unscaledTime-seconds);
    void TickGraphic() => typeof(CalibrationTimingGraphic)
        .GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(graphic,null);
    float[] PointPositions()
    {
        using(var mesh=new UnityEngine.UI.VertexHelper())
        {
            typeof(CalibrationTimingGraphic).GetMethod("OnPopulateMesh",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.DeclaredOnly)
                .Invoke(graphic,new object[]{mesh});
            // 帯の四角形3枚のあとに、中心+円周13頂点の点が並ぶ。幅500で1px=1ms。
            const int bandVertices=12,pointVertices=14;
            Assert.Zero((mesh.currentVertCount-bandVertices)%pointVertices);
            var positions=new List<float>(); var vertex=new UIVertex();
            for(int i=bandVertices;i<mesh.currentVertCount;i+=pointVertices)
            { mesh.PopulateUIVertex(ref vertex,i); positions.Add(vertex.position.x); }
            return positions.ToArray();
        }
    }
}
