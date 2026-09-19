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
    [UnityTest] public IEnumerator RealGame_PreviousJudgmentDisplay_FullComboAndOutro()
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
        Assert.IsNotNull(feedback);
        var hud=Object.FindFirstObjectByType<GameHUDSkin>(); Assert.IsNotNull(hud); Assert.IsTrue(hud.IsBuilt);
        manager.enabled=false;
        foreach(var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous=false;
        manager.noteSpawner.SetChart(new ChartData()); manager.scoreManager.Reset();
        foreach(var source in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None)) source.Stop();
        DisplaySettings.SetReducedEffectsForTest(true);
        // 従来の画面下の判定に戻り、ノーツ付近の札を作らない。
        var tierLabel=hud.transform.Find("TierText").GetComponent<TMPro.TextMeshProUGUI>();
        foreach(var tier in new[]{JudgmentTier.Perfect,JudgmentTier.Great,JudgmentTier.Good,JudgmentTier.Bad})
        {
            manager.scoreManager.RegisterHit(tier);
            Assert.AreEqual(JudgmentTierHelper.Label(tier),tierLabel.text);
        }
        manager.scoreManager.RegisterMiss();
        Assert.AreEqual("MISS",tierLabel.text);
        yield return null;
        Assert.That(tierLabel.rectTransform.anchoredPosition.y,Is.EqualTo(-330f).Within(.01f));
        Assert.Greater(tierLabel.color.a,0);
        Assert.IsNull(feedback.transform.Find("Judgment0"),"ノーツ付近の説明札を生成しない");
        manager.scoreManager.Reset();
        for(int i=0;i<50;i++) manager.scoreManager.RegisterHit(JudgmentTier.Good);
        Assert.AreEqual(50,feedback.LatestMilestone); Assert.AreEqual("FC ACTIVE",feedback.FullComboLabel);
        manager.scoreManager.RegisterHit(JudgmentTier.Bad);
        Assert.AreEqual("FC LOST",feedback.FullComboLabel,"BadでもFCは失われる");
        manager.scoreManager.RegisterHit(JudgmentTier.Perfect); Assert.AreEqual("FC LOST",feedback.FullComboLabel);
        feedback.BeginOutro(); feedback.Tick(.1f,10,10,8);
        Assert.AreEqual("TRACK CLEAR",feedback.EndingLabel); Assert.IsTrue(feedback.OutroStarted);
        yield return SceneManager.LoadSceneAsync("SongSelect");
        Assert.IsNull(Object.FindFirstObjectByType<GameplayFeedbackPresenter>(),"シーン終了時に演出と購読を回収");
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
