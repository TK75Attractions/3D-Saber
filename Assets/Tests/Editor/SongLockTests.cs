using NUnit.Framework;
using UnityEngine;

// 譜面未制作の曲を曲選択でロック(選べない)する仕組みの検証。
public class SongLockTests
{
    [Test]
    public void HasPlayableChart_ElDorado_IsPlayable()
    {
        Assert.IsTrue(SongSelectController.HasPlayableChart(
            "ElDorado", new[] { "Easy", "Normal", "Hard" }));
    }

    [Test]
    public void HasPlayableChart_EmptyChartSong_IsLocked()
    {
        // 揺籠は空chart.jsonのみ → ロック
        Assert.IsFalse(SongSelectController.HasPlayableChart(
            "揺籠", new[] { "Easy", "Normal", "Hard" }));
    }

    [Test]
    public void HasPlayableChart_UnknownSong_IsLocked()
    {
        Assert.IsFalse(SongSelectController.HasPlayableChart(
            "__no_such_song__", new[] { "Normal" }));
    }

    [Test]
    public void Controller_LocksEmptySong_AndBlocksStart()
    {
        var go = new GameObject("songSelect");
        try
        {
            var ctl = go.AddComponent<SongSelectController>();
            ctl.Populate();

            int lockedIdx = -1, playableIdx = -1;
            for (int i = 0; i < 100; i++)
            {
                // songIds は非公開なので IsLocked の走査で判定する
                if (ctl.IsLocked(i)) { if (lockedIdx < 0) lockedIdx = i; }
            }
            // 揺籠(空譜面)がロックされているはず
            Assert.GreaterOrEqual(lockedIdx, 0, "空譜面の曲がロックされている");

            // ロック曲を選択して StartGame してもシーン遷移・セッション変更が起きない
            GameSession.SelectedSongId = "__sentinel__";
            ctl.Select(lockedIdx);
            Assert.IsTrue(ctl.SelectedSongLocked);
            ctl.StartGame();
            Assert.AreEqual("__sentinel__", GameSession.SelectedSongId, "ロック曲は開始できない");

            // 参考: ElDorado(実譜面あり)はロックされない
            for (int i = 0; i < 100; i++)
            {
                if (!ctl.IsLocked(i) && i != lockedIdx) { playableIdx = i; break; }
            }
            Assert.GreaterOrEqual(playableIdx, 0, "遊べる曲も存在する");
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
