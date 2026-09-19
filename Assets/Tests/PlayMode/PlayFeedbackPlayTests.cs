using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class PlayFeedbackPlayTests
{
    readonly List<GameObject> created = new List<GameObject>();
    bool reduced;
    string song,difficulty;
    bool calibration;
    [SetUp] public void Remember() { reduced=DisplaySettings.ReducedEffects; song=GameSession.SelectedSongId; difficulty=GameSession.SelectedDifficulty; calibration=GameSession.IsCalibrationMode; }
    [TearDown] public void Cleanup()
    {
        DisplaySettings.SetReducedEffectsForTest(reduced);
        GameSession.SelectedSongId=song; GameSession.SelectedDifficulty=difficulty; GameSession.IsCalibrationMode=calibration;
        foreach(var go in created) if(go!=null) Object.DestroyImmediate(go); created.Clear();
    }
    [UnityTest] public IEnumerator RealGame_LocalMissAndReject_PoolExpires_FullComboAndOutro()
    {
        GameSession.SelectedSongId="揺籠"; GameSession.SelectedDifficulty="normal"; GameSession.IsCalibrationMode=false;
        yield return SceneManager.LoadSceneAsync("Game");
        GamePlayManager manager=null; GameplayFeedbackPresenter feedback=null;
        float limit=Time.realtimeSinceStartup+30;
        while(Time.realtimeSinceStartup<limit)
        {
            manager=Object.FindFirstObjectByType<GamePlayManager>(); feedback=Object.FindFirstObjectByType<GameplayFeedbackPresenter>();
            if(feedback!=null && manager.songPlayer.IsScheduled) break;
            yield return null;
        }
        Assert.IsNotNull(feedback); Assert.IsTrue(Object.FindFirstObjectByType<GameHUDSkin>().UseLocalJudgments);
        manager.enabled=false;
        foreach(var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous=false;
        manager.noteSpawner.SetChart(new ChartData()); manager.scoreManager.Reset();
        foreach(var source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) source.Stop();
        DisplaySettings.SetReducedEffectsForTest(true);
        for(int i=0;i<12;i++)
        {
            var go=new GameObject("feedback-note"); created.Add(go); go.transform.position=new Vector3((i%4-1.5f)*1.3f,0,0);
            var note=go.AddComponent<CuttableNote>(); note.IsJudgeable=true; feedback.Track(note);
            if(i%2==0) note.NotifyRejected(CutRejectionReason.Direction,note.transform.position);
            else note.MarkMiss();
        }
        Assert.That(feedback.ActivePopupCount,Is.InRange(1,GameplayFeedbackPresenter.PopupCapacity));
        Assert.AreEqual(GameplayFeedbackPresenter.PopupCapacity,feedback.GetComponentsInChildren<GameplayFeedbackGlyph>(true).Length,"プールは8個から増えない");
        yield return null; Canvas.ForceUpdateCanvases();
        var glyphs=feedback.GetComponentsInChildren<GameplayFeedbackGlyph>();
        Assert.AreEqual(feedback.ActivePopupCount,glyphs.Length,"全表示に記号のコンポーネントが必要");
        foreach(var glyph in glyphs)
        {
            var mesh=glyph.canvasRenderer.GetMesh();
            Assert.IsNotNull(mesh,"記号メッシュが未生成");
            Assert.GreaterOrEqual(mesh.vertexCount,4,"Missの×／矢印が実際に描画される");
        }
        for(int i=0;i<glyphs.Length;i++)for(int j=i+1;j<glyphs.Length;j++)
        {
            var a=((RectTransform)glyphs[i].transform.parent).anchoredPosition;
            var b=((RectTransform)glyphs[j].transform.parent).anchoredPosition;
            Assert.IsTrue(Mathf.Abs(a.x-b.x)>=236 || Mathf.Abs(a.y-b.y)>=72,"密集したラベルが画面端で重ならない");
        }
        for(int i=0;i<9;i++) feedback.Tick(.1f,1,10,8);
        Assert.AreEqual(0,feedback.ActivePopupCount,"控えめ設定でも期限で消える");
        for(int i=0;i<50;i++) manager.scoreManager.RegisterHit(JudgmentTier.Good);
        Assert.AreEqual(50,feedback.LatestMilestone); Assert.AreEqual("FC ACTIVE",feedback.FullComboLabel);
        manager.scoreManager.RegisterHit(JudgmentTier.Bad);
        Assert.AreEqual("FC LOST",feedback.FullComboLabel,"BadでもFCは失われる");
        manager.scoreManager.RegisterHit(JudgmentTier.Perfect); Assert.AreEqual("FC LOST",feedback.FullComboLabel);
        feedback.BeginOutro(); feedback.Tick(.1f,10,10,8);
        Assert.AreEqual("TRACK CLEAR",feedback.EndingLabel); Assert.IsTrue(feedback.OutroStarted);
        Assert.AreEqual(0,feedback.ActivePopupCount);
        yield return SceneManager.LoadSceneAsync("SongSelect");
        Assert.IsNull(Object.FindFirstObjectByType<GameplayFeedbackPresenter>(),"シーン終了時にプールと購読を回収");
    }

    [UnityTest] public IEnumerator SongSelect_LowEffectsButtonIsReadableClickableAndCuttable()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        float until=Time.realtimeSinceStartup+10;
        GameObject button=null;
        while(button==null && Time.realtimeSinceStartup<until) { button=GameObject.Find("ReducedEffectsButton"); yield return null; }
        Assert.IsNotNull(button); Assert.IsNotNull(button.GetComponent<MenuNoteAction>());
        var projection=GameObject.Find(ProjectorModeToggleUI.ButtonName).GetComponent<RectTransform>();
        var rect=button.GetComponent<RectTransform>();
        Assert.Greater(rect.anchoredPosition.x-rect.sizeDelta.x/2,projection.anchoredPosition.x+projection.sizeDelta.x/2,"投影設定の隣に重ならず並ぶ");
        // 実ユーザーのPlayerPrefsは退避し、永続化の往復も検証する。
        bool existed=PlayerPrefs.HasKey("displayReducedEffects"); int before=PlayerPrefs.GetInt("displayReducedEffects",0);
        try
        {
            bool start=DisplaySettings.ReducedEffects;
            button.GetComponent<Button>().onClick.Invoke();
            Assert.AreEqual(!start,DisplaySettings.ReducedEffects);
            DisplaySettings.ResetReducedEffectsCacheForTest();
            Assert.AreEqual(!start,DisplaySettings.ReducedEffects,"次回起動用の設定保存");
            string label=button.transform.Find("ActionLabel").GetComponent<TMPro.TextMeshProUGUI>().text;
            Assert.AreEqual(ProjectorModeToggleUI.EffectsLabelFor(!start),label);
        }
        finally
        {
            if(existed) PlayerPrefs.SetInt("displayReducedEffects",before); else PlayerPrefs.DeleteKey("displayReducedEffects");
            PlayerPrefs.Save(); DisplaySettings.SetReducedEffectsForTest(reduced);
        }
        yield return null;
    }

    [UnityTest] public IEnumerator PerfectOnlyAccent_RemainsExclusive_AndLowModeReducesGeometry()
    {
        var owner=new GameObject("spawner"); created.Add(owner); var spawner=owner.AddComponent<NoteSpawner>();
        var effect=GameplayCutFeedback.Create(spawner);
        var notify=typeof(CuttableNote).GetMethod("NotifyJudgment",BindingFlags.Instance|BindingFlags.NonPublic);
        var isCut=typeof(CuttableNote).GetProperty("IsCut");
        foreach(var tier in new[]{JudgmentTier.Great,JudgmentTier.Good,JudgmentTier.Bad,JudgmentTier.Miss})
        {
            var go=new GameObject("note"); created.Add(go); var note=go.AddComponent<CuttableNote>();
            isCut.SetValue(note,true); effect.Track(note); notify.Invoke(note,new object[]{tier,Vector3.zero,Vector3.right*5});
        }
        Assert.AreEqual(0,effect.ActiveCount);
        var perfectGo=new GameObject("perfect"); created.Add(perfectGo); var perfect=perfectGo.AddComponent<CuttableNote>();
        isCut.SetValue(perfect,true); effect.Track(perfect);
        notify.Invoke(perfect,new object[]{JudgmentTier.Perfect,Vector3.zero,Vector3.right*5});
        DisplaySettings.SetReducedEffectsForTest(false); effect.Tick(.01f);
        int normal=effect.GetComponent<MeshFilter>().sharedMesh.vertexCount;
        DisplaySettings.SetReducedEffectsForTest(true); effect.Tick(0);
        int low=effect.GetComponent<MeshFilter>().sharedMesh.vertexCount;
        Assert.Greater(normal,low); Assert.Greater(low,0); Assert.AreEqual(1,effect.ActiveCount);
        yield return null;
    }
}
