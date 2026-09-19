using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class PlayFeedbackTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    GameObject Make(string name) { var go = new GameObject(name); objects.Add(go); return go; }
    [TearDown] public void Cleanup() { foreach (var go in objects) if (go != null) Object.DestroyImmediate(go); objects.Clear(); }

    [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
    public void RejectedPass_ExplainsHandOrSpeedWithoutCutting(bool blade, bool slow)
    {
        var note = Make("note").AddComponent<CuttableNote>(); note.IsJudgeable = true;
        note.RequiredHand = SaberHand.Left;
        var rig = Make("rig"); var tracker = rig.AddComponent<SaberTracker>();
        var judge = rig.AddComponent<SaberCutJudge>(); judge.saber = tracker;
        judge.hand = slow ? SaberHand.Left : SaberHand.Right; judge.bladeRadius = .2f; judge.noteHitRadiusXY = .3f;
        SaberInputBridge bridge = null;
        if (blade) { bridge = rig.AddComponent<SaberInputBridge>(); judge.bladeProvider = bridge; }
        int notices = 0; CutRejectionReason reason = default;
        note.OnRejected += (n,r,p) => { notices++; reason = r; };
        tracker.ResetTo(new Vector3(-1, 0, 0));
        tracker.Tick(Vector3.zero, slow ? .5f : .1f);
        if (blade) bridge.OverrideBlade(new Vector3(0,-.6f,0), new Vector3(0,.6f,0));
        judge.TryCut(); Assert.AreEqual(0, notices);
        tracker.Tick(new Vector3(1,0,0), slow ? .5f : .1f);
        if (blade) bridge.OverrideBlade(new Vector3(1,-.6f,0), new Vector3(1,.6f,0));
        judge.TryCut();
        Assert.AreEqual(1, notices); Assert.AreEqual(slow ? CutRejectionReason.Speed : CutRejectionReason.Hand, reason);
        Assert.IsFalse(note.IsCut); Assert.IsFalse(note.IsMissed); Assert.AreEqual(0, note.CutsAchieved);
        judge.TryCut(); Assert.AreEqual(1, notices, "静止状態で警告を繰り返さない");
    }

    [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
    public void ReturningStationaryWarpedExpiredOrResetContact_DoesNotWarn(int situation)
    {
        var note = Make("note").AddComponent<CuttableNote>(); note.IsJudgeable = true; note.RequiredHand = SaberHand.Left;
        var rig = Make("rig"); var tracker = rig.AddComponent<SaberTracker>(); var judge = rig.AddComponent<SaberCutJudge>();
        judge.saber = tracker; judge.hand = SaberHand.Right; int notices = 0;
        note.OnRejected += (n,r,p) => notices++;
        tracker.ResetTo(new Vector3(-1,0,0)); tracker.Tick(Vector3.zero,.1f); judge.TryCut();
        if (situation == 3) note.IsJudgeable = false;
        if (situation == 4) tracker.ResetTo(Vector3.zero);
        tracker.Tick(new Vector3(situation == 0 ? -1 : situation == 1 ? 0 : situation == 2 ? 9 : 1,0,0), .1f);
        judge.TryCut(); Assert.AreEqual(0, notices); Assert.IsFalse(note.IsCut);
    }

    [Test] public void RecoveredSpeed_CutsWithoutStaleWarning()
    {
        var note = Make("note").AddComponent<CuttableNote>(); note.IsJudgeable = true; note.RequiredCutCount = note.RemainingCuts = 2;
        var rig = Make("rig"); var tracker = rig.AddComponent<SaberTracker>(); var judge = rig.AddComponent<SaberCutJudge>(); judge.saber = tracker;
        int notices=0; note.OnRejected += (n,r,p) => notices++;
        tracker.ResetTo(new Vector3(-1,0,0)); tracker.Tick(Vector3.zero,.5f); judge.TryCut();
        tracker.Tick(new Vector3(1,0,0),.1f); judge.TryCut();
        Assert.AreEqual(1, note.CutsAchieved); Assert.AreEqual(0,notices);
    }

    [Test] public void DirectionRejection_IsExplanatoryAndThrottled()
    {
        var note = Make("note").AddComponent<CuttableNote>(); note.IsJudgeable = true; note.RequiredDirection = CutDirection.Up;
        int notices=0; note.OnRejected += (n,r,p) => { notices++; Assert.AreEqual(CutRejectionReason.Direction,r); };
        note.Cut(Vector3.zero,Vector3.down * 5); note.Cut(Vector3.zero,Vector3.down * 5);
        Assert.AreEqual(1,notices); Assert.IsFalse(note.IsCut); Assert.AreEqual(CutDirection.Up,note.RequiredDirection);
    }
    [Test] public void DirectionHintRescue_HasNoRejection()
    {
        var note = Make("note").AddComponent<CuttableNote>(); note.IsJudgeable = true; note.RequiredDirection = CutDirection.Up;
        note.RequiredCutCount = note.RemainingCuts = 2;
        int notices=0; note.OnRejected += (n,r,p) => notices++;
        note.Cut(Vector3.zero,Vector3.down * 5,CutDirection.Up);
        Assert.AreEqual(0,notices); Assert.AreEqual(1,note.CutsAchieved);
    }
    [TestCase(0,0,0,false)] [TestCase(1,0,0,true)] [TestCase(50,1,0,false)] [TestCase(50,0,1,false)]
    public void FullCombo_FollowsBadAndMissRules(int hits,int bad,int miss,bool expected)
        => Assert.AreEqual(expected,GameplayFeedbackPresenter.FullComboEligible(hits,bad,miss));
    [TestCase(0,false)] [TestCase(49,false)] [TestCase(50,true)] [TestCase(100,true)] [TestCase(200,true)]
    public void Milestones(int combo,bool expected) => Assert.AreEqual(expected,GameplayFeedbackPresenter.IsMilestone(combo));
    [TestCase(9,10,8,10,10,10,false)] [TestCase(10,10,8,10,10,10,true)]
    [TestCase(10,10,12,10,10,10,false)] [TestCase(12,10,12,10,9,10,false)]
    [TestCase(12,10,12,10,10,9,false)] [TestCase(12,10,12,10,10,10,true)]
    [TestCase(12,10,12,0,0,0,false)]
    public void Outro_WaitsForMusicAndEveryJudgment(double time,double duration,double last,int total,int judged,int spawned,bool expected)
        => Assert.AreEqual(expected,GameplayFeedbackPresenter.CanEnd(time,duration,last,total,judged,spawned));
    [TestCase(0,0)] [TestCase(1,1)] [TestCase(.5f,.5f)]
    public void Popup_RemainsInSafeViewport(float x,float y)
    {
        var p=GameplayFeedbackPresenter.PopupPosition(new Vector2(x,y),new Vector2(1280,720));
        Assert.That(p.x,Is.InRange(-508,508)); Assert.That(p.y,Is.InRange(-300,140));
    }
}
