using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class SongStagePerformancePlayTests
{
    [UnityTest]
    public IEnumerator ActualGameLoadsEachSongAndDrivesItsHighlightFromAudioClock()
    {
        string[] songs={"2_23_AM","ElDorado","Epilogue","Morning","揺籠"};
        string[] difficulties={"easy","normal","hard"};
        var clock=typeof(SongPlayer).GetField("startDspTime",BindingFlags.Instance|BindingFlags.NonPublic);
        try
        {
            for(int i=0;i<songs.Length;i++)
            {
                GameSession.SelectedSongId=songs[i]; GameSession.SelectedDifficulty=difficulties[i%3]; GameSession.IsCalibrationMode=false;
                yield return SceneManager.LoadSceneAsync("Game",LoadSceneMode.Single);
                var manager=Object.FindFirstObjectByType<GamePlayManager>();
                float deadline=Time.realtimeSinceStartup+25;
                while(Time.realtimeSinceStartup<deadline && (!manager.songPlayer.IsPlaying || manager.songPlayer.SongTime<.2)) yield return null;
                Assert.IsTrue(manager.songPlayer.IsPlaying,songs[i]);
                var stage=Object.FindFirstObjectByType<FloorRenderer>(); Assert.IsNotNull(stage);
                Assert.AreEqual(0,stage.ChorusIntensity);
                var timeline=StagePerformanceTimeline.Load(songs[i]); var section=timeline.sections[0];
                Assert.IsNotNull(manager.songPlayer.Clip,songs[i]);
                foreach(var s in timeline.sections) Assert.Less(s.endSeconds,manager.songPlayer.Duration,songs[i]);
                double peak=(section.startSeconds+section.endSeconds)/2;
                // 数分待つ代わりに、テスト内だけ実プレイヤーのDSP基準時刻を進める。
                clock.SetValue(manager.songPlayer,AudioSettings.dspTime-peak);
                yield return null; yield return null;
                Assert.That(stage.ChorusIntensity,Is.EqualTo(timeline.Evaluate(manager.songPlayer.SongTime)).Within(.01),songs[i]);
                Assert.Greater(stage.ChorusIntensity,.6f,songs[i]);
                manager.songPlayer.Stop(); double stopped=stage.LastTickSeconds; float strength=stage.ChorusIntensity;
                yield return new WaitForSeconds(.05f);
                Assert.AreEqual(stopped,stage.LastTickSeconds); Assert.AreEqual(strength,stage.ChorusIntensity);
            }
        }
        finally { GameSession.SelectedSongId="ElDorado"; GameSession.SelectedDifficulty="normal"; }
    }
}
